// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// #1992: an instance whose binary is older than the schema stands down, and is visible while it does. It records
/// why through the one recorder of its stage and phase (Preflight, standing by, the reason naming both versions),
/// registers at once without applying anything, and keeps its row alive on the heartbeat's cadence until the host
/// stops, so the rollout that replaces it can see it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStandDown.cs</code-under-test>
[Category("Startup")]
public class StartupStandDownTests {

  private static readonly StartupAssessment _obsolete = new(StartupVerdict.StandDown,
    "the ledger records version 2.0.0, newer than this binary's 1.0.0");

  [Test]
  public async Task Record_StaysInPreflight_NamingTheReason_AndStandsByAsync() {
    var stages = new RecordingStageReporter();
    var standDown = _create(new BeatingCoordinator(), stages, new FakeTimeProvider());

    await standDown.RecordAsync(_obsolete, CancellationToken.None);

    await Assert.That(stages.Calls).IsEquivalentTo([
      "Enter Preflight: standing down: the ledger records version 2.0.0, newer than this binary's 1.0.0",
      "Phase StandingBy",
    ], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the stage and the phase are written by their one recorder, and the stage stays where nothing was applied");
  }

  [Test]
  public async Task Record_WithTheRunControlPath_StandsDownThroughItAsync() {
    // #2045: a stand-down found by Assess takes the same path as one found while running, a pause or a stop. The stage
    // keeps the reason; the path closes the permit, releases what is held (nothing, at Preflight) and writes the phase.
    var stages = new RecordingStageReporter();
    var lifecycle = new PhaseLog();
    var permit = new WhizbangRunPermit();
    var path = new WhizbangRunController(permit, lifecycle, [], [], new WhizbangLifecycleOptions(), null,
      NullLogger<WhizbangRunController>.Instance);
    var standDown = _create(new BeatingCoordinator(), stages, new FakeTimeProvider(), path);

    await standDown.RecordAsync(_obsolete, CancellationToken.None);

    await Assert.That(stages.Calls).IsEquivalentTo([
      "Enter Preflight: standing down: the ledger records version 2.0.0, newer than this binary's 1.0.0",
    ]).Because("the phase is the path's to write; the stage keeps the reason");
    await Assert.That(lifecycle.Phases).IsEquivalentTo([LifecyclePhase.Pausing, LifecyclePhase.StandingBy],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(permit.State).IsEqualTo(RunState.Paused);
    await Assert.That(path.HaltedFor).IsEqualTo(RunHaltReason.StandDown);
  }

  [Test]
  public async Task FromServices_TakesTheRegisteredRunControlPathAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(_instance);
    services.AddWhizbangRunControl();
    await using var provider = services.BuildServiceProvider();

    var standDown = StartupStandDown.FromServices(provider)!;

    await Assert.That(standDown.RunControl).IsSameReferenceAs(provider.GetRequiredService<IWhizbangRunController>());
    await Assert.That(standDown.InstanceExit).IsSameReferenceAs(provider.GetRequiredService<IWhizbangInstanceExit>());
  }

  private sealed class PhaseLog : IWhizbangLifecycleState {
    public List<LifecyclePhase> Phases { get; } = [];
    public LifecyclePhase Phase => Phases.Count == 0 ? LifecyclePhase.Starting : Phases[^1];
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Phases.Add(phase);
      return ValueTask.CompletedTask;
    }
    public ValueTask FaultAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }

  [Test]
  public async Task KeepVisible_RegistersAtOnce_ThenOnEveryBeat_UntilTheHostStopsAsync() {
    var clock = new FakeTimeProvider();
    var coordinator = new BeatingCoordinator();
    var stages = new RecordingStageReporter();
    var standDown = _create(coordinator, stages, clock);
    var beats = new BeatSignals(standDown);
    using var stop = new CancellationTokenSource();

    var visible = standDown.KeepVisibleAsync(stop.Token);
    await Assert.That(await beats.NextAsync()).IsTrue();
    await Assert.That(coordinator.Requests).Count().IsEqualTo(1)
      .Because("the instance is registered at once, before any wait");
    clock.Advance(TimeSpan.FromSeconds(5));
    await Assert.That(await beats.NextAsync()).IsTrue();
    await stop.CancelAsync();
    await visible;

    await Assert.That(coordinator.Requests).Count().IsEqualTo(2);
    await Assert.That(stages.Calls).IsEquivalentTo(["Registered", "Registered"])
      .Because("each accepted beat writes whatever the recorder holds: the stand-down state lands with the row");
    var request = coordinator.Requests[0];
    await Assert.That(request.InstanceId).IsEqualTo(_instance.InstanceId);
    await Assert.That(request.ServiceName).IsEqualTo("orders");
    await Assert.That(request.LibraryVersion).IsEqualTo("1.0.0");
    await Assert.That(request.LifecyclePhase).IsNull()
      .Because("the phase has one writer, the stage recorder; a beat never writes it");
  }

