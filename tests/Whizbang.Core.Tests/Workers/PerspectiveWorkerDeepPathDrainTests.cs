using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Deep-path coverage for PerspectiveWorker drain mode:
/// - Deserialization failure / empty-deserialization short-circuits
/// - Batch cursor prefetch hydration (event_id + commit_sequence halves)
/// - Cold-cache per-perspective cursor fallback
/// - Cooldown partition (all-cooled short-circuit + mixed cooled/fresh)
/// - Commit-sequence inversion detection routing to the 5-arg RewindAndRunAsync
/// - Slice-30 refetch loop (empty refetch, throwing refetch, foreign-stream refetch, min-batch gate)
/// - Unhandled OperationCanceledException isolation between sibling perspectives
/// - PERF debug logging branch for large drains
/// - Lease-deadline cancellation routed to the failure path (FakeTimeProvider driven)
/// - Runner and PostPerspective receptor failures routed to the failure path
/// - Buffered (BatchedCompletionStrategy) completions/failures flushed onto the Phase C channels
/// </summary>
public class PerspectiveWorkerDeepPathDrainTests {

  private const string PERSPECTIVE = "Drain.DeepPerspective";

  [Test]
  public async Task DrainMode_DeserializeThrows_SkipsBatchWithoutRunningPerspectivesAsync() {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore { ThrowOnDeserialize = true };
    var runner = new DrainRunner();
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry);
    var cycleComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnBatchCycleComplete += () => cycleComplete.TrySetResult();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await cycleComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(eventStore.DeserializeCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(0)
      .Because("A deserialization failure must skip the drain batch without invoking any runner");
    await Assert.That(coordinator.Completions.Count).IsEqualTo(0);
  }

  [Test]
  public async Task DrainMode_DeserializeReturnsEmpty_SkipsBatchWithoutRunningPerspectivesAsync() {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([]); // zero typed events from non-zero raw rows
    var runner = new DrainRunner();
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry);
    var cycleComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnBatchCycleComplete += () => cycleComplete.TrySetResult();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await cycleComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(eventStore.DeserializeCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(0)
      .Because("Zero typed events means there is nothing to apply");
  }

