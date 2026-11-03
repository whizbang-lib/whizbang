// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Component.Tests.Schema;

/// <summary>
/// The schema initializer is one runner for every driver: each driver registers what it initializes, and the
/// initializer runs every registration in order, then opens the schema-ready gate, exactly once.
/// </summary>
/// <docs>data/turnkey-initialization</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/WhizbangDatabaseInitializerService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/ISchemaInitializationRunner.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaInitializationRegistration.cs</code-under-test>
public class SchemaInitializationRunnersTests {

  [Test]
  public async Task Start_RunsEveryRegisteredRunnerInOrder_ThenOpensTheGateAsync() {
    var gate = new SchemaReadyGate();
    var order = new List<string>();
    var first = new RecordingRunner("first", order, gate);
    var second = new RecordingRunner("second", order, gate);
    var service = _create(gate, first, second);

    await service.StartAsync(CancellationToken.None);

    await Assert.That(order).IsEquivalentTo(["first", "second"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(first.GateWasOpenWhenItRan || second.GateWasOpenWhenItRan).IsFalse()
      .Because("the gate opens after the last runner, so nothing reads a schema a later runner has not finished");
    await Assert.That(gate.IsReady).IsTrue();
  }

  [Test]
  public async Task Start_WhenARunnerFails_TheLaterOnesDoNotRun_AndTheGateStaysClosedAsync() {
    var gate = new SchemaReadyGate();
    var order = new List<string>();
    var failing = new RecordingRunner("failing", order, gate, new InvalidOperationException("migration failed"));
    var later = new RecordingRunner("later", order, gate);
    var service = _create(gate, failing, later);

    await Assert.That(async () => await service.StartAsync(CancellationToken.None))
      .ThrowsExactly<InvalidOperationException>();

    await Assert.That(order).IsEquivalentTo(["failing"]);
    await Assert.That(gate.IsReady).IsFalse();
  }

  [Test]
  public async Task Background_AFailedAttempt_IsReportedToEveryObserver_ThenRetriedAsync() {
    var gate = new SchemaReadyGate();
    var attempts = 0;
    var flaky = new FlakyRunner(() => ++attempts == 1);
    var watching = new FailureRecorder();
    var service = new WhizbangDatabaseInitializerService(
      new ServiceCollection().BuildServiceProvider(),
      [flaky],
      gate,
      Options.Create(new ClaimWorkerOptions()),
      Options.Create(new SchemaInitializationOptions { NonBlockingSchemaInit = true, InitRetryDelay = TimeSpan.Zero }),
      TimeProvider.System,
      NullLogger<WhizbangDatabaseInitializerService>.Instance,
      [new SilentObserver(), watching]);

    await service.StartAsync(CancellationToken.None);
    await service.BackgroundInitTask!;

    await Assert.That(watching.Attempts).IsEquivalentTo([1]);
    await Assert.That(gate.IsReady).IsTrue();
  }

  [Test]
  public async Task Registration_OpensTheRegisteredGate_WhenNoDriverRegisteredAnythingAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangSchemaInitialization();
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);

    await using var provider = services.BuildServiceProvider();
    var initializer = provider.GetServices<IHostedService>().OfType<WhizbangDatabaseInitializerService>().Single();
    await initializer.StartAsync(CancellationToken.None);

    await Assert.That(provider.GetRequiredService<ISchemaReadyGate>().IsReady).IsTrue()
      .Because("the gate the initializer opens is the registered one the workers wait on");
  }