  [Test]
  public async Task KeepVisible_WhenTheRegistryEvictedTheInstance_StopsBeatingAsync() {
    var coordinator = new BeatingCoordinator { Accept = false };
    var stages = new RecordingStageReporter();
    var standDown = _create(coordinator, stages, new FakeTimeProvider());

    await standDown.KeepVisibleAsync(CancellationToken.None);

    await Assert.That(coordinator.Requests).Count().IsEqualTo(1);
    await Assert.That(stages.Calls).IsEmpty()
      .Because("a tombstoned instance id never becomes valid again, so beating on is pointless");
  }

  [Test]
  public async Task KeepVisible_WhenTheRegistryEvictedTheInstance_ExitsOnce_AsAClusterEvictionAsync() {
    // #2126: nothing keeps running sidelined. A stood-down instance the fleet evicted exits, code 3, so its host restarts
    // it under a new instance id.
    var coordinator = new BeatingCoordinator { Accept = false };
    var exit = new Whizbang.Testing.Lifecycle.RecordingInstanceExit();
    var standDown = _create(coordinator, new RecordingStageReporter(), new FakeTimeProvider(), instanceExit: exit);

    await standDown.KeepVisibleAsync(CancellationToken.None);

    var request = exit.Requests.Single();
    await Assert.That(request.Cause).IsEqualTo(ExitCause.ClusterEviction);
    await Assert.That(request.Reason).Contains("stood-down instance was refused");
  }

  [Test]
  public async Task KeepVisible_AFailedBeat_IsRetriedOnTheNextBeatAsync() {
    var clock = new FakeTimeProvider();
    var coordinator = new BeatingCoordinator { Failures = 1 };
    var stages = new RecordingStageReporter();
    var standDown = _create(coordinator, stages, clock);
    var beats = new BeatSignals(standDown);
    using var stop = new CancellationTokenSource();

    var visible = standDown.KeepVisibleAsync(stop.Token);
    await Assert.That(await beats.NextAsync()).IsFalse();
    clock.Advance(TimeSpan.FromSeconds(5));
    await Assert.That(await beats.NextAsync()).IsTrue();
    await stop.CancelAsync();
    await visible;

    await Assert.That(stages.Calls).IsEquivalentTo(["Registered"]);
  }

  [Test]
  public async Task KeepVisible_ACoordinatorThatCannotBeResolved_IsABeatThatFailed_AndRetriedAsync() {
    var clock = new FakeTimeProvider();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => throw new InvalidOperationException("a dependency is not registered"));
    var provider = services.BuildServiceProvider();
    var standDown = new StartupStandDown(provider.GetRequiredService<IServiceScopeFactory>(), _instance, new RecordingStageReporter(),
      "1.0.0", TimeSpan.FromSeconds(5), clock, NullLogger<StartupStandDown>.Instance);
    var beats = new BeatSignals(standDown);
    using var stop = new CancellationTokenSource();