  [Test]
  public async Task DrainMode_BatchCursorPrefetch_HydratesCacheAndDetectsCommitSequenceInversionAsync() {
    // Arrange — persisted cursor at commit_sequence 100; the pending event is stamped 50,
    // i.e. strictly behind the cursor → commit-sequence inversion → 5-arg RewindAndRunAsync.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var workId = Guid.CreateVersion7();
    var cursorEventId = Guid.CreateVersion7(); // newer than eventId

    var coordinator = new DrainWorkCoordinator {
      CursorsBatchToReturn = [
        new PerspectiveCursorInfo {
          StreamId = streamId,
          PerspectiveName = PERSPECTIVE,
          LastEventId = cursorEventId,
          LastCommitSequence = 100,
          Status = PerspectiveProcessingStatus.None
        }
      ]
    };
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, workId, commitSequence: 50)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("inverted"))]);
    var runner = new DrainRunner();
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry);

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — prefetch hydrated the cache (single batch call, no per-perspective fallback)
    await Assert.That(coordinator.GetCursorsBatchCallCount).IsEqualTo(1);
    await Assert.That(coordinator.GetPerspectiveCursorCallCount).IsEqualTo(0)
      .Because("A hydrated cursor cache must not trigger the cold-cache per-perspective fallback");

    // Assert — the inversion was detected against the commit-sequence cursor and routed to
    // the commit-sequence-anchored rewind overload with the violator's stamp.
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(0);
    await Assert.That(runner.RewindCalls.Count).IsEqualTo(1);
    runner.RewindCalls.TryPeek(out var rewind);
    await Assert.That(rewind.TriggerEventId).IsEqualTo(eventId);
    await Assert.That(rewind.CommitSequence).IsEqualTo(50L);
  }

  [Test]
  public async Task DrainMode_ColdCursorCache_FallsBackToPersistedCursorAsync() {
    // Arrange — batch prefetch returns nothing; the per-perspective fallback reads the
    // persisted cursor and forwards it to the runner as lastProcessedEventId.
    var persistedCursorEventId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7(); // newer than the persisted cursor → forward apply
    var coordinator = new DrainWorkCoordinator();
    coordinator.CursorOverrides[(PERSPECTIVE, streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = PERSPECTIVE,
      LastEventId = persistedCursorEventId,
      LastCommitSequence = 10,
      Status = PerspectiveProcessingStatus.None
    };
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7(), commitSequence: 20)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("cold-cache"))]);
    var runner = new DrainRunner();
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry);

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — the fallback read the persisted cursor and the runner saw it
    await Assert.That(coordinator.GetPerspectiveCursorCallCount).IsGreaterThanOrEqualTo(1)
      .Because("A cold cursor cache must fall back to the persisted wh_perspective_cursors row");
    await Assert.That(runner.ObservedCursors).Contains(persistedCursorEventId);
    await Assert.That(runner.RewindCalls.Count).IsEqualTo(0)
      .Because("commit_sequence 20 is ahead of cursor 10 — no inversion");
  }

  [Test]
  public async Task DrainMode_AllEventsCooled_SkipsApplyButSignalsLifecycleCompletionAsync() {
    // Arrange — the event's work_id is already in the cooldown cache (prior drain applied it).
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var workId = Guid.CreateVersion7();
    var cooldownCache = new RecentlyProcessedEventCache(new SystemTimeProvider());
    cooldownCache.MarkProcessed(workId);

    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, workId)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("cooled"))]);
    var runner = new DrainRunner();
    var registry = _registry(runner);
    var lifecycle = new RecordingLifecycleCoordinator();

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, registry,
      lifecycleCoordinator: lifecycle,
      cooldownCache: cooldownCache,
      logger: new AlwaysEnabledLogger());

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await lifecycle.FirstPerspectiveSignal.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — apply was skipped but the WhenAll bookkeeping was still satisfied
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(0)
      .Because("Fully-cooled batches must short-circuit before RunWithEventsAsync");
    await Assert.That(lifecycle.PerspectiveSignals).Contains((eventId, PERSPECTIVE))
      .Because("Cooldown-skipped events must still signal perspective completion or PostAllPerspectives never fires");
  }

  [Test]
  public async Task DrainMode_MixedCooledAndFresh_AppliesOnlyFreshEventsAsync() {
    // Arrange — two events: e1's work row is cooled, e2 is fresh. Only e2 reaches the runner.
    var streamId = Guid.CreateVersion7();
    var eventId1 = Guid.CreateVersion7();
    var eventId2 = Guid.CreateVersion7();
    var workId1 = Guid.CreateVersion7();
    var workId2 = Guid.CreateVersion7();
    var cooldownCache = new RecentlyProcessedEventCache(new SystemTimeProvider());
    cooldownCache.MarkProcessed(workId1);

    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, eventId1, workId1),
      _raw(streamId, eventId2, workId2)
    ]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([
      _envelope(eventId1, new DrainDeepEvent("cooled")),
      _envelope(eventId2, new DrainDeepEvent("fresh"))
    ]);
    var runner = new DrainRunner();
    var registry = _registry(runner);
    var lifecycle = new RecordingLifecycleCoordinator();

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, registry,
      lifecycleCoordinator: lifecycle,
      cooldownCache: cooldownCache);

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — the runner saw exactly the fresh remainder
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(1);
    runner.ReceivedBatches.TryPeek(out var batch);
    await Assert.That(batch).IsNotNull();
    await Assert.That(batch ?? []).Count().IsEqualTo(1);
    await Assert.That((batch ?? [])[0]).IsEqualTo(eventId2);

    // Assert — the cooled event still got its completion signal
    await Assert.That(lifecycle.PerspectiveSignals).Contains((eventId1, PERSPECTIVE));
  }

  [Test]
  public async Task DrainMode_RefetchReturnsEmpty_ExitsLoopAfterSecondFetchAsync() {
    // Arrange — 2 fresh events (>= DrainLoopRefetchMinBatch default 2) → refetch fires; the
    // refetch returns no rows → loop exits.
    var streamId = Guid.CreateVersion7();
    var eventId1 = Guid.CreateVersion7();
    var eventId2 = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, eventId1, Guid.CreateVersion7()),
      _raw(streamId, eventId2, Guid.CreateVersion7())
    ]);
    // no second response enqueued → refetch returns empty
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([
      _envelope(eventId1, new DrainDeepEvent("one")),
      _envelope(eventId2, new DrainDeepEvent("two"))
    ]);
    var runner = new DrainRunner();
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry);

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.WaitForStreamEventsCallsAsync(2, TimeSpan.FromSeconds(10));
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — exactly one refetch happened, and the first iteration applied both events
    await Assert.That(coordinator.GetStreamEventsCallCount).IsEqualTo(2);
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(1);
    runner.ReceivedBatches.TryPeek(out var batch);
    await Assert.That(batch ?? []).Count().IsEqualTo(2);
  }

  [Test]
  public async Task DrainMode_RefetchThrows_ExitsLoopWithoutFailingCompletedWorkAsync() {
    // Arrange — first fetch succeeds (2 events applied), refetch throws; the already-completed
    // first iteration must survive.
    var streamId = Guid.CreateVersion7();
    var eventId1 = Guid.CreateVersion7();
    var eventId2 = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, eventId1, Guid.CreateVersion7()),
      _raw(streamId, eventId2, Guid.CreateVersion7())
    ]);
    coordinator.EnqueueStreamEventsError(new InvalidOperationException("refetch blew up"));
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([
      _envelope(eventId1, new DrainDeepEvent("one")),
      _envelope(eventId2, new DrainDeepEvent("two"))
    ]);
    var runner = new DrainRunner();
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry);
    var cycleComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnBatchCycleComplete += () => cycleComplete.TrySetResult();

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(10));
    await cycleComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — refetch was attempted, its failure did not undo the completed apply
    await Assert.That(coordinator.GetStreamEventsCallCount).IsEqualTo(2);
    await Assert.That(coordinator.Completions.Count).IsGreaterThanOrEqualTo(1)
      .Because("A refetch failure must not abort the already-completed first iteration");
    await Assert.That(coordinator.Failures.Count).IsEqualTo(0);
  }

  [Test]
  public async Task DrainMode_RefetchReturnsForeignStream_ExitsLoopAsync() {
    // Arrange — the refetch returns rows for a DIFFERENT stream; the loop must exit because
    // the refetched group has no entry for the stream being drained.
    var streamId = Guid.CreateVersion7();
    var foreignStreamId = Guid.CreateVersion7();
    var eventId1 = Guid.CreateVersion7();
    var eventId2 = Guid.CreateVersion7();
    var foreignEventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, eventId1, Guid.CreateVersion7()),
      _raw(streamId, eventId2, Guid.CreateVersion7())
    ]);
    coordinator.EnqueueStreamEvents([_raw(foreignStreamId, foreignEventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([
      _envelope(eventId1, new DrainDeepEvent("one")),
      _envelope(eventId2, new DrainDeepEvent("two"))
    ]);
    eventStore.EnqueueDeserialized([_envelope(foreignEventId, new DrainDeepEvent("foreign"))]);
    var runner = new DrainRunner();
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry);
    var cycleComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnBatchCycleComplete += () => cycleComplete.TrySetResult();

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.WaitForStreamEventsCallsAsync(2, TimeSpan.FromSeconds(10));
    await cycleComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — only the first iteration ran the runner (foreign rows never applied here)
    await Assert.That(coordinator.GetStreamEventsCallCount).IsEqualTo(2);
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(1)
      .Because("Refetched rows for another stream must not be applied by this stream's drain loop");
  }

  [Test]
  public async Task DrainMode_SingleEventBelowMinBatch_SkipsRefetchAsync() {
    // Arrange — one event < DrainLoopRefetchMinBatch (2) → the loop must exit without refetching.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("solo"))]);
    var runner = new DrainRunner();
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry);
    var cycleComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnBatchCycleComplete += () => cycleComplete.TrySetResult();

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(10));
    await cycleComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(coordinator.GetStreamEventsCallCount).IsEqualTo(1)
      .Because("Single-event drains skip the refetch SQL round-trip");
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task DrainMode_UnhandledOceFromOnePerspective_DoesNotPoisonSiblingPerspectiveAsync() {
    // Arrange — two perspectives handle the same event type. One throws a bare OCE (no
    // cancellation requested anywhere); the sibling must still be processed.
    const string throwingPerspective = "Drain.OceThrower";
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, eventId, Guid.CreateVersion7(), perspectiveName: PERSPECTIVE),
      _raw(streamId, eventId, Guid.CreateVersion7(), perspectiveName: throwingPerspective)
    ]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("oce"))]);
    var healthyRunner = new DrainRunner();
    var oceRunner = new DrainRunner { RunWithEventsException = new OperationCanceledException("bare OCE from runner") };
    var registry = new MultiRunnerRegistry([typeof(DrainDeepEvent)]);
    registry.Add(PERSPECTIVE, healthyRunner);
    registry.Add(throwingPerspective, oceRunner);

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry);
    var cycleComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnBatchCycleComplete += () => cycleComplete.TrySetResult();

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await healthyRunner.FirstRunWithEvents.WaitAsync(TimeSpan.FromSeconds(10));
    await cycleComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — the healthy sibling applied despite the OCE from the other perspective
    await Assert.That(healthyRunner.RunWithEventsCallCount).IsEqualTo(1)
      .Because("An unhandled OCE from one perspective must not abort sibling perspectives on the stream");
    await Assert.That(oceRunner.RunWithEventsCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task DrainMode_FiveEventBatchWithDebugLogging_AppliesAllEventsAsync() {
    // Arrange — >= 5 events with Debug logging enabled exercises the PERF breakdown branch.
    var streamId = Guid.CreateVersion7();
    var eventIds = new List<Guid>();
    var raws = new List<StreamEventData>();
    var envelopes = new List<MessageEnvelope<IEvent>>();
    for (var i = 0; i < 5; i++) {
      var id = Guid.CreateVersion7();
      eventIds.Add(id);
      raws.Add(_raw(streamId, id, Guid.CreateVersion7()));
      envelopes.Add(_envelope(id, new DrainDeepEvent($"evt-{i}")));
    }
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents(raws);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized(envelopes);
    var runner = new DrainRunner();
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, registry,
      configure: opts => opts.DrainLoopMaxIterations = 1,
      logger: new AlwaysEnabledLogger());

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — one apply pass covering all five events. The worker may hand the runner the
    // batch in id/commit-sorted order rather than seed order, so assert set membership (all
    // five applied) rather than positional equality.
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(1);
    runner.ReceivedBatches.TryPeek(out var batch);
    var appliedIds = batch ?? [];
    await Assert.That(appliedIds).Count().IsEqualTo(5);
    foreach (var expectedId in eventIds) {
      await Assert.That(appliedIds).Contains(expectedId)
        .Because("Every seeded event must be applied in the single drain pass");
    }
  }

  [Test]
  public async Task DrainMode_LeaseDeadlineExceeded_RoutesToFailurePathAsync() {
    // Arrange — a runner that never completes; the LeaseHandle deadline (FakeTimeProvider
    // driven) cancels the dispatch and the worker must report a lease-deadline failure.
    var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero));
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("hung"))]);
    var runner = new DrainRunner { BlockUntilCancelled = true };
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, registry,
      timeProvider: fakeTime,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions { LeaseGraceSeconds = 4 }),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 5 }));

    // Act — wait for the hung apply to start, then advance past the 1-second lease deadline
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
    fakeTime.Advance(TimeSpan.FromSeconds(10));
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — the failure carries the lease-deadline error, not a generic exception
    coordinator.Failures.TryPeek(out var failure);
    await Assert.That(failure).IsNotNull();
    await Assert.That(failure?.PerspectiveName).IsEqualTo(PERSPECTIVE);
    await Assert.That(failure?.Error ?? string.Empty).Contains("Lease deadline exceeded");
  }

  [Test]
  public async Task DrainMode_RunnerThrows_ReportsFailureForPerspectiveAsync() {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("boom"))]);
    var runner = new DrainRunner { RunWithEventsException = new InvalidOperationException("drain apply failed") };
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    coordinator.Failures.TryPeek(out var failure);
    await Assert.That(failure).IsNotNull();
    await Assert.That(failure?.PerspectiveName).IsEqualTo(PERSPECTIVE);
    await Assert.That(failure?.Error).IsEqualTo("drain apply failed");
  }

  [Test]
  public async Task DrainMode_PostPerspectiveReceptorThrows_RoutesToFailurePathAsync() {
    // Arrange — a receptor invoker that throws at PostPerspectiveInline. The lifecycle
    // invocation helper logs and rethrows; the drain catch converts it to a failure report.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("receptor"))]);
    var runner = new DrainRunner();
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, registry,
      receptorInvoker: new ThrowAtStageInvoker(LifecycleStage.PostPerspectiveInline, new InvalidOperationException("post-perspective receptor failed")));

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(1)
      .Because("The apply itself succeeded; the receptor threw afterwards");
    coordinator.Failures.TryPeek(out var failure);
    await Assert.That(failure?.Error).IsEqualTo("post-perspective receptor failed");
  }

  [Test]
  public async Task DrainMode_BatchedStrategy_FlushesBufferedCompletionToCompletionChannelAsync() {
    // Arrange — default BatchedCompletionStrategy buffers the cursor completion; the next
    // wake cycle must flush it onto the perspective completion channel.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("buffered"))]);
    var runner = new DrainRunner();
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry, useBatchedStrategy: true);

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    var cursor = await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — the buffered completion reached the Phase C channel, not the coordinator
    await Assert.That(cursor.StreamId).IsEqualTo(streamId);
    await Assert.That(cursor.PerspectiveName).IsEqualTo(PERSPECTIVE);
    await Assert.That(coordinator.Completions.Count).IsEqualTo(0)
      .Because("BatchedCompletionStrategy routes completions through the completion channel");
  }

  [Test]
  public async Task DrainMode_BatchedStrategy_FlushesBufferedFailureToFailureChannelAsync() {
    // Arrange — a failing apply with the default BatchedCompletionStrategy: the failure is
    // buffered and the next flush writes a MessageFailure onto the failure channel.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("buffered-failure"))]);
    var runner = new DrainRunner { RunWithEventsException = new InvalidOperationException("buffered drain failure") };
    var registry = _registry(runner);
    var failureChannel = new SignalingFailureChannel();

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, registry,
      useBatchedStrategy: true,
      failureChannelOverride: failureChannel);

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await failureChannel.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — the buffered failure surfaced on the failure channel with its category + error
    failureChannel.Items.TryPeek(out var item);
    await Assert.That(item.Category).IsEqualTo(WorkCategory.PerspectiveEvent);
    await Assert.That(item.Failure?.Error).IsEqualTo("buffered drain failure");
    await Assert.That(coordinator.Failures.Count).IsEqualTo(0)
      .Because("BatchedCompletionStrategy routes failures through the failure channel");
  }

  #region Test event + helpers

  private sealed record DrainDeepEvent(string Data) : IEvent;

  private static MultiRunnerRegistry _registry(DrainRunner runner) {
    var registry = new MultiRunnerRegistry([typeof(DrainDeepEvent)]);
    registry.Add(PERSPECTIVE, runner);
    return registry;
  }

  private static StreamEventData _raw(
      Guid streamId, Guid eventId, Guid workId, long? commitSequence = null, string perspectiveName = PERSPECTIVE) => new() {
        StreamId = streamId,
        EventId = eventId,
        EventType = TypeNameFormatter.Format(typeof(DrainDeepEvent)),
        EventData = "{}",
        Metadata = null,
        Scope = null,
        EventWorkId = workId,
        PerspectiveName = perspectiveName,
        CommitSequence = commitSequence
      };

  private static MessageEnvelope<IEvent> _envelope(Guid eventId, IEvent payload) => new() {
    MessageId = new MessageId(eventId),
    Payload = payload,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = CorrelationId.New(),
        CausationId = MessageId.New(),
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.NewGuid(),
          ServiceName = "TestService",
          HostName = "test-host",
          ProcessId = 1234
        }
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private static (PerspectiveWorker Worker, PerspectiveWorkerTestHarness Harness, ServiceProvider Provider) _createWorker(
      DrainWorkCoordinator coordinator,
      DrainEventStore eventStore,
      MultiRunnerRegistry registry,
      Action<PerspectiveWorkerOptions>? configure = null,
      ILifecycleCoordinator? lifecycleCoordinator = null,
      IReceptorInvoker? receptorInvoker = null,
      RecentlyProcessedEventCache? cooldownCache = null,
      bool useBatchedStrategy = false,
      IFailureChannel? failureChannelOverride = null,
      ILogger<PerspectiveWorker>? logger = null,
      TimeProvider? timeProvider = null,
      IOptions<LeaseHandleOptions>? leaseHandleOptions = null,
      IOptions<LeaseRenewalWorkerOptions>? leaseRenewalOptions = null) {
    var instanceProvider = new FakeInstanceProvider();
    var harness = new PerspectiveWorkerTestHarness();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    if (lifecycleCoordinator is not null) {
      services.AddSingleton(lifecycleCoordinator);
    }
    if (receptorInvoker is not null) {
      services.AddSingleton(receptorInvoker);
    }
    services.AddLogging();
    var provider = services.BuildServiceProvider();

    var options = new PerspectiveWorkerOptions {
      PollingIntervalMilliseconds = 50,
      DrainBatcher = new SlidingWindowBatcherOptions {
        SlidingWindow = TimeSpan.Zero,
        MaxWait = TimeSpan.Zero,
        MaxSize = 1000
      }
    };
    configure?.Invoke(options);

    var worker = new PerspectiveWorker(
      instanceProvider,
      provider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(options),
      tracingOptions: null,
      completionStrategy: useBatchedStrategy ? null : new InstantCompletionStrategy(),
      eventTypeProvider: registry,
      logger: logger,
      timeProvider: timeProvider,
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: failureChannelOverride ?? harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      recentlyProcessedEventCache: cooldownCache,
      leaseHandleOptions: leaseHandleOptions,
      leaseRenewalOptions: leaseRenewalOptions);
    return (worker, harness, provider);
  }

  #endregion

  #region Fakes

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "DeepPathDrainTest";
    public string HostName => "test-host";
    public int ProcessId => 4243;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class DrainWorkCoordinator : IWorkCoordinator {
    private readonly ConcurrentQueue<Func<List<StreamEventData>>> _streamEventsResponses = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _streamEventsWaiters = new();
    private readonly TaskCompletionSource _firstCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _streamEventsCallCount;
    private int _cursorsBatchCallCount;
    private int _cursorCallCount;

    public List<PerspectiveCursorInfo> CursorsBatchToReturn { get; init; } = [];
    public Dictionary<(string PerspectiveName, Guid StreamId), PerspectiveCursorInfo> CursorOverrides { get; } = [];
    public ConcurrentQueue<PerspectiveCursorCompletion> Completions { get; } = new();
    public ConcurrentQueue<PerspectiveCursorFailure> Failures { get; } = new();

    public int GetStreamEventsCallCount => Volatile.Read(ref _streamEventsCallCount);
    public int GetCursorsBatchCallCount => Volatile.Read(ref _cursorsBatchCallCount);
    public int GetPerspectiveCursorCallCount => Volatile.Read(ref _cursorCallCount);
    public Task FirstCompletion => _firstCompletion.Task;
    public Task FirstFailure => _firstFailure.Task;

    public void EnqueueStreamEvents(List<StreamEventData> rows) =>
      _streamEventsResponses.Enqueue(() => rows);

    public void EnqueueStreamEventsError(Exception exception) =>
      _streamEventsResponses.Enqueue(() => throw exception);

    public Task WaitForStreamEventsCallsAsync(int count, TimeSpan timeout) {
      var tcs = _streamEventsWaiters.GetOrAdd(count, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      if (Volatile.Read(ref _streamEventsCallCount) >= count) {
        tcs.TrySetResult();
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default) {
      var count = Interlocked.Increment(ref _streamEventsCallCount);
      foreach (var waiter in _streamEventsWaiters) {
        if (count >= waiter.Key) {
          waiter.Value.TrySetResult();
        }
      }
      if (_streamEventsResponses.TryDequeue(out var response)) {
        return Task.FromResult(new List<StreamEventData>(response()));
      }
      return Task.FromResult(new List<StreamEventData>());
    }

    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(Guid[] streamIds, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _cursorsBatchCallCount);
      return Task.FromResult(new List<PerspectiveCursorInfo>(CursorsBatchToReturn));
    }

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _cursorCallCount);
      if (CursorOverrides.TryGetValue((perspectiveName, streamId), out var cursor)) {
        return Task.FromResult<PerspectiveCursorInfo?>(cursor);
      }
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      Completions.Enqueue(completion);
      _firstCompletion.TrySetResult();
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) {
      Failures.Enqueue(failure);
      _firstFailure.TrySetResult();
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private sealed class DrainEventStore : IEventStore {
    private readonly ConcurrentQueue<List<MessageEnvelope<IEvent>>> _deserializedResponses = new();
    private int _deserializeCallCount;

    public bool ThrowOnDeserialize { get; init; }
    public int DeserializeCallCount => Volatile.Read(ref _deserializeCallCount);

    public void EnqueueDeserialized(List<MessageEnvelope<IEvent>> envelopes) =>
      _deserializedResponses.Enqueue(envelopes);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) {
      Interlocked.Increment(ref _deserializeCallCount);
      if (ThrowOnDeserialize) {
        throw new InvalidOperationException("simulated deserialization failure");
      }
      if (_deserializedResponses.TryDequeue(out var next)) {
        return [.. next];
      }
      return [];
    }

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
  }

  private sealed class MultiRunnerRegistry(IReadOnlyList<Type> eventTypes) : IPerspectiveRunnerRegistry, IEventTypeProvider {
    private readonly ConcurrentDictionary<string, IPerspectiveRunner> _runners = new(StringComparer.Ordinal);

    public void Add(string perspectiveName, IPerspectiveRunner runner) => _runners[perspectiveName] = runner;

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      _runners.TryGetValue(perspectiveName, out var runner) ? runner : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [.. _runners.Keys.Select(name => new PerspectiveRegistrationInfo(
        name,
        $"global::{name}",
        "global::Test.DrainDeepModel",
        [.. eventTypes.Select(TypeNameFormatter.Format)]))];

    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class DrainRunner : IPerspectiveRunner {
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstRunWithEvents = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _runWithEventsCallCount;

    public Exception? RunWithEventsException { get; init; }
    public bool BlockUntilCancelled { get; init; }
    public ConcurrentQueue<List<Guid>> ReceivedBatches { get; } = new();
    public ConcurrentQueue<Guid?> ObservedCursors { get; } = new();
    public ConcurrentQueue<(Guid TriggerEventId, long? CommitSequence)> RewindCalls { get; } = new();
    public int RunWithEventsCallCount => Volatile.Read(ref _runWithEventsCallCount);
    public Task Started => _started.Task;
    public Task FirstRunWithEvents => _firstRunWithEvents.Task;
    public Type PerspectiveType => typeof(DrainRunner);

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) =>
      Task.FromResult(_completed(streamId, perspectiveName, lastProcessedEventId ?? Guid.Empty));

    public async Task<PerspectiveCursorCompletion> RunWithEventsAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
        IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _runWithEventsCallCount);
      ReceivedBatches.Enqueue([.. events.Select(e => e.MessageId.Value)]);
      ObservedCursors.Enqueue(lastProcessedEventId);
      _started.TrySetResult();
      _firstRunWithEvents.TrySetResult();
      if (BlockUntilCancelled) {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = cancellationToken.Register(() => blocked.TrySetResult());
        await blocked.Task;
        cancellationToken.ThrowIfCancellationRequested();
      }
      if (RunWithEventsException is not null) {
        throw RunWithEventsException;
      }
      var lastEventId = events.Count > 0 ? events[^1].MessageId.Value : Guid.Empty;
      return _completed(streamId, perspectiveName, lastEventId);
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) {
      RewindCalls.Enqueue((triggeringEventId, null));
      return Task.FromResult(_completed(streamId, perspectiveName, triggeringEventId));
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, long? triggeringCommitSequence, CancellationToken cancellationToken = default) {
      RewindCalls.Enqueue((triggeringEventId, triggeringCommitSequence));
      return Task.FromResult(_completed(streamId, perspectiveName, triggeringEventId));
    }

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    private static PerspectiveCursorCompletion _completed(Guid streamId, string perspectiveName, Guid lastEventId) => new() {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = lastEventId,
      Status = PerspectiveProcessingStatus.Completed
    };
  }

  private sealed class RecordingLifecycleCoordinator : ILifecycleCoordinator {
    private readonly TaskCompletionSource _firstPerspectiveSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<(Guid EventId, string PerspectiveName)> PerspectiveSignals { get; } = new();
    public Task FirstPerspectiveSignal => _firstPerspectiveSignal.Task;

    public ILifecycleTracking BeginTracking(Guid eventId, IMessageEnvelope envelope, LifecycleStage entryStage, MessageSource source, Guid? streamId = null, Type? perspectiveType = null) =>
      new NoOpTracking(eventId);

    public ILifecycleTracking? GetTracking(Guid eventId) => null;
    public void ExpectCompletionsFrom(Guid eventId, params PostLifecycleCompletionSource[] sources) { }
    public ValueTask SignalSegmentCompleteAsync(Guid eventId, PostLifecycleCompletionSource source, IServiceProvider scopedProvider, CancellationToken ct) =>
      ValueTask.CompletedTask;
    public void AbandonTracking(Guid eventId) { }
    public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) { }

    public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) {
      PerspectiveSignals.Enqueue((eventId, perspectiveName));
      _firstPerspectiveSignal.TrySetResult();
      return false;
    }

    public bool AreAllPerspectivesComplete(Guid eventId) => false;
    public int CleanupStaleTracking(TimeSpan inactivityThreshold) => 0;

    private sealed class NoOpTracking(Guid eventId) : ILifecycleTracking {
      public Guid EventId => eventId;
      public LifecycleStage CurrentStage => LifecycleStage.PrePerspectiveDetached;
      public bool IsComplete => false;
      public ValueTask AdvanceToAsync(LifecycleStage stage, IServiceProvider scopedProvider, CancellationToken ct) => ValueTask.CompletedTask;
      public ValueTask DrainDetachedAsync() => ValueTask.CompletedTask;
    }
  }

  private sealed class ThrowAtStageInvoker(LifecycleStage throwAtStage, Exception exception) : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      if (stage == throwAtStage) {
        throw exception;
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class SignalingFailureChannel : IFailureChannel {
    private readonly TaskCompletionSource _firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<(WorkCategory Category, MessageFailure Failure)> Items { get; } = new();
    public Task FirstFailure => _firstFailure.Task;

    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      Items.Enqueue((category, failure));
      _firstFailure.TrySetResult();
      return ValueTask.CompletedTask;
    }
  }

  private sealed class AlwaysEnabledLogger : ILogger<PerspectiveWorker> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      _ = formatter(state, exception);
    }
  }

  #endregion
}
