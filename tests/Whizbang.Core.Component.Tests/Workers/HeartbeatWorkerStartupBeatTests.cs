// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.Startup;
using Whizbang.Core.Tests.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The heartbeat starts at registration, not at schema-ready (#2126). Until the schema-ready gate opens, a beat goes
/// through <see cref="IWorkCoordinator.RecordStartupHeartbeatAsync"/>, which needs only what Preflight creates and
/// carries the instance's connection mode and liveness window; it writes no stage and announces no join, because the
/// instance is not yet part of the working fleet. The beat after the gate opens is due at once and is the full
/// heartbeat, which registers, reaps and announces as before.
/// </summary>
/// <remarks>
/// The clock is a <see cref="FakeTimeProvider"/> that is never advanced unless a test says so, so every beat a test
/// sees was caused by the plan or by the gate, never by time passing.
/// </remarks>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <code-under-test>src/Whizbang.Core/Workers/HeartbeatWorker.cs</code-under-test>
[Category("Workers")]
public class HeartbeatWorkerStartupBeatTests {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);
  private static readonly HeartbeatWorkerOptions _options = new() { IntervalSeconds = 30, SlowIntervalSeconds = 60 };

  [Test]
  public async Task BeforeTheSchemaIsReady_ABeat_GoesThroughTheStartupHeartbeat_WithTheModeAndTheWindowAsync() {
    var fixture = new Fixture(new SchemaReadyGate());

    await fixture.Worker.TickForTestsAsync(CancellationToken.None);

    await Assert.That(fixture.Coordinator.FullBeats).IsEqualTo(0)
      .Because("the full heartbeat needs the migrated schema; a starting instance beats with what Preflight created");
    await Assert.That(fixture.Coordinator.StartupRequests.Count).IsEqualTo(1);
    var beat = fixture.Coordinator.StartupRequests[0];
    await Assert.That(beat.ConnectionMode).IsEqualTo(InstanceConnectionMode.Direct);
    await Assert.That(beat.StaleThresholdSeconds).IsEqualTo(HeartbeatLivenessThreshold.StaleThresholdSeconds(_options))
      .Because("the rule judges a starting instance by the window its own cadence implies, not by 30 seconds");
    await Assert.That(beat.InstanceId).IsEqualTo(fixture.Instance.InstanceId);
  }

  [Test]
  public async Task BeforeTheSchemaIsReady_AnAcceptedBeat_IsRecorded_ButWritesNoStageAndAnnouncesNoJoinAsync() {
    var fixture = new Fixture(new SchemaReadyGate());
    var recorded = 0;
    fixture.Worker.OnHeartbeatRecorded += () => recorded++;

    await fixture.Worker.TickForTestsAsync(CancellationToken.None);

    await Assert.That(recorded).IsEqualTo(1);
    await Assert.That(fixture.Worker.LastAcceptedAt).IsEqualTo(Fixture.Start);
    await Assert.That(fixture.Stages.Calls).IsEmpty()
      .Because("a startup beat does not prove the row exists, and the stage record is the one writer of the stage");
    await Assert.That(fixture.Bus.Published).IsEmpty()
      .Because("the instance joins the working fleet once its schema is ready, not while it is still migrating");
  }

  [Test]
  [Timeout(60000)]
  public async Task BeforeTheSchemaIsReady_ARefusedBeat_EndsTheLoopAsEvictedAsync(CancellationToken cancellationToken) {
    var fixture = new Fixture(new SchemaReadyGate(), startupAccepted: false);

    await fixture.Worker.StartAsync(cancellationToken);
    await fixture.Worker.ExecuteTask!.WaitAsync(_deadline, cancellationToken);

    await Assert.That(fixture.Coordinator.StartupRequests.Count).IsEqualTo(1)
      .Because("an evicted instance learns it from its first refused beat and stops asking");
    await Assert.That(fixture.Coordinator.FullBeats).IsEqualTo(0);
    var exit = fixture.Exit.Requests.Single();
    await Assert.That(exit.Cause).IsEqualTo(Whizbang.Core.RunControl.ExitCause.ClusterEviction)
      .Because("#2126: an instance evicted while it is still starting exits, code 3, so its host restarts it");
    await Assert.That(exit.Reason).Contains("while it was still starting");
  }

  [Test]
  [Timeout(60000)]
  public async Task OnceTheSchemaIsReady_ARefusedBeat_ExitsOnce_AsAClusterEvictionAsync(CancellationToken cancellationToken) {
    var fixture = new Fixture(SchemaReadyGate.AlreadyReady(), fullAccepted: false);

    await fixture.Worker.StartAsync(cancellationToken);
    await fixture.Worker.ExecuteTask!.WaitAsync(_deadline, cancellationToken);

    await Assert.That(fixture.Coordinator.FullBeats).IsEqualTo(1).Because("a refused beat is never retried");
    var exit = fixture.Exit.Requests.Single();
    await Assert.That(exit.Cause).IsEqualTo(Whizbang.Core.RunControl.ExitCause.ClusterEviction);
    await Assert.That(Whizbang.Core.RunControl.ExitCauses.ExitCodeOf(exit.Cause)).IsEqualTo(3);
    await Assert.That(exit.Reason).IsEqualTo(
      "its heartbeat was refused: the fleet evicted this instance (a peer reaped it and tombstoned it)");
  }

  [Test]
  public async Task WithNoExitPath_TheWorkerIsNotConstructedAsync() {
    await Assert.That(() => new Fixture(SchemaReadyGate.AlreadyReady(), withExit: false)).Throws<ArgumentNullException>()
      .Because("an evicted instance that cannot exit would keep running sidelined");
  }

  [Test]
  public async Task OnceTheGateOpens_TheNextBeatIsDueAtOnce_AndIsTheFullHeartbeatAsync() {
    var gate = new SchemaReadyGate();
    var fixture = new Fixture(gate);
    await fixture.Worker.TickForTestsAsync(CancellationToken.None);
    var whileStarting = fixture.Worker.PlanNextTick(Fixture.Start);

    gate.MarkReady();
    var onceReady = fixture.Worker.PlanNextTick(Fixture.Start);
    await fixture.Worker.TickForTestsAsync(CancellationToken.None);
    var afterTheFullBeat = fixture.Worker.PlanNextTick(Fixture.Start);

    await Assert.That(whileStarting.BeatNow).IsFalse().Because("a startup beat was just accepted");
    await Assert.That(onceReady).IsEqualTo(new HeartbeatTickPlan(true, TimeSpan.Zero, HeartbeatBeatReason.SchemaReady))
      .Because("the full heartbeat registers the row and writes any held stage; it is not left to the next cadence");
    await Assert.That(fixture.Coordinator.FullBeats).IsEqualTo(1);
    await Assert.That(afterTheFullBeat.BeatNow).IsFalse().Because("one full beat answers the gate opening");
  }

  [Test]
  public async Task WithAnOpenGate_EveryBeatIsTheFullHeartbeatAsync() {
    var fixture = new Fixture(SchemaReadyGate.AlreadyReady());

    await fixture.Worker.TickForTestsAsync(CancellationToken.None);

    await Assert.That(fixture.Coordinator.FullBeats).IsEqualTo(1);
    await Assert.That(fixture.Coordinator.StartupRequests).IsEmpty()
      .Because("a host with no schema to wait for passes an open gate and is ready from the start");
  }

  [Test]
  public async Task WithNoSchemaReadyGate_TheWorkerIsNotConstructedAsync() {
    await Assert.That(() => new Fixture(gate: null!)).Throws<ArgumentNullException>()
      .Because("a missing gate would silently read as ready; a host with nothing to wait for says so with an open gate");
  }

  [Test]
  [Timeout(60000)]
  public async Task TheLoop_AfterAStartupBeat_BeatsInFullWhenTheGateOpens_WithoutWaitingForItsCadenceAsync(
      CancellationToken cancellationToken) {
    var gate = new SchemaReadyGate();
    var fixture = new Fixture(gate);

    await fixture.Worker.StartAsync(cancellationToken);
    try {
      await fixture.Coordinator.FirstStartupBeat.Task.WaitAsync(_deadline, cancellationToken);
      gate.MarkReady();
      await fixture.Coordinator.FirstFullBeat.Task.WaitAsync(_deadline, cancellationToken);
    } finally {
      await fixture.Worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(fixture.Time.GetUtcNow()).IsEqualTo(Fixture.Start)
      .Because("the clock never moved: the full beat followed the gate, not the cadence");
  }

  [Test]
  public async Task TheWait_WhileTheGateIsClosed_EndsWhenTheGateOpensAsync() {
    var gate = new SchemaReadyGate();
    var fixture = new Fixture(gate);

    var wait = fixture.Worker.WaitForNextTickAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
    var beforeTheGate = wait.IsCompleted;
    gate.MarkReady();
    await wait.WaitAsync(_deadline);

    await Assert.That(beforeTheGate).IsFalse();
  }

  /// <summary>
  /// The gate can open after the loop planned a wait under a closed gate and before the wait begins. The wait then ends at
  /// once, because the full beat is due; a cadence wait here held the full beat back until the clock moved.
  /// </summary>
  [Test]
  public async Task TheWait_AfterAStartupBeat_WhenTheGateOpenedSinceThePlan_EndsAtOnceAsync() {
    var gate = new SchemaReadyGate();
    var fixture = new Fixture(gate);
    await fixture.Worker.TickForTestsAsync(CancellationToken.None);
    gate.MarkReady();

    var wait = fixture.Worker.WaitForNextTickAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

    await Assert.That(wait.IsCompleted).IsTrue();
    await Assert.That(fixture.Time.GetUtcNow()).IsEqualTo(Fixture.Start);
  }

  [Test]
  public async Task TheWait_OnceTheGateIsOpen_IsTheCadenceAsync() {
    var fixture = new Fixture(SchemaReadyGate.AlreadyReady());

    var wait = fixture.Worker.WaitForNextTickAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
    fixture.Time.Advance(TimeSpan.FromSeconds(29));
    var early = wait.IsCompleted;
    fixture.Time.Advance(TimeSpan.FromSeconds(1));
    await wait.WaitAsync(_deadline);

    await Assert.That(early).IsFalse();
  }

  [Test]
  public async Task TheWait_WhileTheGateIsClosed_EndsWithTheStoppingTokenAsync() {
    var fixture = new Fixture(new SchemaReadyGate());
    using var stopping = new CancellationTokenSource();

    var wait = fixture.Worker.WaitForNextTickAsync(TimeSpan.FromSeconds(30), stopping.Token);
    await stopping.CancelAsync();

    await Assert.That(async () => await wait.WaitAsync(_deadline)).Throws<OperationCanceledException>();
  }

  private sealed class Fixture {
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public Fixture(ISchemaReadyGate gate, bool startupAccepted = true, bool fullAccepted = true, bool withExit = true) {
      Coordinator = new BeatCoordinator(startupAccepted, fullAccepted);
      var services = new ServiceCollection();
      services.AddSingleton<IWorkCoordinator>(Coordinator);
      services.AddSingleton<IStartupStageReporter>(Stages);
      services.AddSingleton<IInstanceConnectionModeSource>(new DirectMode());
      var sp = services.BuildServiceProvider();
      Instance = new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build());
      Worker = new HeartbeatWorker(
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        instanceProvider: Instance,
        options: Options.Create(_options),
        logger: NullLogger<HeartbeatWorker>.Instance,
        lifecycleState: HeartbeatTestDependencies.LifecycleState,
        libraryVersion: HeartbeatTestDependencies.Version,
        applicationVersion: HeartbeatTestDependencies.ApplicationVersion,
        pinnedPool: NoOpPinnedConnectionPool.Instance,
        aliveLockSource: NullInstanceAliveLockSource.Instance,
        signalBus: Bus,
        schemaReady: gate,
        instanceExit: withExit ? Exit : null!,
        timeProvider: Time);
    }

    public BeatCoordinator Coordinator { get; }
    public Whizbang.Testing.Lifecycle.RecordingInstanceExit Exit { get; } = new();
    public RecordingStageReporter Stages { get; } = new();
    public CapturingBus Bus { get; } = new();
    public FakeTimeProvider Time { get; } = new(Start);
    public ServiceInstanceProvider Instance { get; }
    public HeartbeatWorker Worker { get; }
  }

  private sealed class DirectMode : IInstanceConnectionModeSource {
    public InstanceConnectionMode ConnectionMode => InstanceConnectionMode.Direct;
    public bool IsAliveLockHeld => false;
  }

  private sealed class CapturingBus : ISignalBus {
    public List<Type> Published { get; } = [];
    public bool IsConfigured => true;
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      lock (Published) {
        Published.Add(typeof(TSignal));
      }
      return ValueTask.CompletedTask;
    }
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal => new NoSubscription();
    private sealed class NoSubscription : ISignalSubscription {
      public void Dispose() { }
    }
  }

  private sealed class BeatCoordinator(bool startupAccepted, bool fullAccepted) : IWorkCoordinator {
    private int _fullBeats;

    public List<HeartbeatRequest> StartupRequests { get; } = [];
    public int FullBeats => Volatile.Read(ref _fullBeats);
    public TaskCompletionSource FirstStartupBeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FirstFullBeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> RecordStartupHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      lock (StartupRequests) {
        StartupRequests.Add(request);
      }
      FirstStartupBeat.TrySetResult();
      return Task.FromResult(startupAccepted);
    }

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _fullBeats);
      FirstFullBeat.TrySetResult();
      return Task.FromResult(fullAccepted);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
