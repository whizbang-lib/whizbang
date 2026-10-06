// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branches of <see cref="MaintenanceWorker"/> the other maintenance suites leave untaken: the
/// options guard, per-task metrics when results and metrics are both present, a runner registry that
/// has no runner for one perspective, and a container that answers an unregistered enumerable with
/// null rather than an empty sequence (both the guard phase and the stream-group cascade).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
[Category("Workers")]
public class MaintenanceWorkerBranchCoverageTests {

  private sealed class AnnouncerModel;
  private sealed class FollowerModel;

  private const string ANNOUNCER_TABLE = "wh_per_branch_announcer";
  private const string FOLLOWER_TABLE = "wh_per_branch_follower";

  private sealed class NullValueOptions : IOptions<MaintenanceWorkerOptions> {
    public MaintenanceWorkerOptions Value => null!;
  }

  private sealed class FakeRunner : IPerspectiveRunner {
    public List<(Guid StreamId, string Perspective, Guid LastEventId)> BootstrapCalls { get; } = [];
    public Type PerspectiveType => typeof(object);
    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) {
      BootstrapCalls.Add((streamId, perspectiveName, lastProcessedEventId));
      return Task.CompletedTask;
    }
    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  /// <summary>Knows a runner for one perspective name only; every other name has none.</summary>
  private sealed class PartialRegistry(string knownPerspective, IPerspectiveRunner runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      perspectiveName == knownPerspective ? runner : null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors => new HashSet<LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  /// <summary>
  /// Wraps a container so it answers the destruction-guard enumerable with null, as a third-party
  /// container may for an enumerable nobody registered, and delegates everything else.
  /// </summary>
  private sealed class NullGuardsProvider(ServiceProvider inner) : IKeyedServiceProvider {
    public object? GetService(Type serviceType) =>
      serviceType == typeof(IEnumerable<IPerspectiveRowDestructionGuard>) ? null : inner.GetService(serviceType);
    public object? GetKeyedService(Type serviceType, object? serviceKey) => inner.GetKeyedService(serviceType, serviceKey);
    public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => inner.GetRequiredKeyedService(serviceType, serviceKey);
  }

  private sealed class FixedScopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public IServiceScope CreateScope() => new FixedScope(provider);
  }

  private sealed class FixedScope(IServiceProvider provider) : IServiceScope {
    public IServiceProvider ServiceProvider => provider;
    public void Dispose() { }
  }

  private sealed class BranchCoordinator : IWorkCoordinator {
    public List<MaintenanceResult> Results { get; init; } = [];
    public List<EphemeralSnapshotTarget> Pairs { get; init; } = [];
    public List<PerspectiveRowRef> Journal { get; init; } = [];
    public List<PerspectiveTableName> Tables { get; init; } = [];
    public int AboutToReapCalls { get; private set; }
    public int EnrolledReapCalls { get; private set; }
    public List<(string Table, IReadOnlyCollection<Guid> Ids)> CascadeDeleted { get; } = [];

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<MaintenanceResult>>(Results);

    public Task<IReadOnlyList<EphemeralSnapshotTarget>> GetEphemeralPairsNeedingSnapshotAsync(CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<EphemeralSnapshotTarget>>(Pairs);

    public Task<IReadOnlyList<PerspectiveRowDestructionTarget>> GetPerspectiveRowsAboutToReapAsync(
        IReadOnlyCollection<string> clrTypeNames, int perTableLimit = 500, CancellationToken cancellationToken = default) {
      AboutToReapCalls++;
      return Task.FromResult<IReadOnlyList<PerspectiveRowDestructionTarget>>([]);
    }

    public Task<PerspectiveRowReapResult> ReapEnrolledPerspectiveRowsAsync(int batchSize = 5000, CancellationToken cancellationToken = default) {
      EnrolledReapCalls++;
      return Task.FromResult(new PerspectiveRowReapResult(0, "ok"));
    }

    public Task<IReadOnlyList<PerspectiveRowRef>> DrainRowEvictionJournalAsync(int limit = 1000, CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<PerspectiveRowRef>>(Journal);

    public Task<IReadOnlyList<PerspectiveTableName>> GetPerspectiveTableNamesAsync(
        IReadOnlyCollection<string> clrTypeNames, CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<PerspectiveTableName>>(Tables);

    public Task<int> CascadeDeletePerspectiveRowsAsync(string tableName, IReadOnlyCollection<Guid> rowIds, CancellationToken cancellationToken = default) {
      lock (CascadeDeleted) { CascadeDeleted.Add((tableName, rowIds)); }
      return Task.FromResult(rowIds.Count);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static MaintenanceWorker _build(IServiceScopeFactory scopeFactory, MaintenanceMetrics? metrics = null) {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new MaintenanceWorker(
      scopeFactory,
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, StuckRowSentinelEnabled = false }),
      NullLogger<MaintenanceWorker>.Instance,
      metrics);
  }

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new MaintenanceWorker(
        sp.GetRequiredService<IServiceScopeFactory>(),
        SchemaReadyGate.AlreadyReady(),
        null!,
        NullLogger<MaintenanceWorker>.Instance))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new MaintenanceWorker(
        sp.GetRequiredService<IServiceScopeFactory>(),
        SchemaReadyGate.AlreadyReady(),
        new NullValueOptions(),
        NullLogger<MaintenanceWorker>.Instance))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  public async Task MaintenanceCycle_WithMetrics_RecordsEachTaskDurationTaggedByTaskAsync() {
    await using var meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new MaintenanceMetrics(new WhizbangMetrics(meterFactory: meterServices.GetRequiredService<IMeterFactory>()));
    var readings = new List<(double Value, string? TaskName)>();
    using var listener = new MeterListener();
    // Pinned to this instance's instrument: parallel tests build MaintenanceMetrics on the same meter name.
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.TaskDuration)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<double>((_, value, tags, _) => {
      string? task = null;
      foreach (var tag in tags) {
        if (tag.Key == "task") {
          task = tag.Value?.ToString();
        }
      }
      lock (readings) {
        readings.Add((value, task));
      }
    });
    listener.Start();

