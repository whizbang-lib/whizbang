// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Execution;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Startup-reconciler and periodic-maintenance coverage for <see cref="PerspectiveWorker"/>:
/// <list type="bullet">
/// <item><description>_reconcileOrphanedLifecyclesAsync — replay of orphaned PostLifecycle events,
/// per-orphan error isolation, skip when no perspectives registered, outer catch</description></item>
/// <item><description>_scanAndRepairRewindsOnStartupAsync — clean scan, Background mode,
/// Blocking mode re-poll loop, disabled scan, error swallow</description></item>
/// <item><description>_periodicGatherStatisticsAsync — 60-cycle cadence + failure swallow</description></item>
/// <item><description>_periodicStaleTrackingCleanup — 10-cycle cadence + cleaned&gt;0 branch</description></item>
/// <item><description>NOTIFY listener subscribe on start / unsubscribe on StopAsync + signal coalescing</description></item>
/// </list>
/// All waits are signal-based (TCS / SemaphoreSlim / OnBatchCycleComplete) — no polling.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
[NotInParallel("PerspectiveChannelModeTests")]
public class PerspectiveWorkerStartupAndMaintenanceTests {

  // ============================================================
  // Test doubles
  // ============================================================

  private sealed class InstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "startup-test-svc";
    public string HostName => "startup-test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class Registry(IReadOnlyList<PerspectiveRegistrationInfo> perspectives) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => perspectives;
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private static Registry _registryWithOnePerspective() => new([
    new PerspectiveRegistrationInfo(
      ClrTypeName: "Test.Perspectives.OrderPerspective",
      FullyQualifiedName: "global::Test.Perspectives.OrderPerspective",
      ModelType: "global::Test.Models.OrderModel",
      EventTypes: [ORPHAN_EVENT_TYPE]
    )
  ]);

  private const string ORPHAN_EVENT_TYPE = "Test.Events.OrderCreated, Test";

  private sealed class StartupCoordinator : IWorkCoordinator, IDisposable {
    // ── orphan reconciliation ────────────────────────────────
    public List<OrphanedLifecycleEvent> Orphans { get; init; } = [];
    public Queue<IReadOnlyList<OrphanedLifecycleEvent>> OrphanBatches { get; } = new();
    private int _orphanQueryCount;
    public int OrphanQueryCount => _orphanQueryCount;
    public int? CapturedMaxOrphans { get; private set; }
    public bool ThrowOnOrphanQuery { get; init; }
    public TaskCompletionSource OrphanQueryCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TimeSpan? CapturedLookback { get; private set; }
    public Dictionary<string, IReadOnlyList<string>>? CapturedPerspectivesMap { get; private set; }
    public ConcurrentBag<Guid> RecordedLifecycleCompletions { get; } = [];
    /// <summary>Runs after each lifecycle completion is recorded, with the running total.</summary>
    public Action<int>? OnRecordedCompletion { get; init; }
    private readonly SemaphoreSlim _recordSignal = new(0, int.MaxValue);

    public async Task WaitForRecordedCompletionsAsync(int count, TimeSpan timeout) {
      for (var i = 0; i < count; i++) {
        if (!await _recordSignal.WaitAsync(timeout)) {
          throw new TimeoutException($"Only saw {i} of {count} lifecycle-completion records within {timeout}");
        }
      }
    }

    // ── rewind startup scan ──────────────────────────────────
    public Queue<IReadOnlyList<RewindCursorInfo>> RewindResults { get; } = new();
    public bool ThrowOnRewindQuery { get; init; }
    /// <summary>When set, every rewind-scan query throws this instead of answering.</summary>
    public Exception? RewindQueryException { get; init; }
    private int _rewindQueryCount;
    public int RewindQueryCount => _rewindQueryCount;
    private readonly SemaphoreSlim _rewindSignal = new(0, int.MaxValue);