  [Test]
  public async Task Start_EntersCoordinate_AfterTheLastRunner_BeforeTheGateOpensAsync() {
    var gate = new SchemaReadyGate();
    var order = new List<string>();
    var stages = new StageRecorder(order, gate);
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<Whizbang.Core.Startup.IStartupStageReporter>(stages);
    var service = new WhizbangDatabaseInitializerService(
      services.BuildServiceProvider(),
      [new RecordingRunner("runner", order, gate)],
      gate,
      Options.Create(new ClaimWorkerOptions()),
      Options.Create(new SchemaInitializationOptions { NonBlockingSchemaInit = false }),
      new FakeTimeProvider(),
      NullLogger<WhizbangDatabaseInitializerService>.Instance,
      []);

    await service.StartAsync(CancellationToken.None);

    await Assert.That(order).IsEquivalentTo(
      ["runner", "Coordinate: recomputing partitions before the schema-ready gate opens"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("Coordinate (#1636) follows every driver's schema work and precedes the gate the data plane waits on");
    await Assert.That(stages.GateWasOpen).IsFalse();
    await Assert.That(gate.IsReady).IsTrue();
  }

  [Test]
  public async Task StandDown_RunsNoRunner_KeepsTheGateClosed_AndRegistersTheInstanceVisiblyAsync() {
    var gate = new SchemaReadyGate();
    var order = new List<string>();
    var coordinator = new BeatingCoordinator();
    var stages = new StageLog();
    var service = _create(gate, new StartupAssessment(StartupVerdict.StandDown,
      "the ledger records version 2.0.0, newer than this binary's 1.0.0"), coordinator, stages,
      new RecordingRunner("runner", order, gate));

    await service.StartAsync(CancellationToken.None);
    await coordinator.FirstBeat.Task;
    await service.StopAsync(CancellationToken.None);
    await service.StandDownTask!;

    await Assert.That(order).IsEmpty()
      .Because("a stood-down instance applies nothing: no bootstrap, no election, no pass, no reconcile");
    await Assert.That(gate.IsReady).IsFalse()
      .Because("the schema-ready gate stays closed, so the data plane never starts and readiness stays 503");
    await Assert.That(stages.Calls).Contains(
      "Enter Preflight: standing down: the ledger records version 2.0.0, newer than this binary's 1.0.0");
    await Assert.That(stages.Calls).Contains("Phase StandingBy");
    await Assert.That(stages.Calls).Contains("Registered")
      .Because("the instance registers at once, so the state it recorded is visible on its row");
  }

  [Test]
  [Arguments(StartupVerdict.Migrate)]
  [Arguments(StartupVerdict.Serve)]
  [Arguments(StartupVerdict.Current)]
  public async Task AnyOtherVerdict_RunsTheRunnersAfterTheVerdict_ThenOpensTheGateAsync(StartupVerdict verdict) {
    var gate = new SchemaReadyGate();
    var order = new List<string>();
    var assessor = new GatedAssessor(new StartupAssessment(verdict, "clear"));
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IStartupAssessor>(assessor);
    var provider = services.BuildServiceProvider();
    var source = provider.GetRequiredService<StartupAssessmentSource>();
    var runner = new VerdictReadingRunner(source, order);
    var service = _create(provider, gate, runner);

    var starting = service.StartAsync(CancellationToken.None);
    await assessor.Asked.Task;
    await Assert.That(order).IsEmpty()
      .Because("Assess is the first stage: no runner starts before the one verdict exists");
    assessor.Release();
    await starting;

    await Assert.That(order).IsEquivalentTo([verdict.ToString()])
      .Because("the runner reads the verdict the initializer produced, not a second answer");
    await Assert.That(assessor.Calls).IsEqualTo(1);
    await Assert.That(gate.IsReady).IsTrue();
  }

  [Test]
  public async Task AnUnreadableVersionRecord_FailsTheAttemptLoudly_BeforeAnyRunnerAsync() {
    var gate = new SchemaReadyGate();
    var order = new List<string>();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IStartupAssessor>(new ThrowingAssessor(new InvalidOperationException("permission denied")));
    var service = _create(services.BuildServiceProvider(), gate, new RecordingRunner("runner", order, gate));

    await Assert.That(async () => await service.StartAsync(CancellationToken.None))
      .ThrowsExactly<StartupAssessmentException>()
      .Because("never migrating blind: a record that cannot be read fails startup like a bootstrap that cannot be applied");
    await Assert.That(order).IsEmpty();
    await Assert.That(gate.IsReady).IsFalse();
  }

  private static WhizbangDatabaseInitializerService _create(
      ISchemaReadyGate gate, StartupAssessment assessment, BeatingCoordinator coordinator, StageLog stages,
      params ISchemaInitializationRunner[] runners) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IStartupAssessor>(new FixedAssessor(assessment));
    services.AddSingleton<IStartupStageReporter>(stages);
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(new FixedInstance());
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    return _create(services.BuildServiceProvider(), gate, runners);
  }

  private static WhizbangDatabaseInitializerService _create(
      IServiceProvider provider, ISchemaReadyGate gate, params ISchemaInitializationRunner[] runners) =>
    new(
      provider,
      runners,
      gate,
      Options.Create(new ClaimWorkerOptions()),
      Options.Create(new SchemaInitializationOptions { NonBlockingSchemaInit = false }),
      new FakeTimeProvider(),
      NullLogger<WhizbangDatabaseInitializerService>.Instance,
      []);

  private sealed class FixedAssessor(StartupAssessment assessment) : IStartupAssessor {
    public Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) => Task.FromResult(assessment);
  }