    var coord = new BranchCoordinator {
      Results = [
        new MaintenanceResult("purge_stale_instances", 3, 12.5, "ok"),
        new MaintenanceResult("reap_expired_perspective_rows", 0, 4.0, "ok"),
      ],
    };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    await using var sp = services.BuildServiceProvider();

    await _build(sp.GetRequiredService<IServiceScopeFactory>(), metrics).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(readings).Contains((12.5, "purge_stale_instances"))
      .Because("each task's outcome is a fleet-visible series, so a registered meter must receive it");
    await Assert.That(readings).Contains((4.0, "reap_expired_perspective_rows"))
      .Because("duration records for every task, including one that touched no rows");
  }

  [Test]
  public async Task ReapDrivenSnapshot_PerspectiveWithNoRunner_IsSkippedAndTheNextPairStillSnapshotsAsync() {
    var missingStream = Guid.CreateVersion7();
    var presentStream = Guid.CreateVersion7();
    var presentEvent = Guid.CreateVersion7();
    var coord = new BranchCoordinator {
      Pairs = [
        new EphemeralSnapshotTarget(missingStream, "RetiredPerspective", Guid.CreateVersion7()),
        new EphemeralSnapshotTarget(presentStream, "LivePerspective", presentEvent),
      ],
    };
    var runner = new FakeRunner();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new PartialRegistry("LivePerspective", runner));
    await using var sp = services.BuildServiceProvider();