    var visible = standDown.KeepVisibleAsync(stop.Token);
    await Assert.That(await beats.NextAsync()).IsFalse()
      .Because("the beat is logged as failed and tried again; keeping visible never faults");
    await stop.CancelAsync();
    await visible;
  }

  [Test]
  public async Task KeepVisible_WithNoInstanceRowsToKeep_DoesNothingAsync() {
    var coordinator = new BeatingCoordinator { Records = false };
    var standDown = _create(coordinator, new RecordingStageReporter(), new FakeTimeProvider());

    await standDown.KeepVisibleAsync(CancellationToken.None);

    await Assert.That(coordinator.Requests).IsEmpty();
  }

  [Test]
  public async Task KeepVisible_StoppedDuringABeat_EndsQuietlyAsync() {
    using var stop = new CancellationTokenSource();
    var coordinator = new BeatingCoordinator { OnBeat = () => stop.Cancel() };
    var standDown = _create(coordinator, new RecordingStageReporter(), new FakeTimeProvider());

    await standDown.KeepVisibleAsync(stop.Token);

    await Assert.That(coordinator.Requests).Count().IsEqualTo(1);
  }

  [Test]
  public async Task FromServices_WithoutAnInstanceIdentity_HasNothingToRegisterAsync() {
    await using var provider = new ServiceCollection().BuildServiceProvider();

    await Assert.That(StartupStandDown.FromServices(provider)).IsNull();
  }

  [Test]
  public async Task FromServices_BeatsOnTheHeartbeatCadence_AsTheRegisteredInstanceAsync() {
    var clock = new FakeTimeProvider();
    var coordinator = new BeatingCoordinator();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider>(_instance);
    services.AddSingleton<ILibraryVersionProvider>(new LibraryVersionProvider("1.0.0"));
    services.AddSingleton<Whizbang.Core.Versioning.IApplicationVersionProvider>(
      new Whizbang.Core.Versioning.ApplicationVersionProvider(Whizbang.Core.Versioning.ApplicationVersion.FromRecorded("deploy:3")));
    services.AddSingleton<TimeProvider>(clock);
    var stages = new RecordingStageReporter();
    services.AddSingleton<IStartupStageReporter>(stages);
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.Configure<HeartbeatWorkerOptions>(o => o.IntervalSeconds = 9);
    await using var provider = services.BuildServiceProvider();
    var standDown = StartupStandDown.FromServices(provider)!;
    var beats = new BeatSignals(standDown);
    using var stop = new CancellationTokenSource();

    var visible = standDown.KeepVisibleAsync(stop.Token);
    await beats.NextAsync();
    clock.Advance(TimeSpan.FromSeconds(9));
    await beats.NextAsync();
    await stop.CancelAsync();
    await visible;

    await Assert.That(coordinator.Requests).Count().IsEqualTo(2);
    await Assert.That(coordinator.Requests[0].LibraryVersion).IsEqualTo("1.0.0");
    // #2027: a stood-down instance records its application version, and the window the one liveness rule judges it by:
    // three of its own beats (9 seconds each), so it is never reaped between them.
    await Assert.That(coordinator.Requests[0].ApplicationVersion).IsEqualTo("deploy:3");
    await Assert.That(coordinator.Requests[0].StaleThresholdSeconds).IsEqualTo(27);
    await Assert.That(stages.Calls).IsEquivalentTo(["Registered", "Registered"])
      .Because("the stand-down records through the host's one stage recorder");
  }

  [Test]
  public async Task FromServices_WithNothingElseRegistered_TakesTheDefaultsAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(_instance);
    await using var provider = services.BuildServiceProvider();
    var standDown = StartupStandDown.FromServices(provider)!;

    await standDown.RecordAsync(_obsolete, CancellationToken.None);
    await standDown.KeepVisibleAsync(CancellationToken.None);

    await Assert.That(standDown).IsNotNull()
      .Because("no reporter, no coordinator and no version provider: nothing is recorded or kept, and nothing throws");
  }

  private static readonly FixedInstance _instance = new();

  private static StartupStandDown _create(
      BeatingCoordinator coordinator, IStartupStageReporter stages, TimeProvider clock, IWhizbangRunController? runControl = null,
      IWhizbangInstanceExit? instanceExit = null) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    var provider = services.BuildServiceProvider();
    return new StartupStandDown(
      provider.GetRequiredService<IServiceScopeFactory>(), _instance, stages, "1.0.0", TimeSpan.FromSeconds(5), clock,
      NullLogger<StartupStandDown>.Instance) { RunControl = runControl, InstanceExit = instanceExit };
  }

  /// <summary>Hands out each beat's outcome in order, as the stand-down announces it.</summary>
  private sealed class BeatSignals {
    private readonly System.Threading.Channels.Channel<bool> _beats = System.Threading.Channels.Channel.CreateUnbounded<bool>();

    public BeatSignals(StartupStandDown standDown) => standDown.BeatRecorded += accepted => _beats.Writer.TryWrite(accepted);

    public ValueTask<bool> NextAsync() => _beats.Reader.ReadAsync();
  }

  private sealed class FixedInstance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "orders";
    public string HostName => "host-1";
    public int ProcessId => 42;
    public ServiceInstanceInfo ToInfo() => throw new NotSupportedException();
  }

  private sealed class BeatingCoordinator : IWorkCoordinator {
    public List<HeartbeatRequest> Requests { get; } = [];
    public bool Accept { get; init; } = true;
    public bool Records { get; init; } = true;
    public int Failures { get; set; }
    public Action? OnBeat { get; init; }
    public bool RecordsInstanceTransitions => Records;

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      Requests.Add(request);
      OnBeat?.Invoke();
      cancellationToken.ThrowIfCancellationRequested();
      if (Failures > 0) {
        Failures--;
        throw new InvalidOperationException("the database dropped the connection");
      }
      return Task.FromResult(Accept);
    }

    // Unused surface for this test.
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
}
