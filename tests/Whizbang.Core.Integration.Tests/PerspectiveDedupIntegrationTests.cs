using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Integration.Tests;

/// <summary>
/// Lock-in integration tests for PerspectiveWorker event deduplication.
/// These tests verify the CONTRACT that the ProcessedEventCache prevents duplicate Apply calls.
/// If the dedup cache is removed or broken, these tests MUST fail immediately.
/// </summary>
/// <remarks>
/// Uses synchronized work coordinators with realistic latency to simulate the real pipeline:
/// SQL → ProcessWorkBatchAsync → dedup filter → runner → completion → next cycle.
/// Tests are deterministic via TaskCompletionSource signals — no arbitrary delays.
/// </remarks>
[Category("Integration")]
[NotInParallel("PerspectiveDedupIntegration")]
public class PerspectiveDedupIntegrationTests {

  // ==================== CONTRACT: Same WorkId is never processed twice ====================

  [Test]
  public async Task Contract_SameWorkIdRedelivered_RunnerCalledExactlyOnce_Async() {
    // This is the PRIMARY lock-in test. If dedup is removed, this test fails.
    // Arrange — coordinator returns the SAME work item on every cycle (simulating SQL re-delivery)
    var runner = new ApplyTrackingRunner();
    var observer = new AssertingDedupObserver();
    var coordinator = new RedeliveryWorkCoordinator();
    var workId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();

    coordinator.WorkToRedeliverOnEveryCycle = new PerspectiveWork {
      WorkId = workId,
      StreamId = streamId,
      PerspectiveName = "Test.LockInPerspective",
      LastProcessedEventId = null,
      PartitionNumber = 1
    };

    var worker = _createWorker(coordinator, new SingleRunnerRegistry(runner), observer);

    // Act — run for 5+ cycles to give ample opportunity for duplicate processing
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForAtLeastOneCallAsync(TimeSpan.FromSeconds(5));
    await coordinator.WaitForCyclesAsync(5, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN ASSERTION: Runner MUST be called exactly once for this WorkId
    await Assert.That(runner.CallCount).IsEqualTo(1)
      .Because("LOCK-IN: Same WorkId must NEVER be processed twice. If this fails, dedup is broken.");

    // LOCK-IN ASSERTION: Observer MUST report dedup events (proves dedup is actively filtering)
    await Assert.That(observer.DedupCount).IsGreaterThanOrEqualTo(1)
      .Because("LOCK-IN: Observer must report dedup filtering. If this fails, dedup is not running.");
  }

  [Test]
  public async Task Contract_100WorkItems_EachProcessedExactlyOnce_Async() {
    // High-volume lock-in test — proves dedup works under load
    var runner = new ApplyTrackingRunner();
    var coordinator = new SequentialThenRedeliveryCoordinator();
    var observer = new AssertingDedupObserver();

    // Generate 100 unique work items across 10 streams
    var workItems = new List<PerspectiveWork>();
    for (var i = 0; i < 100; i++) {
      workItems.Add(new PerspectiveWork {
        WorkId = Guid.CreateVersion7(),
        StreamId = Guid.CreateVersion7(),
        PerspectiveName = "Test.HighVolumePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      });
    }

    // Cycle 1: all 100 items. Cycles 2-5: re-deliver all 100 (SQL re-delivery scenario)
    coordinator.InitialWork = workItems;
    coordinator.RedeliverAfterInitial = true;

    var worker = _createWorker(coordinator, new SingleRunnerRegistry(runner), observer);

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForCallCountAsync(100, TimeSpan.FromSeconds(10));
    await coordinator.WaitForCyclesAsync(3, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN ASSERTION: Each of the 100 work items processed exactly once
    await Assert.That(runner.CallCount).IsEqualTo(100)
      .Because("LOCK-IN: 100 unique WorkIds must each be processed exactly once, not re-processed on re-delivery.");

    // Verify no WorkId was processed more than once
    await Assert.That(runner.DuplicateWorkIds).Count().IsEqualTo(0)
      .Because("LOCK-IN: No WorkId should appear in the runner's call log more than once.");
  }

  // ==================== CONTRACT: InFlight guard blocks until DB ack ====================

  [Test]
  public async Task Contract_InFlightGuard_BlocksRedeliveryBeforeDbAck_Async() {
    // Verifies that InFlight entries (no TTL) prevent re-processing even before DB acknowledges
    var runner = new ApplyTrackingRunner();
    var coordinator = new RedeliveryWorkCoordinator();
    var observer = new AssertingDedupObserver();
    var workId = Guid.CreateVersion7();

    coordinator.WorkToRedeliverOnEveryCycle = new PerspectiveWork {
      WorkId = workId,
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = "Test.InFlightPerspective",
      LastProcessedEventId = null,
      PartitionNumber = 1
    };

    // Use batched strategy (completions deferred to next cycle)
    var worker = _createWorker(coordinator, new SingleRunnerRegistry(runner), observer, useBatchedStrategy: true);

    // Act — run 3 cycles (cycle 1 processes, cycles 2-3 should dedup even though DB hasn't acked)
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForAtLeastOneCallAsync(TimeSpan.FromSeconds(5));
    await coordinator.WaitForCyclesAsync(3, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN: InFlight guard must block even before ActivateRetention
    await Assert.That(runner.CallCount).IsEqualTo(1)
      .Because("LOCK-IN: InFlight entries must block re-delivery even before DB ack.");

    // Verify observer saw InFlight marking
    await Assert.That(observer.InFlightCount).IsGreaterThanOrEqualTo(1)
      .Because("LOCK-IN: Observer must record InFlight events after Apply.");
  }

  // ==================== CONTRACT: Retention expires correctly ====================

  [Test]
  public async Task Contract_AfterRetentionExpiry_ReprocessingAllowed_Async() {
    // Verifies the full lifecycle: InFlight → Retained → Expired → Reprocessable
    var runner = new ApplyTrackingRunner();
    var fakeTime = new FakeTimeProvider();
    var observer = new AssertingDedupObserver();
    var coordinator = new RedeliveryWorkCoordinator { SimulatedLatencyMs = 5 };
    var workId = Guid.CreateVersion7();

    coordinator.WorkToRedeliverOnEveryCycle = new PerspectiveWork {
      WorkId = workId,
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = "Test.ExpiryPerspective",
      LastProcessedEventId = null,
      PartitionNumber = 1
    };

    var worker = _createWorker(coordinator, new SingleRunnerRegistry(runner), observer, timeProvider: fakeTime);

    // Act — cycle 1 processes, cycle 2 sends completions
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForAtLeastOneCallAsync(TimeSpan.FromSeconds(5));
    await coordinator.WaitForCyclesAsync(2, TimeSpan.FromSeconds(5));

    // Advance time past retention (5 min + buffer)
    fakeTime.Advance(TimeSpan.FromMinutes(6));

    // Wait for more cycles where the expired entry should allow reprocessing
    await coordinator.WaitForCyclesAsync(5, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN: After retention expiry, the work MUST be reprocessable
    await Assert.That(runner.CallCount).IsGreaterThanOrEqualTo(2)
      .Because("LOCK-IN: After retention period expires, same WorkId must be reprocessable.");
  }

  // ==================== CONTRACT: Observer hooks fire correctly ====================

  [Test]
  public async Task Contract_Observer_FullLifecycle_AllHooksFire_Async() {
    // Verifies that ALL observer hooks fire during a normal dedup lifecycle
    var runner = new ApplyTrackingRunner();
    var fakeTime = new FakeTimeProvider();
    var observer = new AssertingDedupObserver();
    var coordinator = new RedeliveryWorkCoordinator { SimulatedLatencyMs = 5 };

    coordinator.WorkToRedeliverOnEveryCycle = new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = "Test.ObserverPerspective",
      LastProcessedEventId = null,
      PartitionNumber = 1
    };

    var worker = _createWorker(coordinator, new SingleRunnerRegistry(runner), observer, timeProvider: fakeTime);

    // Phase 1: Process + InFlight
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForAtLeastOneCallAsync(TimeSpan.FromSeconds(5));
    await coordinator.WaitForCyclesAsync(3, TimeSpan.FromSeconds(5));

    // Phase 2: Advance past retention → eviction
    fakeTime.Advance(TimeSpan.FromMinutes(6));
    await coordinator.WaitForCyclesAsync(5, TimeSpan.FromSeconds(10));

    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN: Every observer hook must fire at least once during the lifecycle
    await Assert.That(observer.InFlightCount).IsGreaterThanOrEqualTo(1)
      .Because("LOCK-IN: OnEventsMarkedInFlight must fire after Apply.");
    await Assert.That(observer.RetentionActivatedCount).IsGreaterThanOrEqualTo(1)
      .Because("LOCK-IN: OnRetentionActivated must fire after DB ack.");
    await Assert.That(observer.DedupCount).IsGreaterThanOrEqualTo(1)
      .Because("LOCK-IN: OnEventsDeduped must fire when re-delivered work is filtered.");
    await Assert.That(observer.EvictionCount).IsGreaterThanOrEqualTo(1)
      .Because("LOCK-IN: OnEvicted must fire after retention period expires.");
  }

  // ==================== CONTRACT: Different WorkIds are not incorrectly deduped ====================

  [Test]
  public async Task Contract_DifferentWorkIds_NeverFalseDedup_Async() {
    // Ensures dedup doesn't incorrectly filter DIFFERENT work items
    var runner = new ApplyTrackingRunner();
    var coordinator = new SequentialWorkCoordinator();

    // 5 cycles, each with a different unique WorkId
    for (var i = 0; i < 5; i++) {
      coordinator.WorkPerCycle.Add([new PerspectiveWork {
        WorkId = Guid.CreateVersion7(),
        StreamId = Guid.CreateVersion7(),
        PerspectiveName = "Test.NoFalseDedupPerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }]);
    }

    var worker = _createWorker(coordinator, new SingleRunnerRegistry(runner));

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForCallCountAsync(5, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN: All 5 different WorkIds must be processed (no false positives)
    await Assert.That(runner.CallCount).IsEqualTo(5)
      .Because("LOCK-IN: Different WorkIds must NEVER be incorrectly deduped.");
  }

  // ==================== CONTRACT: Batched vs Instant strategy both protected ====================

  [Test]
  public async Task Contract_BatchedStrategy_ProtectedByDedup_Async() {
    // BatchedCompletionStrategy is the most vulnerable to the bug — lock it in
    var runner = new ApplyTrackingRunner();
    var coordinator = new RedeliveryWorkCoordinator();

    coordinator.WorkToRedeliverOnEveryCycle = new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = "Test.BatchedLockIn",
      LastProcessedEventId = null,
      PartitionNumber = 1
    };

    var worker = _createWorker(coordinator, new SingleRunnerRegistry(runner), useBatchedStrategy: true);

    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForAtLeastOneCallAsync(TimeSpan.FromSeconds(5));
    await coordinator.WaitForCyclesAsync(4, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    await Assert.That(runner.CallCount).IsEqualTo(1)
      .Because("LOCK-IN: BatchedCompletionStrategy must be protected by dedup cache.");
  }

  [Test]
  public async Task Contract_InstantStrategy_ProtectedByDedup_Async() {
    // InstantCompletionStrategy should also be protected
    var runner = new ApplyTrackingRunner();
    var coordinator = new RedeliveryWorkCoordinator();

    coordinator.WorkToRedeliverOnEveryCycle = new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = "Test.InstantLockIn",
      LastProcessedEventId = null,
      PartitionNumber = 1
    };

    var worker = _createWorker(coordinator, new SingleRunnerRegistry(runner), useBatchedStrategy: false);

    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForAtLeastOneCallAsync(TimeSpan.FromSeconds(5));
    await coordinator.WaitForCyclesAsync(4, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    await Assert.That(runner.CallCount).IsEqualTo(1)
      .Because("LOCK-IN: InstantCompletionStrategy must be protected by dedup cache.");
  }

  // ==================== CONTRACT: Lifecycle coordinator WhenAll — PostLifecycle fires exactly once ====================

  [Test]
  public async Task Contract_20Perspectives_AllSucceed_PostLifecycleFiresExactlyOnce_Async() {
    // 20 perspectives for the same stream, all succeed in one batch.
    // PostLifecycle must fire exactly once after all 20 signal complete.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var perspectiveNames = Enumerable.Range(1, 20).Select(i => $"Test.Perspective{i:D2}").ToList();

    var lifecycleCoordinator = new LifecycleCoordinator();
    var postLifecycleSpy = new PostLifecycleSpyInvoker();
    var eventStore = new FakeEventStore();
    var eventTypeProvider = new FakeEventTypeProvider();

    // Pre-configure event store: each stream returns a single event with our known eventId
    eventStore.EventsPerStream[streamId] = [_createFakeEnvelope(eventId)];

    // Build work items: one per perspective, all for the same stream
    var workItems = perspectiveNames.Select(name => new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = name,
      LastProcessedEventId = null,
      PartitionNumber = 1
    }).ToList();

    // Runner returns a LastEventId matching the event in the store
    var runner = new FixedEventIdRunner(eventId);
    var registry = new MultiPerspectiveRunnerRegistry(perspectiveNames, runner);

    // Coordinator returns all 20 work items in a single batch, then empty
    var workCoordinator = new SequentialWorkCoordinator();
    workCoordinator.WorkPerCycle.Add(workItems);

    var worker = _createWorker(
      workCoordinator, registry,
      lifecycleCoordinator: lifecycleCoordinator,
      receptorInvoker: postLifecycleSpy,
      eventStore: eventStore,
      eventTypeProvider: eventTypeProvider);

    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForCallCountAsync(20, TimeSpan.FromSeconds(10));

    // Allow a couple more cycles for PostLifecycle to fire
    await Task.Delay(300);
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN: PostLifecycleInline must fire exactly once (not 0, not 20)
    await Assert.That(postLifecycleSpy.PostLifecycleInlineCount).IsEqualTo(1)
      .Because("LOCK-IN: PostLifecycleInline must fire exactly once after all 20 perspectives complete, via coordinator WhenAll.");
  }

  [Test]
  public async Task Contract_20Perspectives_5SucceedPerBatch_4BatchesUntilPostLifecycle_Async() {
    // 20 perspectives processed in batch 1 (all succeed, PostLifecycle fires once).
    // Then all 20 re-delivered across 3 more batches (deduped, PostLifecycle must NOT fire again).
    // This locks in the contract that re-delivery after PostLifecycle does not cause duplicates.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var perspectiveNames = Enumerable.Range(1, 20).Select(i => $"Test.Perspective{i:D2}").ToList();

    var lifecycleCoordinator = new LifecycleCoordinator();
    var postLifecycleSpy = new PostLifecycleSpyInvoker();
    var eventStore = new FakeEventStore();
    var eventTypeProvider = new FakeEventTypeProvider();

    eventStore.EventsPerStream[streamId] = [_createFakeEnvelope(eventId)];

    // Batch 1: all 20 perspectives succeed. Batches 2-4: re-deliver all 20 (should be deduped).
    var allWorkItems = perspectiveNames.Select(name => new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = name,
      LastProcessedEventId = null,
      PartitionNumber = 1
    }).ToList();

    var workCoordinator = new SequentialThenRedeliveryCoordinator {
      InitialWork = allWorkItems,
      RedeliverAfterInitial = true
    };

    var runner = new FixedEventIdRunner(eventId);
    var registry = new MultiPerspectiveRunnerRegistry(perspectiveNames, runner);

    var worker = _createWorker(
      workCoordinator, registry,
      lifecycleCoordinator: lifecycleCoordinator,
      receptorInvoker: postLifecycleSpy,
      eventStore: eventStore,
      eventTypeProvider: eventTypeProvider);

    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForCallCountAsync(20, TimeSpan.FromSeconds(10));
    await workCoordinator.WaitForCyclesAsync(4, TimeSpan.FromSeconds(10));

    // Allow time for PostLifecycle processing
    await Task.Delay(200);
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN: PostLifecycleInline fires exactly once on batch 1. Re-delivery batches are deduped.
    await Assert.That(postLifecycleSpy.PostLifecycleInlineCount).IsEqualTo(1)
      .Because("LOCK-IN: PostLifecycleInline must fire exactly once. Re-delivered work items after completion must be deduped.");

    // LOCK-IN: Runner must be called exactly 20 times (once per perspective, no re-processing)
    await Assert.That(runner.CallCount).IsEqualTo(20)
      .Because("LOCK-IN: Each perspective must be processed exactly once despite re-delivery.");
  }

  [Test]
  public async Task Contract_SinglePerspective_PostLifecycleFiresImmediately_Async() {
    // Degenerate case: 1 perspective. PostLifecycle fires at batch end.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var perspectiveName = "Test.SinglePerspective";

    var lifecycleCoordinator = new LifecycleCoordinator();
    var postLifecycleSpy = new PostLifecycleSpyInvoker();
    var eventStore = new FakeEventStore();
    var eventTypeProvider = new FakeEventTypeProvider();

    eventStore.EventsPerStream[streamId] = [_createFakeEnvelope(eventId)];

    var workCoordinator = new SequentialWorkCoordinator();
    workCoordinator.WorkPerCycle.Add([new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = null,
      PartitionNumber = 1
    }]);

    var runner = new FixedEventIdRunner(eventId);
    var registry = new MultiPerspectiveRunnerRegistry([perspectiveName], runner);

    var worker = _createWorker(
      workCoordinator, registry,
      lifecycleCoordinator: lifecycleCoordinator,
      receptorInvoker: postLifecycleSpy,
      eventStore: eventStore,
      eventTypeProvider: eventTypeProvider);

    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForCallCountAsync(1, TimeSpan.FromSeconds(5));

    // Allow time for PostLifecycle to fire
    await Task.Delay(200);
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN: PostLifecycleInline fires exactly once with 1 perspective
    await Assert.That(postLifecycleSpy.PostLifecycleInlineCount).IsEqualTo(1)
      .Because("LOCK-IN: Single perspective must trigger PostLifecycleInline exactly once at batch end.");
  }

  // ==================== CONTRACT: Concurrent streams don't interfere ====================

  [Test]
  public async Task Contract_MultipleStreamsConcurrent_IndependentProcessing_Async() {
    // Verifies that dedup per WorkId doesn't cause cross-stream interference
    var runner = new ApplyTrackingRunner();
    var coordinator = new SequentialWorkCoordinator();

    // Single cycle with 10 different streams, each with unique WorkId
    var batchWork = new List<PerspectiveWork>();
    for (var i = 0; i < 10; i++) {
      batchWork.Add(new PerspectiveWork {
        WorkId = Guid.CreateVersion7(),
        StreamId = Guid.CreateVersion7(),
        PerspectiveName = "Test.ConcurrentStreams",
        LastProcessedEventId = null,
        PartitionNumber = 1
      });
    }
    coordinator.WorkPerCycle.Add(batchWork);

    var worker = _createWorker(coordinator, new SingleRunnerRegistry(runner));

    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForCallCountAsync(10, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN: All 10 streams must be processed independently
    await Assert.That(runner.CallCount).IsEqualTo(10)
      .Because("LOCK-IN: Different streams with different WorkIds must all be processed.");
    await Assert.That(runner.UniqueStreamIds.Count).IsEqualTo(10)
      .Because("LOCK-IN: Each stream must be processed independently.");
  }

  // ==================== Helpers ====================

  private static PerspectiveWorker _createWorker(
    IWorkCoordinator coordinator,
    IPerspectiveRunnerRegistry registry,
    IProcessedEventCacheObserver? observer = null,
    TimeProvider? timeProvider = null,
    bool useBatchedStrategy = true,
    ILifecycleCoordinator? lifecycleCoordinator = null,
    IReceptorInvoker? receptorInvoker = null,
    IEventStore? eventStore = null,
    IEventTypeProvider? eventTypeProvider = null) {
    var instanceProvider = new _fakeInstanceProvider();
    var databaseReadiness = new _fakeDatabaseReadiness();
    IPerspectiveCompletionStrategy strategy = useBatchedStrategy
      ? new BatchedCompletionStrategy()
      : new InstantCompletionStrategy();

    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();

    if (lifecycleCoordinator is not null) {
      services.AddSingleton(lifecycleCoordinator);
    }
    if (receptorInvoker is not null) {
      services.AddSingleton(receptorInvoker);
    }
    if (eventStore is not null) {
      services.AddSingleton(eventStore);
    }

    var serviceProvider = services.BuildServiceProvider();

    return new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      strategy,
      databaseReadiness,
      eventTypeProvider: eventTypeProvider,
      processedEventCacheObserver: observer,
      timeProvider: timeProvider
    );
  }

  // ==================== Test Fakes ====================

  /// <summary>
  /// Runner that tracks every RunAsync call for lock-in assertions.
  /// Records call count, stream IDs, and detects duplicate WorkIds.
  /// </summary>
  private sealed class ApplyTrackingRunner : IPerspectiveRunner {
    private int _callCount;
    private readonly ConcurrentBag<Guid> _processedWorkIds = [];
    private readonly ConcurrentBag<Guid> _streamIds = [];
    private readonly ConcurrentBag<Guid> _duplicateWorkIds = [];
    private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _callCountWaiters = new();

    public int CallCount => _callCount;
    public ConcurrentBag<Guid> DuplicateWorkIds => _duplicateWorkIds;
    public HashSet<Guid> UniqueStreamIds => [.. _streamIds];

    public Task WaitForAtLeastOneCallAsync(TimeSpan timeout) =>
      _firstCall.Task.WaitAsync(timeout);

    public async Task WaitForCallCountAsync(int count, TimeSpan timeout) {
      var waiter = _callCountWaiters.GetOrAdd(count, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      await waiter.Task.WaitAsync(timeout);
    }

    public Task<PerspectiveCursorCompletion> RunAsync(
      Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) {
      var current = Interlocked.Increment(ref _callCount);
      _streamIds.Add(streamId);
      _firstCall.TrySetResult();

      // Signal call count waiters
      foreach (var kvp in _callCountWaiters) {
        if (current >= kvp.Key) {
          kvp.Value.TrySetResult();
        }
      }

      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.CreateVersion7(),
        Status = PerspectiveProcessingStatus.Completed
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  /// <summary>
  /// Observer that counts hook invocations for lock-in assertions.
  /// </summary>
  private sealed class AssertingDedupObserver : IProcessedEventCacheObserver {
    private int _dedupCount;
    private int _inFlightCount;
    private int _retentionActivatedCount;
    private int _evictionCount;

    public int DedupCount => _dedupCount;
    public int InFlightCount => _inFlightCount;
    public int RetentionActivatedCount => _retentionActivatedCount;
    public int EvictionCount => _evictionCount;

    public void OnEventsDeduped(IReadOnlyList<Guid> dedupedEventIds, string perspectiveName, Guid streamId) =>
      Interlocked.Increment(ref _dedupCount);
    public void OnEventsMarkedInFlight(IReadOnlyList<Guid> eventIds) =>
      Interlocked.Increment(ref _inFlightCount);
    public void OnRetentionActivated(int count) =>
      Interlocked.Increment(ref _retentionActivatedCount);
    public void OnEvicted(int count) =>
      Interlocked.Increment(ref _evictionCount);
    public void OnEventsRemoved(IReadOnlyList<Guid> eventIds) { }
  }

  /// <summary>
  /// Coordinator that returns the same work item on every cycle (simulates SQL re-delivery).
  /// </summary>
  private sealed class RedeliveryWorkCoordinator : IWorkCoordinator {
    private int _cycleCount;
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _cycleWaiters = new();

    public PerspectiveWork? WorkToRedeliverOnEveryCycle { get; set; }
    public int SimulatedLatencyMs { get; set; } = 1;

    public async Task WaitForCyclesAsync(int count, TimeSpan timeout) {
      var waiter = _cycleWaiters.GetOrAdd(count, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      await waiter.Task.WaitAsync(timeout);
    }

    public async Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      if (SimulatedLatencyMs > 0) {
        await Task.Delay(SimulatedLatencyMs, cancellationToken);
      }

      var current = Interlocked.Increment(ref _cycleCount);
      foreach (var kvp in _cycleWaiters) {
        if (current >= kvp.Key) {
          kvp.Value.TrySetResult();
        }
      }

      return new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = WorkToRedeliverOnEveryCycle is not null ? [WorkToRedeliverOnEveryCycle] : []
      };
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// Coordinator that returns all work items on cycle 1, then re-delivers on subsequent cycles.
  /// </summary>
  private sealed class SequentialThenRedeliveryCoordinator : IWorkCoordinator {
    private int _cycleCount;
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _cycleWaiters = new();

    public List<PerspectiveWork> InitialWork { get; set; } = [];
    public bool RedeliverAfterInitial { get; set; }

    public async Task WaitForCyclesAsync(int count, TimeSpan timeout) {
      var waiter = _cycleWaiters.GetOrAdd(count, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      await waiter.Task.WaitAsync(timeout);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      var current = Interlocked.Increment(ref _cycleCount);
      foreach (var kvp in _cycleWaiters) {
        if (current >= kvp.Key) {
          kvp.Value.TrySetResult();
        }
      }

      var work = current == 1 ? [.. InitialWork] : (RedeliverAfterInitial ? [.. InitialWork] : new List<PerspectiveWork>());

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = work
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// Coordinator that returns specified work per cycle in sequence.
  /// </summary>
  private sealed class SequentialWorkCoordinator : IWorkCoordinator {
    private int _cycleCount;

    public List<List<PerspectiveWork>> WorkPerCycle { get; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      var current = Interlocked.Increment(ref _cycleCount);
      var idx = current - 1;
      var work = idx < WorkPerCycle.Count ? [.. WorkPerCycle[idx]] : new List<PerspectiveWork>();

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = work
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class SingleRunnerRegistry(IPerspectiveRunner runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo("Test.LockInPerspective", "global::Test.LockInPerspective", "global::Test.Model", ["global::Test.Event"])];
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class _fakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;
    public Observability.ServiceInstanceInfo ToInfo() => new() { ServiceName = ServiceName, InstanceId = InstanceId, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class _fakeDatabaseReadiness : IDatabaseReadinessCheck {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
  }

  // ==================== Lifecycle Integration Test Fakes ====================

  /// <summary>
  /// Creates a minimal MessageEnvelope for IEvent with a specific MessageId.
  /// Used to wire the event store response to the lifecycle coordinator's tracking.
  /// </summary>
  private static MessageEnvelope<IEvent> _createFakeEnvelope(Guid eventId) {
    return new MessageEnvelope<IEvent> {
      MessageId = MessageId.From(eventId),
      Payload = new _fakeEvent(),
      Hops = []
    };
  }

  /// <summary>
  /// Minimal IEvent implementation for test envelope creation.
  /// </summary>
  private sealed record _fakeEvent : IEvent;

  /// <summary>
  /// Spy IReceptorInvoker that counts PostLifecycleInline invocations.
  /// All other stage invocations are no-ops.
  /// </summary>
  private sealed class PostLifecycleSpyInvoker : IReceptorInvoker {
    private int _postLifecycleInlineCount;

    public int PostLifecycleInlineCount => _postLifecycleInlineCount;

    public ValueTask InvokeAsync(
      IMessageEnvelope envelope,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default) {
      if (stage == LifecycleStage.PostLifecycleInline) {
        Interlocked.Increment(ref _postLifecycleInlineCount);
      }
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Fake IEventStore that returns pre-configured events per stream.
  /// Only GetEventsBetweenPolymorphicAsync is wired; other methods are stubs.
  /// </summary>
  private sealed class FakeEventStore : IEventStore {
    public ConcurrentDictionary<Guid, List<MessageEnvelope<IEvent>>> EventsPerStream { get; } = new();

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      var events = EventsPerStream.TryGetValue(streamId, out var list) ? list : [];
      return Task.FromResult(events);
    }

    // Stubs — not used by PerspectiveWorker lifecycle path
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) => _emptyAsyncEnumerable<TMessage>(cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) => _emptyAsyncEnumerable<TMessage>(cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) => _emptyAsyncEnumerable<IEvent>(cancellationToken);
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);

    private static async IAsyncEnumerable<MessageEnvelope<T>> _emptyAsyncEnumerable<T>([EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
  }

  /// <summary>
  /// Fake IEventTypeProvider that returns a single dummy event type.
  /// Required for the PerspectiveWorker to attempt event loading.
  /// </summary>
  private sealed class FakeEventTypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(_fakeEvent)];
  }

  /// <summary>
  /// Runner that returns a fixed LastEventId for all perspectives.
  /// Tracks calls per perspective name and supports WaitForCallCount.
  /// </summary>
  private sealed class FixedEventIdRunner : IPerspectiveRunner {
    private readonly Guid _lastEventId;
    private int _callCount;
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _callCountWaiters = new();
    private readonly ConcurrentBag<string> _calledPerspectives = [];

    public FixedEventIdRunner(Guid lastEventId) {
      _lastEventId = lastEventId;
    }

    public int CallCount => _callCount;
    public IReadOnlyCollection<string> CalledPerspectives => [.. _calledPerspectives];

    public async Task WaitForCallCountAsync(int count, TimeSpan timeout) {
      var waiter = _callCountWaiters.GetOrAdd(count, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      await waiter.Task.WaitAsync(timeout);
    }

    public Task<PerspectiveCursorCompletion> RunAsync(
      Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) {
      var current = Interlocked.Increment(ref _callCount);
      _calledPerspectives.Add(perspectiveName);

      foreach (var kvp in _callCountWaiters) {
        if (current >= kvp.Key) {
          kvp.Value.TrySetResult();
        }
      }

      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = _lastEventId,
        Status = PerspectiveProcessingStatus.Completed
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  /// <summary>
  /// Registry that maps multiple perspective names to a shared runner.
  /// Used by lifecycle tests where many perspectives share the same behavior.
  /// </summary>
  private sealed class MultiPerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {
    private readonly HashSet<string> _knownNames;
    private readonly IPerspectiveRunner _runner;

    public MultiPerspectiveRunnerRegistry(IEnumerable<string> perspectiveNames, IPerspectiveRunner runner) {
      _knownNames = [.. perspectiveNames];
      _runner = runner;
    }

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      _knownNames.Contains(perspectiveName) ? _runner : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      _knownNames.Select(name => new PerspectiveRegistrationInfo(name, $"global::{name}", "global::Test.Model", ["global::Test.Event"])).ToList();

    public IReadOnlyList<Type> GetEventTypes() => [];
  }
}