    public async Task WaitForRewindQueriesAsync(int count, TimeSpan timeout) {
      for (var i = 0; i < count; i++) {
        if (!await _rewindSignal.WaitAsync(timeout)) {
          throw new TimeoutException($"Only saw {i} of {count} rewind-scan queries within {timeout}");
        }
      }
    }

    // ── periodic statistics ──────────────────────────────────
    public bool ThrowOnFirstGatherStatistics { get; init; }
    /// <summary>The pending perspective event count every statistics gather reports.</summary>
    public long PendingPerspectiveEvents { get; init; }
    private int _gatherCount;
    public int GatherStatisticsCount => _gatherCount;
    private readonly SemaphoreSlim _gatherSignal = new(0, int.MaxValue);

    public async Task WaitForGatherStatisticsAsync(int count, TimeSpan timeout) {
      for (var i = 0; i < count; i++) {
        if (!await _gatherSignal.WaitAsync(timeout)) {
          throw new TimeoutException($"Only saw {i} of {count} GatherStatistics calls within {timeout}");
        }
      }
    }

    // ── IWorkCoordinator surface ─────────────────────────────
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<StreamEventData>());

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) {
      var call = Interlocked.Increment(ref _gatherCount);
      _gatherSignal.Release();
      if (call == 1 && ThrowOnFirstGatherStatistics) {
        throw new InvalidOperationException("simulated statistics failure");
      }
      return Task.FromResult(new WorkCoordinatorStatistics { PendingPerspectiveEvents = PendingPerspectiveEvents });
    }

    public Task<IReadOnlyList<OrphanedLifecycleEvent>> GetOrphanedLifecycleEventsAsync(
        Dictionary<string, IReadOnlyList<string>> perspectivesPerEventType,
        TimeSpan lookbackWindow,
        int maxOrphans = 100,
        CancellationToken cancellationToken = default) {
      CapturedPerspectivesMap = perspectivesPerEventType;
      CapturedLookback = lookbackWindow;
      CapturedMaxOrphans = maxOrphans;
      Interlocked.Increment(ref _orphanQueryCount);
      OrphanQueryCalled.TrySetResult();
      if (ThrowOnOrphanQuery) {
        throw new InvalidOperationException("simulated orphan-query failure");
      }
      IReadOnlyList<OrphanedLifecycleEvent> batch =
        OrphanBatches.Count > 0 ? OrphanBatches.Dequeue() : Orphans;
      return Task.FromResult(batch);
    }

    public Task RecordLifecycleCompletionAsync(Guid eventId, CancellationToken cancellationToken = default) {
      RecordedLifecycleCompletions.Add(eventId);
      _recordSignal.Release();
      OnRecordedCompletion?.Invoke(RecordedLifecycleCompletions.Count);
      return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RewindCursorInfo>> GetCursorsRequiringRewindAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _rewindQueryCount);
      _rewindSignal.Release();
      if (ThrowOnRewindQuery) {
        throw new InvalidOperationException("simulated rewind-scan failure");
      }
      if (RewindQueryException is not null) {
        throw RewindQueryException;
      }
      IReadOnlyList<RewindCursorInfo> result = RewindResults.Count > 0 ? RewindResults.Dequeue() : [];
      return Task.FromResult(result);
    }

    public void Dispose() {
      _gatherSignal.Dispose();
      _recordSignal.Dispose();
      _rewindSignal.Dispose();
    }
  }

  private sealed class SpyTracking(Guid eventId, List<LifecycleStage> stages, bool throwOnAdvance) : ILifecycleTracking {
    public Guid EventId { get; } = eventId;
    public LifecycleStage CurrentStage { get; private set; }
    public bool IsComplete { get; private set; }
    public ValueTask AdvanceToAsync(LifecycleStage stage, IServiceProvider scopedProvider, CancellationToken ct) {
      if (throwOnAdvance) {
        throw new InvalidOperationException("simulated lifecycle replay failure");
      }
      stages.Add(stage);
      CurrentStage = stage;
      if (stage == LifecycleStage.PostLifecycleInline) {
        IsComplete = true;
      }
      return ValueTask.CompletedTask;
    }
    public ValueTask DrainDetachedAsync() => ValueTask.CompletedTask;
  }

  private sealed class SpyLifecycleCoordinator : ILifecycleCoordinator {
    public HashSet<Guid> ThrowOnAdvanceFor { get; } = [];
    public ConcurrentDictionary<Guid, List<LifecycleStage>> AdvancedByEvent { get; } = new();
    public int CleanupCalls { get; private set; }
    public TimeSpan? LastCleanupThreshold { get; private set; }
    public int CleanupReturnValue { get; set; }

    public ILifecycleTracking BeginTracking(
        Guid eventId, IMessageEnvelope envelope, LifecycleStage entryStage,
        MessageSource source, Guid? streamId = null, Type? perspectiveType = null) {
      var stages = AdvancedByEvent.GetOrAdd(eventId, _ => []);
      return new SpyTracking(eventId, stages, ThrowOnAdvanceFor.Contains(eventId));
    }

    public ILifecycleTracking? GetTracking(Guid eventId) => null;
    public void ExpectCompletionsFrom(Guid eventId, params PostLifecycleCompletionSource[] sources) { }
    public ValueTask SignalSegmentCompleteAsync(
        Guid eventId, PostLifecycleCompletionSource source,
        IServiceProvider scopedProvider, CancellationToken ct) => ValueTask.CompletedTask;
    public void AbandonTracking(Guid eventId) { }
    public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) { }
    public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) => false;
    public bool AreAllPerspectivesComplete(Guid eventId) => true;
    public int CleanupStaleTracking(TimeSpan inactivityThreshold) {
      CleanupCalls++;
      LastCleanupThreshold = inactivityThreshold;
      return CleanupReturnValue;
    }
  }

  private sealed class FakeNotificationListener : IWorkNotificationListener {
    private Action<WorkSignalCategory>? _onSignal;
    public int SubscriberCount { get; private set; }
    public bool IsHealthy => true;
    public DateTimeOffset? LastSignalAt => null;
    /// <summary>Completes when the worker hooks OnSignal — the subscription happens inside
    /// ExecuteAsync, so tests must await this instead of racing StartAsync's return.</summary>
    public TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action<WorkSignalCategory>? OnSignal {
      add { _onSignal += value; SubscriberCount++; Subscribed.TrySetResult(); }
      remove { _onSignal -= value; SubscriberCount--; }
    }
    public event Action<bool>? OnHealthChanged { add { /* the fake never raises this event */ } remove { /* the fake never raises this event */ } }
    public void Fire(WorkSignalCategory category) => _onSignal?.Invoke(category);
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static OrphanedLifecycleEvent _orphan() {
    var eventId = Guid.CreateVersion7();
    var envelope = new MessageEnvelope<System.Text.Json.JsonElement> {
      MessageId = new MessageId(eventId),
      Payload = System.Text.Json.JsonDocument.Parse("{}").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    return new OrphanedLifecycleEvent(eventId, Guid.CreateVersion7(), envelope);
  }

  private sealed record Fixture(
    PerspectiveWorker Worker,
    PerspectiveWorkerTestHarness Harness,
    StartupCoordinator Coordinator,
    SpyLifecycleCoordinator LifecycleCoordinator);

  private static Fixture _build(
      StartupCoordinator coordinator,
      IPerspectiveRunnerRegistry registry,
      PerspectiveWorkerOptions? options = null,
      PerspectiveRewindOptions? rewindOptions = null,
      SpyLifecycleCoordinator? lifecycleCoordinator = null,
      IWorkNotificationListener? notificationListener = null,
      PerspectiveMetrics? metrics = null,
      bool registerLifecycleCoordinator = true) {
    var harness = new PerspectiveWorkerTestHarness();
    var lifecycle = lifecycleCoordinator ?? new SpyLifecycleCoordinator();
    var instanceProvider = new InstanceProvider();

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    if (registerLifecycleCoordinator) {
      services.AddSingleton<ILifecycleCoordinator>(lifecycle);
    }
    services.AddSingleton(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(options ?? new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 1_000_000,
        MaxConcurrentDrainConsumers = 1,
      }),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new BatchedCompletionStrategy(),
      eventTypeProvider: sp.GetRequiredService<IEventTypeProvider>(),
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: NullLogger<PerspectiveWorker>.Instance,
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: (rewindOptions is null ? null : Options.Create(rewindOptions)) ?? Options.Create(new PerspectiveRewindOptions()) ?? Options.Create(new PerspectiveRewindOptions()) ?? Options.Create(new PerspectiveRewindOptions()),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      leaseRenewalChannel: harness.LeaseRenewalCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: notificationListener ?? new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor((Options.Create(options ?? new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 1_000_000,
        MaxConcurrentDrainConsumers = 1,
      })).Value),
      metrics: metrics);
    return new Fixture(worker, harness, coordinator, lifecycle);
  }

  private static readonly LifecycleStage[] _postLifecycleReplayStages = [
    LifecycleStage.PostAllPerspectivesDetached,
    LifecycleStage.PostAllPerspectivesInline,
    LifecycleStage.PostLifecycleDetached,
    LifecycleStage.PostLifecycleInline,
  ];

  // ============================================================
  // Orphaned-lifecycle reconciliation
  // ============================================================

  [Test]
  public async Task Startup_OrphanedLifecycles_ReplaysTerminalStagesAndRecordsCompletionAsync() {
    var orphan1 = _orphan();
    var orphan2 = _orphan();
    var coordinator = new StartupCoordinator { Orphans = [orphan1, orphan2] };
    var fx = _build(coordinator, _registryWithOnePerspective());

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await coordinator.WaitForRecordedCompletionsAsync(2, TimeSpan.FromSeconds(10));

    await Assert.That(coordinator.RecordedLifecycleCompletions).Contains(orphan1.EventId)
      .Because("Every orphaned event must get a durable completion marker after replay, or the next restart replays it again.");
    await Assert.That(coordinator.RecordedLifecycleCompletions).Contains(orphan2.EventId)
      .Because("Every orphaned event must get a durable completion marker after replay, or the next restart replays it again.");
    await Assert.That(fx.LifecycleCoordinator.AdvancedByEvent[orphan1.EventId])
      .IsEquivalentTo(_postLifecycleReplayStages)
      .Because("Reconciliation must replay exactly the four terminal stages (PostAllPerspectives + PostLifecycle, Detached then Inline) that the crash skipped.");
    await Assert.That(fx.LifecycleCoordinator.AdvancedByEvent[orphan2.EventId])
      .IsEquivalentTo(_postLifecycleReplayStages)
      .Because("Each orphan gets its own full replay.");
    await Assert.That(coordinator.CapturedLookback).IsEqualTo(TimeSpan.FromMinutes(30))
      .Because("The reconciler scans a bounded 30-minute lookback window, not the whole table.");
    await Assert.That(coordinator.CapturedPerspectivesMap!.ContainsKey(ORPHAN_EVENT_TYPE)).IsTrue()
      .Because("The registry-derived event-type → perspectives map drives the orphan query.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Startup_OrphanReplayThrowsForOneEvent_ContinuesWithRemainingOrphansAsync() {
    var poisoned = _orphan();
    var healthy = _orphan();
    var coordinator = new StartupCoordinator { Orphans = [poisoned, healthy] };
    var lifecycle = new SpyLifecycleCoordinator();
    lifecycle.ThrowOnAdvanceFor.Add(poisoned.EventId);
    var fx = _build(coordinator, _registryWithOnePerspective(), lifecycleCoordinator: lifecycle);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await coordinator.WaitForRecordedCompletionsAsync(1, TimeSpan.FromSeconds(10));

    await Assert.That(coordinator.RecordedLifecycleCompletions).Contains(healthy.EventId)
      .Because("Per-orphan error isolation: a poisoned orphan must not block replay of the remaining orphans.");
    await Assert.That(coordinator.RecordedLifecycleCompletions.Contains(poisoned.EventId)).IsFalse()
      .Because("A failed replay must NOT record completion — the next startup retries it.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Startup_LargeOrphanBacklog_DrainsInBoundedPassesAsync() {
    // A backlog larger than one pass's cap must drain through REPEATED bounded passes — one
    // capped query + one capped replay batch per pass — instead of a single unbounded scan
    // that can stall the host past its liveness budget on a large store (and every probe kill
    // orphans MORE lifecycles, making that loop self-sustaining).
    var fullBatch = Enumerable.Range(0, 100).Select(_ => _orphan()).ToList();
    var tailBatch = new List<OrphanedLifecycleEvent> { _orphan(), _orphan(), _orphan() };
    var coordinator = new StartupCoordinator();
    coordinator.OrphanBatches.Enqueue(fullBatch);
    coordinator.OrphanBatches.Enqueue(tailBatch);
    var fx = _build(coordinator, _registryWithOnePerspective());

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await coordinator.WaitForRecordedCompletionsAsync(103, TimeSpan.FromSeconds(60));

    await Assert.That(coordinator.OrphanQueryCount).IsEqualTo(2)
      .Because("a FULL first batch means more may remain — the reconciler must run another " +
               "bounded pass; the partial second batch means drained, so it must stop there.");
    await Assert.That(coordinator.CapturedMaxOrphans).IsEqualTo(100)
      .Because("the cap belongs in the query — fetching an unbounded orphan set to replay a " +
               "bounded batch is the same stall one layer down.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Startup_NoPerspectivesRegistered_SkipsOrphanReconciliationAsync() {
    var coordinator = new StartupCoordinator();
    var fx = _build(coordinator, new Registry([]));

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    // The rewind scan runs strictly AFTER orphan reconciliation — reaching it proves the
    // reconciler already returned.
    await coordinator.WaitForRewindQueriesAsync(1, TimeSpan.FromSeconds(10));

    await Assert.That(coordinator.OrphanQueryCalled.Task.IsCompleted).IsFalse()
      .Because("With no registered perspectives there is no perspectives-per-event map, so the orphan query must never run.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Startup_OrphanQueryThrows_StartupContinuesToRewindScanAsync() {
    var coordinator = new StartupCoordinator { ThrowOnOrphanQuery = true };
    var fx = _build(coordinator, _registryWithOnePerspective());

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await coordinator.OrphanQueryCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await coordinator.WaitForRewindQueriesAsync(1, TimeSpan.FromSeconds(10));

    await Assert.That(fx.Worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("Reconciliation is best-effort: a failed orphan query must be swallowed so the worker still starts.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Startup rewind scan
  // ============================================================

  [Test]
  public async Task Startup_RewindScanClean_QueriesExactlyOnceAsync() {
    var coordinator = new StartupCoordinator();  // queue empty → every query returns []
    var fx = _build(coordinator, _registryWithOnePerspective());

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await coordinator.WaitForRewindQueriesAsync(1, TimeSpan.FromSeconds(10));

    // Prove the scan finished (worker reached the channel loop) by pushing a drain cycle through.
    var cycleDone = new SemaphoreSlim(0, int.MaxValue);
    fx.Worker.OnBatchCycleComplete += () => cycleDone.Release();
    await fx.Harness.EnqueueDrainStreamAsync(Guid.CreateVersion7(), cts.Token);
    await Assert.That(await cycleDone.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();

    await Assert.That(coordinator.RewindQueryCount).IsEqualTo(1)
      .Because("A clean scan (zero RewindRequired cursors) must return after one query — no blocking re-poll loop.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Startup_RewindScanBackgroundMode_DoesNotRepollAsync() {
    var coordinator = new StartupCoordinator();
    coordinator.RewindResults.Enqueue([
      new RewindCursorInfo(Guid.CreateVersion7(), "Test.Perspectives.OrderPerspective", null, Guid.CreateVersion7())
    ]);
    var fx = _build(coordinator, _registryWithOnePerspective(),
      rewindOptions: new PerspectiveRewindOptions { StartupRewindMode = RewindStartupMode.Background });

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await coordinator.WaitForRewindQueriesAsync(1, TimeSpan.FromSeconds(10));

    var cycleDone = new SemaphoreSlim(0, int.MaxValue);
    fx.Worker.OnBatchCycleComplete += () => cycleDone.Release();
    await fx.Harness.EnqueueDrainStreamAsync(Guid.CreateVersion7(), cts.Token);
    await Assert.That(await cycleDone.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();

    await Assert.That(coordinator.RewindQueryCount).IsEqualTo(1)
      .Because("Background mode logs the pending rewinds and hands them to the normal channel loop — it must NOT block startup re-polling.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Startup_RewindScanBlockingMode_RepollsUntilNoRewindCursorsRemainAsync() {
    var coordinator = new StartupCoordinator();
    coordinator.RewindResults.Enqueue([
      new RewindCursorInfo(Guid.CreateVersion7(), "Test.Perspectives.OrderPerspective", null, Guid.CreateVersion7())
    ]);
    // Second (and later) queries return [] → the blocking loop exits after one re-poll.
    var fx = _build(coordinator, _registryWithOnePerspective(),
      options: new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 20,  // blocking-scan re-poll cadence (production timer)
        MaxConcurrentDrainConsumers = 1,
      },
      rewindOptions: new PerspectiveRewindOptions { StartupRewindMode = RewindStartupMode.Blocking });

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await coordinator.WaitForRewindQueriesAsync(2, TimeSpan.FromSeconds(10));

    await Assert.That(coordinator.RewindQueryCount).IsEqualTo(2)
      .Because("Blocking mode must re-query until the RewindRequired set drains: one initial query returning work + one re-poll returning clean.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Startup_RewindScanDisabled_NeverQueriesAsync() {
    var coordinator = new StartupCoordinator();
    var fx = _build(coordinator, _registryWithOnePerspective(),
      rewindOptions: new PerspectiveRewindOptions { StartupScanEnabled = false });

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);

    var cycleDone = new SemaphoreSlim(0, int.MaxValue);
    fx.Worker.OnBatchCycleComplete += () => cycleDone.Release();
    await fx.Harness.EnqueueDrainStreamAsync(Guid.CreateVersion7(), cts.Token);
    await Assert.That(await cycleDone.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue()
      .Because("The worker must reach the channel loop without running the scan.");

    await Assert.That(coordinator.RewindQueryCount).IsEqualTo(0)
      .Because("StartupScanEnabled=false must skip the rewind query entirely.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Startup_RewindScanThrows_WorkerStillProcessesWorkAsync() {
    var coordinator = new StartupCoordinator { ThrowOnRewindQuery = true };
    var fx = _build(coordinator, _registryWithOnePerspective());

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await coordinator.WaitForRewindQueriesAsync(1, TimeSpan.FromSeconds(10));

    var cycleDone = new SemaphoreSlim(0, int.MaxValue);
    fx.Worker.OnBatchCycleComplete += () => cycleDone.Release();
    await fx.Harness.EnqueueDrainStreamAsync(Guid.CreateVersion7(), cts.Token);
    await Assert.That(await cycleDone.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue()
      .Because("The startup scan is best-effort: a thrown scan must be swallowed and the channel loop must still process work.");
    await Assert.That(fx.Worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("A failed startup scan must not fault the worker.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Periodic statistics + stale-tracking cleanup
  // ============================================================

  [Test]
  public async Task Maintenance_StatisticsGatherAtSixtyCycles_SurvivesFailureAndFiresAgainAsync() {
    var coordinator = new StartupCoordinator { ThrowOnFirstGatherStatistics = true };
    var lifecycle = new SpyLifecycleCoordinator { CleanupReturnValue = 3 };
    var fx = _build(coordinator, _registryWithOnePerspective(),
      options: new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 1_000_000,
        MaxConcurrentDrainConsumers = 1,
        DrainBatcher = new SlidingWindowBatcherOptions {
          MaxSize = 1,  // one drain stream per cycle → 1 cycle per enqueued id
          SlidingWindow = TimeSpan.FromMilliseconds(1),
          MaxWait = TimeSpan.FromMilliseconds(1),
        },
      },
      lifecycleCoordinator: lifecycle);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);

    // 121 one-stream cycles: statistics fire at cycle 60 (throws — must be swallowed)
    // and again at cycle 120 (succeeds — proves the loop survived the failure).
    for (var i = 0; i < 121; i++) {
      await fx.Harness.EnqueueDrainStreamAsync(Guid.CreateVersion7(), cts.Token);
    }
    await coordinator.WaitForGatherStatisticsAsync(2, TimeSpan.FromSeconds(30));

    await Assert.That(coordinator.GatherStatisticsCount).IsEqualTo(2)
      .Because("Statistics gather every 60 batch cycles; 121 cycles → exactly two gathers (cycle 60 + cycle 120).");
    await Assert.That(fx.Worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("A throwing statistics gather is non-critical and must never take down the consumer loop.");
    await Assert.That(lifecycle.CleanupCalls).IsGreaterThanOrEqualTo(1)
      .Because("Stale-tracking cleanup runs every 10 batch cycles — 121 cycles must have triggered it.");
    await Assert.That(lifecycle.LastCleanupThreshold).IsEqualTo(TimeSpan.FromMinutes(5))
      .Because("Tracking older than 5 minutes is considered stale — the documented cleanup threshold.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Maintenance_SixtyCyclesWithMetricsAndNoLifecycleCoordinator_PublishesThePendingCountAsync() {
    var coordinator = new StartupCoordinator { PendingPerspectiveEvents = 42 };
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var fx = _build(coordinator, _registryWithOnePerspective(),
      options: new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 1_000_000,
        MaxConcurrentDrainConsumers = 1,
        DrainBatcher = new SlidingWindowBatcherOptions {
          MaxSize = 1,  // one drain stream per cycle → 1 cycle per enqueued id
          SlidingWindow = TimeSpan.FromMilliseconds(1),
          MaxWait = TimeSpan.FromMilliseconds(1),
        },
      },
      metrics: metrics,
      registerLifecycleCoordinator: false);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);

    // 60 one-stream cycles. The stale-tracking cleanup runs at cycles 10 through 60 with no lifecycle
    // coordinator to clean; the statistics gather at cycle 60 runs after it in the same cycle, so reaching
    // the gather proves every cleanup before it passed.
    for (var i = 0; i < 60; i++) {
      await fx.Harness.EnqueueDrainStreamAsync(Guid.CreateVersion7(), cts.Token);
    }
    await coordinator.WaitForGatherStatisticsAsync(1, TimeSpan.FromSeconds(30));
    await cts.CancelAsync();
    // The gauge is set after the gather returns, inside the same cycle: the body has to have finished.
    await fx.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(fx.Worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("a host with no lifecycle coordinator has nothing stale to clean, which is not a failure");
    var pending = meters.GetByName("whizbang.perspective.pending_events");
    await Assert.That(pending).Count().IsEqualTo(1);
    await Assert.That(pending[0].Value).IsEqualTo(42d)
      .Because("the pending-events gauge publishes the count the periodic statistics gather returned");
  }

  // ============================================================
  // Orphan reconciliation: pass budget and stop requests
  // ============================================================

  [Test]
  public async Task Startup_OrphanBacklogThatNeverDrains_StopsAfterThePassBudgetAsync() {
    // Every pass returns a full batch, so the backlog never reads as drained: only the pass budget ends it.
    var fullBatch = Enumerable.Range(0, 100).Select(_ => _orphan()).ToList();
    var coordinator = new StartupCoordinator { Orphans = fullBatch };
    var fx = _build(coordinator, _registryWithOnePerspective());

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    // The rewind scan runs strictly after reconciliation, so reaching it proves reconciliation returned.
    await coordinator.WaitForRewindQueriesAsync(1, TimeSpan.FromSeconds(60));

    await Assert.That(coordinator.OrphanQueryCount).IsEqualTo(20)
      .Because("reconciliation runs at most 20 bounded passes, so a backlog that never drains cannot hold "
        + "startup past the liveness budget");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Startup_StopRequestedDuringOrphanReconciliation_RunsNoFurtherPassAsync() {
    var fullBatch = Enumerable.Range(0, 100).Select(_ => _orphan()).ToList();
    using var cts = new CancellationTokenSource();
    // The stop arrives as the last orphan of the first full pass is recorded, between passes.
    var coordinator = new StartupCoordinator {
      Orphans = fullBatch,
      OnRecordedCompletion = recorded => {
        if (recorded == fullBatch.Count) {
          cts.Cancel();
        }
      }
    };
    var fx = _build(coordinator, _registryWithOnePerspective());

    await fx.Worker.StartAsync(cts.Token);
    await coordinator.WaitForRecordedCompletionsAsync(fullBatch.Count, TimeSpan.FromSeconds(30));
    await fx.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(coordinator.OrphanQueryCount).IsEqualTo(1)
      .Because("the first pass returned a full batch, which would ordinarily start a second pass; the stop "
        + "request ends reconciliation between passes instead");
    await Assert.That(coordinator.RecordedLifecycleCompletions.Count).IsEqualTo(fullBatch.Count);

    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Startup_RewindScanOnDisposedServices_PropagatesRatherThanLoggingAScanErrorAsync() {
    // An ObjectDisposedException means the host is tearing the container down. The scan's best-effort
    // catch is deliberately not for that: it would log a scan error for what is a shutdown.
    var coordinator = new StartupCoordinator { RewindQueryException = new ObjectDisposedException("service provider") };
    var fx = _build(coordinator, _registryWithOnePerspective());

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await coordinator.WaitForRewindQueriesAsync(1, TimeSpan.FromSeconds(10));

    await Assert.That(async () => await fx.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)))
      .Throws<ObjectDisposedException>()
      .Because("the startup scan swallows ordinary failures but lets disposal through");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // NOTIFY listener subscribe/unsubscribe
  // ============================================================

  [Test]
  public async Task NotificationListener_SubscribedOnStart_UnsubscribedOnStopAsync() {
    var listener = new FakeNotificationListener();
    var coordinator = new StartupCoordinator();
    var fx = _build(coordinator, _registryWithOnePerspective(),
      options: new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 1_000_000,
        NotifyHealthyPollingIntervalMilliseconds = 1_000_000,
        MaxConcurrentDrainConsumers = 1,
      },
      notificationListener: listener);

    await Assert.That(listener.SubscriberCount).IsEqualTo(0)
      .Because("Construction must not subscribe — only a started worker listens.");

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await listener.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(listener.SubscriberCount).IsEqualTo(1)
      .Because("ExecuteAsync must hook the perspective NOTIFY signal so inserts wake the worker without polling.");

    // Non-perspective category must be ignored; back-to-back perspective signals must
    // coalesce (second Release lands on a full wake semaphore) — neither may throw.
    listener.Fire(WorkSignalCategory.Outbox);
    listener.Fire(WorkSignalCategory.Perspective);
    listener.Fire(WorkSignalCategory.Perspective);

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);

    await Assert.That(listener.SubscriberCount).IsEqualTo(0)
      .Because("StopAsync must unsubscribe the signal handler — a leaked handler would wake a dead worker and pin it in memory.");

    // Firing after stop must be a no-op (handler already detached).
    listener.Fire(WorkSignalCategory.Perspective);
  }
}