  private sealed class ThrowingAssessor(Exception failure) : IStartupAssessor {
    public Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) => Task.FromException<StartupAssessment>(failure);
  }

  /// <summary>Holds its answer until released, and says when it was asked.</summary>
  private sealed class GatedAssessor(StartupAssessment assessment) : IStartupAssessor {
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Calls { get; private set; }

    public void Release() => _release.SetResult();

    public async Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) {
      Calls++;
      Asked.TrySetResult();
      await _release.Task;
      return assessment;
    }
  }

  private sealed class VerdictReadingRunner(StartupAssessmentSource source, List<string> order) : ISchemaInitializationRunner {
    public async Task RunAsync(CancellationToken cancellationToken) =>
      order.Add((await source.AssessAsync(cancellationToken)).Verdict.ToString());
  }

  private sealed class StageLog : IStartupStageReporter {
    private readonly Lock _lock = new();
    private readonly List<string> _calls = [];
    public IReadOnlyList<string> Calls { get { lock (_lock) { return [.. _calls]; } } }
    public StartupStageSnapshot? Current => null;

    public ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default) => _add($"Enter {stage}: {message}");
    public ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default) => _add($"Skip {stage}: {reason}");
    public ValueTask EnterPhaseAsync(Whizbang.Core.RunControl.LifecyclePhase phase, CancellationToken cancellationToken = default) => _add($"Phase {phase}");
    public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) => _add("Registered");

    private ValueTask _add(string call) {
      lock (_lock) {
        _calls.Add(call);
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FixedInstance : Whizbang.Core.Observability.IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "orders";
    public string HostName => "host-1";
    public int ProcessId => 42;
    public Whizbang.Core.Observability.ServiceInstanceInfo ToInfo() => throw new NotSupportedException();
  }

  /// <summary>Accepts every heartbeat; says when the first one landed.</summary>
  private sealed class BeatingCoordinator : IWorkCoordinator {
    public TaskCompletionSource FirstBeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool RecordsInstanceTransitions => true;

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      FirstBeat.TrySetResult();
      return Task.FromResult(true);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }

  /// <summary>Records each stage entered, and whether the schema-ready gate was already open then.</summary>
  private sealed class StageRecorder(List<string> order, ISchemaReadyGate gate) : Whizbang.Core.Startup.IStartupStageReporter {
    public bool GateWasOpen { get; private set; }
    public Whizbang.Core.Startup.StartupStageSnapshot? Current => null;

    public ValueTask EnterAsync(Whizbang.Core.Startup.StartupStage stage, string message, CancellationToken cancellationToken = default) {
      order.Add($"{stage}: {message}");
      GateWasOpen |= gate.IsReady;
      return ValueTask.CompletedTask;
    }

    public ValueTask SkipAsync(Whizbang.Core.Startup.StartupStage stage, string reason, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  private static WhizbangDatabaseInitializerService _create(ISchemaReadyGate gate, params ISchemaInitializationRunner[] runners) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    return new WhizbangDatabaseInitializerService(
      services.BuildServiceProvider(),
      runners,
      gate,
      Options.Create(new ClaimWorkerOptions()),
      Options.Create(new SchemaInitializationOptions { NonBlockingSchemaInit = false }),
      TimeProvider.System,
      NullLogger<WhizbangDatabaseInitializerService>.Instance,
      []);
  }

  private sealed class FlakyRunner(Func<bool> fails) : ISchemaInitializationRunner {
    public Task RunAsync(CancellationToken cancellationToken) =>
      fails() ? Task.FromException(new InvalidOperationException("the database is not up yet")) : Task.CompletedTask;
  }

  /// <summary>Watches nothing; every notification takes its default.</summary>
  private sealed class SilentObserver : ISchemaInitializationObserver;

  private sealed class FailureRecorder : ISchemaInitializationObserver {
    public List<int> Attempts { get; } = [];

    public ValueTask OnAttemptFailedAsync(int attempt, Exception exception, CancellationToken cancellationToken) {
      Attempts.Add(attempt);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class RecordingRunner(string name, List<string> order, ISchemaReadyGate gate, Exception? failure = null)
    : ISchemaInitializationRunner {
    public bool GateWasOpenWhenItRan { get; private set; }

    public Task RunAsync(CancellationToken cancellationToken) {
      GateWasOpenWhenItRan = gate.IsReady;
      order.Add(name);
      return failure is null ? Task.CompletedTask : Task.FromException(failure);
    }
  }
}