    await _build(sp.GetRequiredService<IServiceScopeFactory>()).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(runner.BootstrapCalls).Count().IsEqualTo(1)
      .Because("a pair whose perspective this host has no runner for is skipped, not handed to some other runner");
    await Assert.That(runner.BootstrapCalls[0]).IsEqualTo((presentStream, "LivePerspective", presentEvent))
      .Because("one unknown perspective must not stop the remaining pairs from getting their rewind floor");
  }

  [Test]
  public async Task RowSweep_ContainerAnswersGuardEnumerableWithNull_SkipsTheGuardPhaseAndStillReapsAsync() {
    var coord = new BranchCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    await using var root = services.BuildServiceProvider();

    await _build(new FixedScopeFactory(new NullGuardsProvider(root))).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.AboutToReapCalls).IsEqualTo(0)
      .Because("no guards means nothing to offer, so the collect query is not issued");
    await Assert.That(coord.EnrolledReapCalls).IsEqualTo(1)
      .Because("a null answer is read as no guards; failing on it would abandon the whole row sweep");
  }

  [Test]
  [NotInParallel("PerspectiveStreamGroupRegistry")]
  public async Task StreamGroupCascade_ContainerAnswersGuardEnumerableWithNull_CascadesUnguardedAsync() {
    PerspectiveStreamGroupRegistry.Clear();
    try {
      PerspectiveStreamGroupRegistry.Register(typeof(AnnouncerModel), "branch-orders", announce: true, follow: false, bridge: false);
      PerspectiveStreamGroupRegistry.Register(typeof(FollowerModel), "branch-orders", announce: false, follow: true, bridge: false);
      var coord = new BranchCoordinator {
        Journal = [new PerspectiveRowRef(ANNOUNCER_TABLE, Guid.CreateVersion7())],
        Tables = [
          new PerspectiveTableName(typeof(AnnouncerModel).FullName!, ANNOUNCER_TABLE),
          new PerspectiveTableName(typeof(FollowerModel).FullName!, FOLLOWER_TABLE),
        ],
      };
      var services = new ServiceCollection();
      services.AddSingleton<IWorkCoordinator>(coord);
      await using var root = services.BuildServiceProvider();

      await _build(new FixedScopeFactory(new NullGuardsProvider(root))).RunMaintenanceOnceAsync(CancellationToken.None);

      await Assert.That(coord.CascadeDeleted.Any(d => d.Table == FOLLOWER_TABLE)).IsTrue()
        .Because("with no guards the follower's rows cascade directly; a null answer must not abort the cascade");
    } finally {
      PerspectiveStreamGroupRegistry.Clear();
    }
  }

  // ---- collaborators that complete asynchronously -----------------------------------------------
  // Every other maintenance fake answers with an already-completed task, so the cycle never suspends
  // inside the guarded try blocks below and the resume path after each await is never exercised. A
  // real database call does suspend; these fakes yield first so the cycle resumes the way it does in
  // production.

  /// <summary>Records its runs and yields before each one completes, as a real snapshot write does.</summary>
  private sealed class YieldingRunner : IPerspectiveRunner {
    private readonly List<(Guid StreamId, string Perspective, Guid LastEventId)> _calls = [];
    public Type PerspectiveType => typeof(object);
    public List<(Guid StreamId, string Perspective, Guid LastEventId)> Snapshot() {
      lock (_calls) { return [.. _calls]; }
    }
    public async Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) {
      await Task.Yield();
      lock (_calls) { _calls.Add((streamId, perspectiveName, lastProcessedEventId)); }
    }
    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  /// <summary>
  /// Answers epoch closure and rewrite recording asynchronously and records the order the cycle
  /// reached each call in.
  /// </summary>
  private sealed class YieldingCoordinator : IWorkCoordinator {
    private readonly List<string> _calls = [];
    public List<EphemeralSnapshotTarget> Pairs { get; init; } = [];
    public List<TableRewriteCandidate> Candidates { get; init; } = [];
    public int EpochsToClose { get; init; }

    public List<string> Snapshot() {
      lock (_calls) { return [.. _calls]; }
    }

    private void _record(string call) {
      lock (_calls) { _calls.Add(call); }
    }

    public async Task<int> CloseDigestEpochsAsync(int settleSeconds, int maxEpochs, CancellationToken cancellationToken = default) {
      await Task.Yield();
      _record("close-epochs");
      return EpochsToClose;
    }

    public Task<IReadOnlyList<TableRewriteCandidate>> GetTablesNeedingRewriteAsync(CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<TableRewriteCandidate>>(Candidates);

    public async Task RequestTableRewriteAsync(string tableName, CancellationToken cancellationToken = default) {
      await Task.Yield();
      _record("request:" + tableName);
    }

    public Task<IReadOnlyList<EphemeralSnapshotTarget>> GetEphemeralPairsNeedingSnapshotAsync(CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<EphemeralSnapshotTarget>>(Pairs);

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
      _record("perform-maintenance");
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  [Test]
  public async Task EpochClosure_CompletesAsynchronously_CycleResumesAndStillReachesTheReapAsync() {
    var coord = new YieldingCoordinator { EpochsToClose = 3 };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton(Options.Create(new StreamIntegrityOptions { EpochClosureEnabled = true }));
    await using var sp = services.BuildServiceProvider();

    await _build(sp.GetRequiredService<IServiceScopeFactory>()).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Snapshot()).IsEquivalentTo(["close-epochs", "perform-maintenance"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a closure that suspends on the database must resume into the rest of the cycle; the reap "
             + "runs after closure, never instead of it and never before it");
  }

  [Test]
  public async Task RewriteRecording_CompletesAsynchronously_EveryCandidateIsStillRecordedInOrderAsync() {
    var coord = new YieldingCoordinator {
      Candidates = [
        new TableRewriteCandidate("wh_event_store", 4.2, Requested: false),
        new TableRewriteCandidate("wh_outbox", 3.1, Requested: true),
        new TableRewriteCandidate("wh_inbox", 3.9, Requested: false),
      ],
    };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    await using var sp = services.BuildServiceProvider();

    await _build(sp.GetRequiredService<IServiceScopeFactory>()).RunMaintenanceOnceAsync(CancellationToken.None);

    var requests = coord.Snapshot().Where(c => c.StartsWith("request:", StringComparison.Ordinal)).ToList();
    await Assert.That(requests).IsEquivalentTo(["request:wh_event_store", "request:wh_inbox"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a recording that suspends resumes into the next candidate, and the one already on the books "
             + "is still skipped after the loop has resumed from an earlier await");
  }

  [Test]
  public async Task ReapDrivenSnapshot_RunnerCompletesAsynchronously_EveryPairStillGetsItsSnapshotAsync() {
    var firstStream = Guid.CreateVersion7();
    var secondStream = Guid.CreateVersion7();
    var firstEvent = Guid.CreateVersion7();
    var secondEvent = Guid.CreateVersion7();
    var coord = new YieldingCoordinator {
      Pairs = [
        new EphemeralSnapshotTarget(firstStream, "LivePerspective", firstEvent),
        new EphemeralSnapshotTarget(secondStream, "LivePerspective", secondEvent),
      ],
    };
    var runner = new YieldingRunner();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new PartialRegistry("LivePerspective", runner));
    await using var sp = services.BuildServiceProvider();

    await _build(sp.GetRequiredService<IServiceScopeFactory>()).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(runner.Snapshot()).IsEquivalentTo(
        [(firstStream, "LivePerspective", firstEvent), (secondStream, "LivePerspective", secondEvent)],
        TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a snapshot write that suspends must resume into the next pair, so every pair the reaper is "
             + "about to delete from has its rewind floor first");
  }
}
