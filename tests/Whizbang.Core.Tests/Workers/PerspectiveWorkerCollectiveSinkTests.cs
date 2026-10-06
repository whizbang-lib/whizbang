// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Execution;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tags;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Unit tests for the PerspectiveWorker collective-event sink seam (<c>_processCollectiveSinkAsync</c>):
/// a perspective-work item for the <see cref="CollectiveRouting.SINK_PERSPECTIVE_NAME"/> sink is dispatched
/// through <see cref="ICollectiveDispatcher"/> exactly once and bypasses the per-stream runner. Covers the
/// dispatch path, the not-configured short-circuit, and the no-collective-event short-circuit.
/// </summary>
[NotInParallel("CollectiveSinkWorker")]
public partial class PerspectiveWorkerCollectiveSinkTests {

  [Test]
  public async Task CollectiveSink_DispatchesEventOnceAndSkipsRunner_Async() {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new RecordingDispatcher();
    var runner = new TrackingRunner();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    // Deterministic wait on the dispatch itself — the channel consumer processes the claimed work
    // asynchronously, so a claim-cycle count can tick over (and cancel the worker) before it runs.
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    // Await the worker BODY, not StartAsync's task: BackgroundService.StartAsync hands back
    // Task.CompletedTask as soon as ExecuteAsync is queued, so awaiting it was no shutdown barrier at
    // all and every assertion below could read state the worker's finally blocks had not settled yet.
    // SuppressThrowing because a body leaving through a cancellation catch settles RanToCompletion or
    // Canceled depending on thread-pool timing — either is a clean stop.
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Calls.Count).IsEqualTo(1)
      .Because("The collective event is dispatched exactly once.");
    await Assert.That(dispatcher.Calls[0].EventId).IsEqualTo(eventId);
    await Assert.That(ReferenceEquals(dispatcher.Calls[0].Event, collectiveEvent)).IsTrue();
    await Assert.That(runner.RunWithEventsCount).IsEqualTo(0)
      .Because("The sink bypasses the per-stream runner.");
  }

  [Test]
  public async Task CollectiveSink_LongApply_RenewsWorkLeasePerReportedBatchAsync() {
    // The dispatcher reports 3 apply batches; the worker must renew the sink work item's lease on
    // EACH report — without renewal, an apply spanning many batches outlives its lease and the
    // (idempotent) work is redelivered, re-running the full batched UPDATE for nothing.
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-lease") };
    var dispatcher = new BatchReportingDispatcher(batches: 3);
    var leaseChannel = new Whizbang.Testing.Workers.CapturingLeaseRenewalChannel();
    var sinkWork = _sinkWork(streamId);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      leaseRenewalChannel: leaseChannel);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    // Deterministic wait: BatchReportingDispatcher signals FirstDispatch only AFTER all 3
    // onBatchApplied reports, and each report enqueues its renewal synchronously before returning —
    // so every renewal is captured by the time this completes. A claim-cycle count instead races
    // the channel consumer: cycle 2 can tick over (cancelling the worker) before the dispatch runs.
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    var renewals = leaseChannel.Items.Where(i => i.id == sinkWork.WorkId).ToList();
    await Assert.That(renewals.Count).IsEqualTo(3)
      .Because("each reported apply batch must renew the leased sink work item — the lease then " +
               "tracks the apply's true duration instead of racing it.");
    await Assert.That(renewals.All(r => r.category == WorkCategory.PerspectiveEvent)).IsTrue();
  }

  [Test]
  public async Task CollectiveSink_LongApply_RenewalLandsThroughRealWorkerAndRegistryAsync() {
    // The sibling above proves the ENQUEUE with a capturing fake; this proves the renewal LANDS.
    // In production LeaseRenewalWorker's flush renews only ids whose LeaseHandle is registered in
    // the LeaseRegistry — an enqueued id with no handle is silently skipped (Debug-level log). The
    // sink must therefore create + register a handle for each leased sink work row before
    // dispatching, as OutboxPublishWorker does for outbox work; otherwise a multi-batch collective
    // apply enqueues renewals that never reach RenewLeasesAsync and the DB lease still expires.
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-lease-real") };
    var dispatcher = new GatedBatchReportingDispatcher();
    var sinkWork = _sinkWork(streamId);

    var leaseRegistry = new LeaseRegistry();
    var renewCoordinator = new RenewCapturingCoordinator();
    var renewalServices = new ServiceCollection();
    renewalServices.TryAddWhizbangDefaults();
    renewalServices.AddSingleton<IWorkCoordinator>(renewCoordinator);
    await using var renewalProvider = renewalServices.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var renewalWorker = new LeaseRenewalWorker(
      scopeFactory: renewalProvider.GetRequiredService<IServiceScopeFactory>(),
      schemaReadyGate: gate,
      options: Options.Create(new LeaseRenewalWorkerOptions {
        Flusher = new BatchFlusherOptions { MaxBatchSize = 10, CoalesceWindowMs = 10, ImmediateFlushThreshold = 1 }
      }),
      logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaseRenewalWorker>.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      leaseRegistry: leaseRegistry);
    await renewalWorker.StartAsync(CancellationToken.None);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      leaseRenewalChannel: renewalWorker,
      leaseRegistry: leaseRegistry);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    try {
      var (Category, Ids) = await renewCoordinator.FirstRenewal.WaitAsync(TimeSpan.FromSeconds(10));
      await Assert.That(Category).IsEqualTo(WorkCategory.PerspectiveEvent);
      await Assert.That(Ids).Contains(sinkWork.WorkId)
        .Because("an enqueued sink renewal must survive the registry filter and reach " +
                 "IWorkCoordinator.RenewLeasesAsync — without a registered LeaseHandle the flush " +
                 "silently drops it and the DB lease still expires mid-apply.");
    } finally {
      dispatcher.ReleaseDispatch.TrySetResult();
      await cts.CancelAsync();
      await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }
      await renewalWorker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task CollectiveSink_ManyBatchApply_RenewalsAreNotCappedAsync() {
    // Renewal enqueues from the sink are PROGRESS-GATED: one fires only when an apply batch has
    // committed, so a hung apply stops enqueuing and its lease expires naturally. The
    // MaxRenewalsPerWork cap exists to surface renewals that fire regardless of progress (timers) —
    // applied to the sink it makes every apply longer than the cap lose renewal protection at
    // batch N+1 and spam a warning per remaining batch. Eight acked batches must therefore produce
    // eight renewals through the REAL worker + registry — the default cap of six fails batch 7.
    const int BATCHES = 8;
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-lease-uncapped") };
    var sinkWork = _sinkWork(streamId);

    var leaseRegistry = new LeaseRegistry();
    var renewCoordinator = new RenewCapturingCoordinator();
    var dispatcher = new AckedBatchReportingDispatcher(
      BATCHES, batchNumber => renewCoordinator.WaitForRenewalCountAsync(sinkWork.WorkId, batchNumber));
    var renewalServices = new ServiceCollection();
    renewalServices.TryAddWhizbangDefaults();
    renewalServices.AddSingleton<IWorkCoordinator>(renewCoordinator);
    await using var renewalProvider = renewalServices.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var renewalWorker = new LeaseRenewalWorker(
      scopeFactory: renewalProvider.GetRequiredService<IServiceScopeFactory>(),
      schemaReadyGate: gate,
      options: Options.Create(new LeaseRenewalWorkerOptions {
        Flusher = new BatchFlusherOptions { MaxBatchSize = 10, CoalesceWindowMs = 10, ImmediateFlushThreshold = 1 }
      }),
      logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaseRenewalWorker>.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      leaseRegistry: leaseRegistry);
    await renewalWorker.StartAsync(CancellationToken.None);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      leaseRenewalChannel: renewalWorker,
      leaseRegistry: leaseRegistry);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    try {
      // Each batch is acked only after ITS renewal reached RenewLeasesAsync, so reaching the full
      // count proves no renewal was dropped by the cap. Under the capped default, batch 7's
      // renewal never arrives and this wait times out — the RED signal.
      await renewCoordinator.WaitForRenewalCountAsync(sinkWork.WorkId, BATCHES).WaitAsync(TimeSpan.FromSeconds(15));
      await Assert.That(renewCoordinator.RenewalCount(sinkWork.WorkId)).IsGreaterThanOrEqualTo(BATCHES)
        .Because("a progress-gated sink renewal fires per committed batch and must never be " +
                 "dropped by MaxRenewalsPerWork — capping it ends renewal protection mid-apply " +
                 "for any apply longer than the cap.");
    } finally {
      await cts.CancelAsync();
      await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }
      await renewalWorker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task CollectiveSink_DrainReofferDuringCompletionFlush_SkipsRedispatchAsync() {
    // Production echo: the drain loop re-offers a sink stream while the first dispatch's
    // completion flush is still in flight — the rows are already in the processed-event cache
    // (they sit there until the DB acks their DELETE) but the refetch still sees them and the
    // cursor read doesn't reflect the advance yet, so the sink re-reads the same collective
    // envelope and re-runs the entire batched UPDATE. Observed live as same-second full-count
    // or 0-row re-applies. The sink must consult the processed-event cache exactly like the
    // per-event path's duplicate filter and skip the re-dispatch (notifying the dedup observer).
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var workId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-echo") };
    var dispatcher = new RecordingDispatcher();
    var observer = new SinkDedupObserver();
    var envelope = _envelope(eventId, collectiveEvent);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new EventStore { Envelopes = { [streamId] = [envelope] }, Deserialized = [envelope] },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [streamId],
      streamEvents: [_raw(streamId, eventId, eventWorkId: workId)],
      processedEventCacheObserver: observer);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    try {
      await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
      // The sink completes its rows synchronously at the end of the dispatch pass (they enter the
      // processed-event cache there); the completion channel write is the observable tail of that.
      await harness.CompletionCapture.FirstEventWorkId.WaitAsync(TimeSpan.FromSeconds(10));

      // Re-offer the same drain stream — the production refetch during the flush window.
      coordinator.OfferDrainAgain();

      // GREEN: the re-offer is recognized as already-completed and skipped via the dedup
      // observer. RED: no dedup ever fires (the sink re-dispatches instead) and this times out.
      await observer.FirstSinkDedup.WaitAsync(TimeSpan.FromSeconds(10));
    } finally {
      await cts.CancelAsync();
      await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }
    }

    await Assert.That(dispatcher.Calls.Count).IsEqualTo(1)
      .Because("a drain re-offer of an already-completed sink row (completion flush in flight) " +
               "must not re-run the collective apply — the processed-event cache knows the row " +
               "is done, exactly as the per-event duplicate filter does.");
    await Assert.That(observer.Deduped.Any(d => d.PerspectiveName == CollectiveRouting.SINK_PERSPECTIVE_NAME
        && d.Ids.Contains(workId))).IsTrue();
  }

  /// <summary>
  /// Gap #6 unit lock-in: the production route. <c>claim_work</c> returns a collective event's sink
  /// stream as a <see cref="WorkBatch.PerspectiveStreamIds"/> entry (DRAIN path), not a per-event
  /// <see cref="WorkBatch.PerspectiveWork"/> with the sink name pre-set. The drain expansion must
  /// surface the <see cref="CollectiveRouting.SINK_PERSPECTIVE_NAME"/> (it has no registered
  /// <c>IPerspectiveFor</c>) and dispatch the event exactly once — the other sink tests here inject
  /// the sink name directly and so only cover the channel guard.
  /// </summary>
  [Test]
  public async Task CollectiveSink_ViaDrainPath_DispatchesEventOnceAndSkipsRunner_Async() {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new RecordingDispatcher();
    var runner = new TrackingRunner();
    var envelope = _envelope(eventId, collectiveEvent);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [], // nothing on the channel/standard path
      eventStore: new EventStore {
        Envelopes = { [streamId] = [envelope] },
        Deserialized = [envelope] // drain fetch → deserialize yields the collective envelope
      },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [streamId],
      streamEvents: [_raw(streamId, eventId)]);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    // Deterministic wait on the actual dispatch — the drain channel processes the claimed stream
    // asynchronously, so cycle counting would race it.
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Calls.Count).IsEqualTo(1)
      .Because("A collective event claimed via the drain path must be dispatched exactly once.");
    await Assert.That(dispatcher.Calls[0].EventId).IsEqualTo(eventId);
    await Assert.That(runner.RunWithEventsCount).IsEqualTo(0)
      .Because("The sink bypasses the per-stream runner.");
  }

  /// <summary>
  /// REGRESSION (production death spiral): after a collective event is dispatched successfully, the sink
  /// MUST complete its own <c>__collective__</c> <c>wh_perspective_events</c> work row by
  /// <c>event_work_id</c> (the <c>complete_perspective_events</c> DELETE path — same as every standard
  /// perspective). Without this the row keeps <c>processed_at = NULL</c>, <c>claim_orphaned_perspective_events</c>
  /// re-leases it every tick, and each re-lease re-dispatches the whole-cohort collective UPDATE — the
  /// self-sustaining re-dispatch loop that bloated a perspective table to the vast majority dead tuples. The cursor
  /// advance alone does NOT delete the row (<c>complete_perspective_cursor_work</c> only advances the cursor
  /// and marks <c>processed_at</c> by <c>event_id</c>; that path is insufficient — the row lingers).
  /// RED before the fix: the sink never enqueues an event-work-id, so <see cref="CapturingPerspectiveCompletionChannel.FirstEventWorkId"/>
  /// never completes and this times out.
  /// </summary>
  [Test]
  public async Task CollectiveSink_SuccessfulDispatch_CompletesSinkWorkRowByEventWorkId_Async() {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var sinkWork = _sinkWork(streamId);
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new RecordingDispatcher();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    // The fix enqueues the sink's event_work_id for deletion; the worker's idle-tick flush drains it to
    // the completion channel. Wait on that deterministically instead of racing the idle loop.
    await harness.CompletionCapture.FirstEventWorkId.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(dispatcher.Calls.Count).IsEqualTo(1)
      .Because("The collective event is dispatched exactly once.");
    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(sinkWork.WorkId)
      .Because("A successful sink dispatch must delete its own __collective__ work row by event_work_id, " +
        "so claim_orphaned can't re-lease it into the re-dispatch loop that caused the production bloat.");
  }

  /// <summary>
  /// #959: a collective apply is awaitable. Once the sink's apply commits, the event is marked applied under
  /// the sink's name, which settles a wait for that event on any perspective.
  /// </summary>
  [Test]
  public async Task CollectiveSink_SuccessfulDispatch_MarksTheEventAppliedForAnyPerspective_Async() {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new RecordingDispatcher();
    var tracker = new SyncEventTracker();
    var awaiter = new PerspectiveSyncAwaiter(
      new FakeCoordinator([]),
      new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled }),
      NullLogger<PerspectiveSyncAwaiter>.Instance,
      tracker,
      new ScopedEventTracker(),
      new AsyncLocalLifecycleContextAccessor());
    // The wait starts before the apply: the event was never tracked in this process.
    var waiting = awaiter.WaitForAppliedAsync(typeof(CollectiveTargetPerspective), eventId, TimeSpan.FromSeconds(30));

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      syncEventTracker: tracker);
    await Assert.That(waiting.IsCompleted).IsFalse();

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    var result = await waiting.WaitAsync(TimeSpan.FromSeconds(30));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(dispatcher.Calls.Count).IsEqualTo(1)
      .Because("the wait completed on the sink's committed apply, not before it");
  }

  /// <summary>A perspective whose model a collective event targets; only its name matters here.</summary>
  private sealed class CollectiveTargetPerspective;

  /// <summary>
  /// Same regression as <see cref="CollectiveSink_SuccessfulDispatch_CompletesSinkWorkRowByEventWorkId_Async"/>,
  /// but via the production DRAIN route (a collective event claimed as a stream, not injected as channel work).
  /// The drain twin of the sink guard must resolve the sink row's <c>event_work_id</c> from the drain batch's
  /// raw rows and complete it the same way.
  /// </summary>
  [Test]
  public async Task CollectiveSink_ViaDrainPath_CompletesSinkWorkRowByEventWorkId_Async() {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var workId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new RecordingDispatcher();
    var envelope = _envelope(eventId, collectiveEvent);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new EventStore { Envelopes = { [streamId] = [envelope] }, Deserialized = [envelope] },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [streamId],
      streamEvents: [_raw(streamId, eventId, eventWorkId: workId)]);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.CompletionCapture.FirstEventWorkId.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(dispatcher.Calls.Count).IsEqualTo(1)
      .Because("A collective event claimed via the drain path is dispatched exactly once.");
    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(workId)
      .Because("The drain-path sink must delete its own __collective__ work row by event_work_id (resolved " +
        "from the drain batch's raw rows) so claim_orphaned can't re-lease it.");
  }

  /// <summary>
  /// REGRESSION (the "Template Activating" toast never resolves): a collective apply completing IS the
  /// "all perspectives complete" moment for that collective event. The sink must run the event through the
  /// PostAllPerspectives lifecycle so a PostAllPerspectives receptor (e.g. a consumer's completion-notification
  /// emitter that publishes the tag-bearing Completed bookend) fires. Before the fix the sink dispatches the
  /// apply and reports completion but never invokes the lifecycle, so nothing downstream learns the apply
  /// finished — this asserts the lifecycle fires on success.
  /// </summary>
  [Test]
  public async Task CollectiveSink_SuccessfulDispatch_FiresPostAllPerspectivesLifecycle_Async() {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new RecordingDispatcher();
    var invoker = new CapturingReceptorInvoker();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      receptorInvoker: invoker);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    await invoker.FirstPostAllPerspectives.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(dispatcher.Calls.Count).IsEqualTo(1);
    await Assert.That(invoker.Invocations.Any(i => i.EventId == eventId && i.Stage == LifecycleStage.PostAllPerspectivesInline)).IsTrue()
      .Because("A successful collective apply must run its event through PostAllPerspectives so the completion " +
        "receptor/tag fires — otherwise the frontend's completion toast waits forever.");
  }

  /// <summary>
  /// The properties the apply's specs assigned reach every post-apply stage, so a tag hook there can say what changed
  /// (#1045).
  /// </summary>
  [Test]
  public async Task CollectiveSink_SuccessfulDispatch_CarriesWhatTheSpecsChangedToThePostApplyStagesAsync() {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new RecordingDispatcher {
      ChangedProperties = new Dictionary<Type, IReadOnlyList<string>> { [typeof(string)] = ["Length"] },
    };
    var invoker = new CapturingReceptorInvoker();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      receptorInvoker: invoker);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await invoker.FirstPostAllPerspectives.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* teardown */ }

    List<(LifecycleStage Stage, MessageChanges? Changes)> seen;
    lock (invoker.Invocations) {
      seen = [.. invoker.Changes];
    }
    await Assert.That(seen).IsNotEmpty();
    foreach (var (stage, changes) in seen) {
      await Assert.That(changes?.Kind).IsEqualTo(MessageChangeKind.Collective).Because($"stage {stage}");
      await Assert.That(changes!.ByModel[typeof(string)]).IsEquivalentTo(["Length"]);
    }
  }

  /// <summary>
  /// The mirror guard: a FAILED collective apply must NOT fire the PostAllPerspectives lifecycle — the apply
  /// did not complete, so emitting a "completed" signal would be a lie (and would prematurely dismiss the toast).
  /// </summary>
  [Test]
  public async Task CollectiveSink_DispatchThrows_DoesNotFirePostAllPerspectives_Async() {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new ThrowingDispatcher();
    var invoker = new CapturingReceptorInvoker();
    var envelope = _envelope(eventId, collectiveEvent);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new EventStore { Envelopes = { [streamId] = [envelope] }, Deserialized = [envelope] },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [streamId],
      streamEvents: [_raw(streamId, eventId)],
      receptorInvoker: invoker);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(invoker.Invocations.Any(i => i.Stage is LifecycleStage.PostAllPerspectivesInline or LifecycleStage.PostAllPerspectivesDetached)).IsFalse()
      .Because("A failed apply must not signal completion — no PostAllPerspectives lifecycle for it.");
  }

  /// <summary>
  /// A throwing post-apply receptor must be isolated: the collective apply already committed and its work
  /// rows were completed, so a failing PostAllPerspectives receptor must neither crash the worker nor undo
  /// the completion. This exercises the sink's per-event catch around the lifecycle invocation.
  /// </summary>
  [Test]
  public async Task CollectiveSink_PostApplyReceptorThrows_DoesNotCrashWorker_Async() {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var sinkWork = _sinkWork(streamId);
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new RecordingDispatcher();
    var invoker = new ThrowingReceptorInvoker();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      receptorInvoker: invoker);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    await invoker.FirstInvoke.WaitAsync(TimeSpan.FromSeconds(10));
    // The apply succeeded and its work row must still be completed despite the throwing post-apply receptor.
    await harness.CompletionCapture.FirstEventWorkId.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("A throwing post-apply receptor must not escape the sink and fault ExecuteAsync — a faulted " +
        "body trips BackgroundServiceExceptionBehavior.StopHost and crash-loops the service.");
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(dispatcher.Calls.Count).IsEqualTo(1);
    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(sinkWork.WorkId)
      .Because("A throwing post-apply receptor must not undo the completed apply's work-row deletion.");
  }

  [Test]
  public async Task CollectiveSink_NoDispatcherRegistered_SkipsTheRunnerAndReturns_Async() {
    var streamId = TrackedGuid.New().Value;
    var runner = new TrackingRunner();
    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(TrackedGuid.New().Value, new TestCollectiveEvent { Scope = new TenantCollectiveScope("t") })] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: null); // not configured

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.WaitForCyclesAsync(2, TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    // "Not configured" has to mean the sink branch logs and returns, not that the work falls
    // through to the ordinary per-stream path — a collective event applied through the per-stream
    // runner is applied against the wrong rows entirely.
    await Assert.That(runner.RunWithEventsCount).IsEqualTo(0);
  }

  [Test]
  public async Task CollectiveSink_NoCollectiveEventOnStream_NoDispatch_Async() {
    var streamId = TrackedGuid.New().Value;
    var sinkWork = _sinkWork(streamId);
    var dispatcher = new RecordingDispatcher();
    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new EventStore(), // empty — no events on the stream
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    // A leased sink row with no collective event on the stream (cursor already advanced past it, or a
    // stale re-lease) must still be completed — otherwise it re-leases forever. Wait on that deletion.
    await harness.CompletionCapture.FirstEventWorkId.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(dispatcher.Calls.Count).IsEqualTo(0)
      .Because("No collective event on the stream → nothing to dispatch.");
    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(sinkWork.WorkId)
      .Because("A leased sink row with no matching collective event must be completed (deleted) so " +
        "claim_orphaned stops re-leasing it into a no-op re-dispatch loop.");
  }

  [Test]
  public async Task CollectiveSink_DispatchThrows_DoesNotCrashWorker_Async() {
    // A failing collective apply (e.g. an EF "does not represent a valid property to be set" on a polymorphic
    // model) must NOT propagate out of the sink: if it does, the perspective batch re-throws and trips
    // BackgroundServiceExceptionBehavior=StopHost, crash-looping the whole service on one poison event.
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new ThrowingDispatcher();
    var envelope = _envelope(eventId, collectiveEvent);

    // DRAIN path (the production route: a collective event arriving via transport is claimed as a stream).
    // Its caller catches only OperationCanceledException, so an un-guarded apply failure propagates and faults
    // the worker host — exactly the dev crash-loop.
    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new EventStore { Envelopes = { [streamId] = [envelope] }, Deserialized = [envelope] },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [streamId],
      streamEvents: [_raw(streamId, eventId)]);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    // The fix catches the apply failure and REPORTS it as a perspective failure (instead of letting it
    // propagate and fault the host). The un-guarded code throws raw and never reports — so this wait times
    // out, which is the RED signal.
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("An un-guarded apply failure would fault ExecuteAsync and trip " +
        "BackgroundServiceExceptionBehavior.StopHost — the crash-loop this test guards against.");
    // Fully stop the background worker so it doesn't linger and starve sibling [NotInParallel] tests.
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(dispatcher.Calls).IsGreaterThan(0)
      .Because("The worker attempted to dispatch the collective event.");
    await Assert.That(coordinator.ReportedFailures.Count).IsGreaterThan(0)
      .Because("A failing collective apply must be caught and reported as a perspective failure — not propagated (which faults the host and crash-loops the service).");
    await Assert.That(coordinator.ReportedFailures[0].PerspectiveName).IsEqualTo(CollectiveRouting.SINK_PERSPECTIVE_NAME);
  }

  [Test]
  public async Task CollectiveSink_AttemptsExceedMax_DeadLetteredNotDispatched_Async() {
    // A genuinely poison collective event (one that fails apply every time — NOT a transient deadlock, which
    // the driver retries in-line) must eventually stop: once its wh_perspective_events attempts exceed
    // MaxPerspectiveEventAttempts it is moved to the DLQ instead of re-dispatched forever. The __collective__
    // sink row is a normal perspective-event row, so the drain path's pre-apply dead-letter filter
    // (FilterDeadLetteredAsync) already covers it — this test locks that in: an over-max sink row is
    // dead-lettered (sourceTable=wh_perspective_events) and the dispatcher is never invoked.
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var workId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new ThrowingDispatcher();
    var deadLetters = new RecordingDeadLetterStore();
    var envelope = _envelope(eventId, collectiveEvent);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new EventStore { Envelopes = { [streamId] = [envelope] }, Deserialized = [envelope] },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [streamId],
      // Poison means every apply failed: the failure counter, not the lease count, crosses the max (#700).
      streamEvents: [_raw(streamId, eventId, attempts: 5, eventWorkId: workId, failures: 5)],
      maxPerspectiveEventAttempts: 2,
      deadLetterStore: deadLetters);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await deadLetters.FirstMove.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(deadLetters.Moves.Count).IsGreaterThanOrEqualTo(1)
      .Because("An over-max sink row must be moved to the DLQ so it stops retrying.");
    await Assert.That(deadLetters.Moves[0].SourceTable).IsEqualTo(DeadLetterSourceTable.PERSPECTIVE_EVENTS);
    await Assert.That(deadLetters.Moves[0].SourceId).IsEqualTo(workId)
      .Because("The dead-lettered row is the sink's own wh_perspective_events work row.");
    await Assert.That(deadLetters.Moves[0].Reason).IsEqualTo(MessageFailureReason.MaxAttemptsExceeded);
    await Assert.That(dispatcher.Calls).IsEqualTo(0)
      .Because("The poison event is dead-lettered pre-apply — it must never reach the dispatcher again.");
  }

  // ── helpers ────────────────────────────────────────────────────────────

  [Test]
  public async Task CollectiveSink_Meters_CountAReceivedAndAppliedCollective_Async() {
    // A collective disappears into the sink once applied; without a meter nothing shows how many a consumer
    // received or applied (#738).
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new RecordingDispatcher();
    using var factory = new Whizbang.Core.Tests.Observability.TestMeterFactory();
    var metrics = new CompositeMetrics(new WhizbangMetrics(factory));

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      compositeMetrics: metrics);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    var meter = factory.CreatedMeters.Single(m => m.Name == CompositeMetrics.METER_NAME);
    await Assert.That(Whizbang.Core.Tests.Observability.ProbeMeterReader.ReadTotal(meter, "whizbang.collectives.received")).IsEqualTo(1)
      .Because("one collective event reached the sink");
    await Assert.That(Whizbang.Core.Tests.Observability.ProbeMeterReader.ReadTotal(meter, "whizbang.collectives.applied")).IsEqualTo(1)
      .Because("the dispatcher applied it once");
    await Assert.That(Whizbang.Core.Tests.Observability.ProbeMeterReader.ReadTotal(meter, "whizbang.collectives.skipped")).IsEqualTo(0);
  }

  [Test]
  public async Task CollectiveSink_Meters_CountALeasedSinkRowWithNoEventAsSkipped_Async() {
    var streamId = TrackedGuid.New().Value;
    var sinkWork = _sinkWork(streamId);
    var dispatcher = new RecordingDispatcher();
    using var factory = new Whizbang.Core.Tests.Observability.TestMeterFactory();
    var metrics = new CompositeMetrics(new WhizbangMetrics(factory));
    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new EventStore(),
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      compositeMetrics: metrics);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await harness.CompletionCapture.FirstEventWorkId.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    var meter = factory.CreatedMeters.Single(m => m.Name == CompositeMetrics.METER_NAME);
    await Assert.That(Whizbang.Core.Tests.Observability.ProbeMeterReader.ReadTotal(meter, "whizbang.collectives.skipped")).IsEqualTo(1)
      .Because("a leased sink row with no collective event behind it is completed without an apply; that is a skip, and a rising skip count is the re-lease loop showing itself");
    await Assert.That(Whizbang.Core.Tests.Observability.ProbeMeterReader.ReadTotal(meter, "whizbang.collectives.applied")).IsEqualTo(0);
  }

  // ── Ordered sink (#963): the stream's collective queue, in commit order ─────────────────────

  /// <summary>
  /// Two collectives sharing an ordering key share one sink stream. The later-committed one was minted first, so its
  /// id is the smaller: applied in id order (as the sink once read its stream) the earlier commit lands last and wins.
  /// The queue is in commit order, and the sink applies it in that order, so the later commit's result stands.
  /// </summary>
  [Test]
  public async Task CollectiveSink_OrderedQueue_AppliesInCommitOrder_WhenIdsRunBackwardAsync() {
    var streamId = TrackedGuid.New().Value;
    var laterCommitEarlierId = Guid.Parse("00000000-0000-7000-8000-000000000001");
    var earlierCommitLaterId = Guid.Parse("00000000-0000-7000-8000-000000000002");
    var first = _sinkWork(streamId);
    var second = _sinkWork(streamId);
    var earlier = new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "a" };
    var later = new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "b" };
    var dispatcher = new FlipDispatcher(expected: 2);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [first, second],
      eventStore: new EventStore {
        Deserialized = [_envelope(laterCommitEarlierId, later), _envelope(earlierCommitLaterId, earlier)],
      },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher);
    coordinator.SinkQueue = [
      new CollectiveSinkQueueEntry(first.WorkId, earlierCommitLaterId, CommitSequence: 10),
      new CollectiveSinkQueueEntry(second.WorkId, laterCommitEarlierId, CommitSequence: 11),
    ];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.AllDispatched.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.CompletionCapture.EventWorkIdsCaptured(first.WorkId, second.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Applied).IsEquivalentTo(["a", "b"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("The sink applies its stream's collectives in commit order, not id order.");
    await Assert.That(dispatcher.State).IsEqualTo("b")
      .Because("Two collectives sharing a key end at the later commit's result.");
    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(first.WorkId);
    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(second.WorkId);
    await Assert.That(coordinator.EventsFetchedById).IsEquivalentTo([earlierCommitLaterId, laterCommitEarlierId])
      .Because("The sink loads exactly the queued events, whatever the cursor says, so none behind it is skipped.");
  }

  /// <summary>
  /// A collective this worker applied stays in the queue until its completion flushes. The next collective on the
  /// stream, claimed in a later batch before that flush, must not wait behind it: it is behind us, not ahead.
  /// </summary>
  [Test]
  public async Task CollectiveSink_OrderedQueue_AnAppliedRowAwaitingItsFlush_DoesNotBlockTheNextAsync() {
    var streamId = TrackedGuid.New().Value;
    var first = _sinkWork(streamId);
    var second = _sinkWork(streamId);
    var firstId = Guid.CreateVersion7();
    var secondId = Guid.CreateVersion7();
    var dispatcher = new FlipDispatcher(expected: 2);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [first],
      eventStore: new EventStore {
        Deserialized = [
          _envelope(firstId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "a" }),
          _envelope(secondId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "b" }),
        ],
      },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher);
    // The queue still holds the first row after it applied, as the database does until the completion flushes.
    coordinator.SinkQueue = [
      new CollectiveSinkQueueEntry(first.WorkId, firstId, CommitSequence: 1),
      new CollectiveSinkQueueEntry(second.WorkId, secondId, CommitSequence: 2),
    ];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstApplied.WaitAsync(TimeSpan.FromSeconds(10));
    coordinator.OfferWork([second]);
    await dispatcher.AllDispatched.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Applied).IsEquivalentTo(["a", "b"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("Each applies once and in order; the first is not applied again and does not hold up the second.");
  }

  /// <summary>
  /// A collective ahead in the queue that this run does not hold (leased elsewhere, or waiting out a retry) blocks
  /// every collective behind it: applying the later one first is exactly the reordering the key exists to prevent.
  /// </summary>
  [Test]
  public async Task CollectiveSink_OrderedQueue_WaitsBehindACollectiveItDoesNotHoldAsync() {
    var streamId = TrackedGuid.New().Value;
    var held = _sinkWork(streamId);
    var ahead = Guid.CreateVersion7();
    var dispatcher = new FlipDispatcher(expected: 1);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [held],
      eventStore: new EventStore(),
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher);
    coordinator.SinkQueue = [
      new CollectiveSinkQueueEntry(ahead, Guid.CreateVersion7(), CommitSequence: 10),
      new CollectiveSinkQueueEntry(held.WorkId, Guid.CreateVersion7(), CommitSequence: 11),
    ];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.SinkQueueRead.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Applied.Count).IsEqualTo(0)
      .Because("The held collective is behind one this run does not hold, so it must wait for it.");
    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(held.WorkId)
      .Because("A collective that did not apply keeps its row, so it is offered again once the one ahead has applied.");
    await Assert.That(coordinator.ReportedFailures.Count).IsEqualTo(0)
      .Because("Waiting its turn is not a failure.");
  }

  /// <summary>
  /// Two collectives sharing a key, claimed together, can reach the sink in two runs; the stream's gate serializes them
  /// but does not order them. When the later one's run gets the gate first it has to wait, and it must then apply right
  /// behind the head in the head's run, not sit until its lease expires and it is offered again. Here the later row is
  /// offered alone first and the head only after the later row's run has read the queue, which is the losing order.
  /// </summary>
  [Test]
  public async Task CollectiveSink_OrderedQueue_ALaterRowWhoseRunWentFirst_AppliesBehindTheHeadAsync() {
    var streamId = TrackedGuid.New().Value;
    var first = _sinkWork(streamId);
    var second = _sinkWork(streamId);
    var firstId = Guid.CreateVersion7();
    var secondId = Guid.CreateVersion7();
    var dispatcher = new FlipDispatcher(expected: 2);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [second],
      eventStore: new EventStore {
        Deserialized = [
          _envelope(firstId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "a" }),
          _envelope(secondId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "b" }),
        ],
      },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher);
    coordinator.SinkQueue = [
      new CollectiveSinkQueueEntry(first.WorkId, firstId, CommitSequence: 1),
      new CollectiveSinkQueueEntry(second.WorkId, secondId, CommitSequence: 2),
    ];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    // The later row's run reads the queue inside the stream's gate, so the head's run cannot start before it ends.
    await coordinator.SinkQueueRead.WaitAsync(TimeSpan.FromSeconds(10));
    coordinator.OfferWork([first]);
    await harness.CompletionCapture.EventWorkIdsCaptured(first.WorkId, second.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Applied).IsEquivalentTo(["a", "b"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("The head's run takes over the row that waited for it and applies both, in commit order.");
    await Assert.That(coordinator.ReportedFailures.Count).IsEqualTo(0);
    await Assert.That(worker.HeldBackSinkStreamCount).IsEqualTo(0)
      .Because("Nothing is left waiting once both have applied.");
  }

  /// <summary>
  /// A row held back is taken over only while its lease is still this pod's. Past that it may have been claimed
  /// elsewhere, so the next run leaves it to be offered again, and it is forgotten; a stream whose held-back rows have
  /// all expired is dropped when another stream holds a row back, so the map holds only streams waiting right now.
  /// </summary>
  [Test]
  public async Task CollectiveSink_OrderedQueue_AHeldBackRowPastItsLease_IsNotTakenOver_AndIsForgottenAsync() {
    var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
    var pastTheLease = TimeSpan.FromSeconds(new LeaseRenewalWorkerOptions().LeaseSeconds + 1);
    var one = TrackedGuid.New().Value;
    var two = TrackedGuid.New().Value;
    var oneHead = _sinkWork(one);
    var oneHeld = _sinkWork(one);
    var twoHead = _sinkWork(two);
    var twoAhead = _sinkWork(two);
    var twoHeld = _sinkWork(two);
    var ids = Enumerable.Range(0, 6).Select(_ => Guid.CreateVersion7()).ToArray();
    var dispatcher = new FlipDispatcher(expected: 3);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [oneHeld],
      eventStore: new EventStore {
        Deserialized = [.. ids.Select((id, i) => _envelope(id, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = $"e{i}" }))],
      },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher,
      timeProvider: clock);
    // Each stream: a head this pod holds, a collective held elsewhere, then one this pod holds behind it.
    coordinator.SinkQueues[one] = [
      new CollectiveSinkQueueEntry(oneHead.WorkId, ids[0], CommitSequence: 1),
      new CollectiveSinkQueueEntry(Guid.CreateVersion7(), ids[1], CommitSequence: 2),
      new CollectiveSinkQueueEntry(oneHeld.WorkId, ids[2], CommitSequence: 3),
    ];
    coordinator.SinkQueues[two] = [
      new CollectiveSinkQueueEntry(twoHead.WorkId, ids[3], CommitSequence: 1),
      new CollectiveSinkQueueEntry(twoAhead.WorkId, ids[4], CommitSequence: 2),
      new CollectiveSinkQueueEntry(twoHeld.WorkId, ids[5], CommitSequence: 3),
    ];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    // Each held row is offered alone and waits; its head follows once that run has read the queue, inside the
    // stream's gate. The head's completion is enqueued after its run has settled what stays held back.
    await coordinator.SinkQueueReads(1).WaitAsync(TimeSpan.FromSeconds(10));
    coordinator.OfferWork([oneHead]);
    await harness.CompletionCapture.EventWorkIdsCaptured(oneHead.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(worker.HeldBackSinkStreamCount).IsEqualTo(1);

    clock.Advance(pastTheLease);
    coordinator.OfferWork([twoHeld]);
    await coordinator.SinkQueueReads(3).WaitAsync(TimeSpan.FromSeconds(10));
    coordinator.OfferWork([twoHead]);
    await harness.CompletionCapture.EventWorkIdsCaptured(twoHead.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(worker.HeldBackSinkStreamCount).IsEqualTo(1)
      .Because("The first stream's row is past its lease, so holding the second stream's row back drops it.");

    clock.Advance(pastTheLease);
    coordinator.OfferWork([twoAhead]);
    await harness.CompletionCapture.EventWorkIdsCaptured(twoAhead.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Applied).IsEquivalentTo(["e0", "e3", "e4"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("A held-back row past its lease may be another instance's now, so it is not applied here.");
    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(twoHeld.WorkId);
    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(oneHeld.WorkId);
    await Assert.That(worker.HeldBackSinkStreamCount).IsEqualTo(0)
      .Because("The expired row is forgotten, and with it the stream.");
  }

  /// <summary>A leased row the queue no longer holds was already applied; it is completed without a dispatch.</summary>
  [Test]
  public async Task CollectiveSink_OrderedQueue_WithoutTheLeasedRows_CompletesThemWithoutDispatchAsync() {
    var streamId = TrackedGuid.New().Value;
    var stale = _sinkWork(streamId);
    var dispatcher = new FlipDispatcher(expected: 1);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [stale],
      eventStore: new EventStore(),
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher);
    coordinator.SinkQueue = [];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await harness.CompletionCapture.FirstEventWorkId.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Applied.Count).IsEqualTo(0);
    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(stale.WorkId);
  }

  /// <summary>
  /// A queued collective whose event the store no longer returns (a reaped body) is completed with the rest rather
  /// than blocking the queue forever.
  /// </summary>
  [Test]
  public async Task CollectiveSink_OrderedQueue_ARowWithoutItsEvent_IsCompletedWithTheRestAsync() {
    var streamId = TrackedGuid.New().Value;
    var gone = _sinkWork(streamId);
    var present = _sinkWork(streamId);
    var presentId = Guid.CreateVersion7();
    var dispatcher = new FlipDispatcher(expected: 1);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [gone, present],
      eventStore: new EventStore {
        Deserialized = [_envelope(presentId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "p" })],
      },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher);
    coordinator.SinkQueue = [
      new CollectiveSinkQueueEntry(gone.WorkId, Guid.CreateVersion7(), CommitSequence: 1),
      new CollectiveSinkQueueEntry(present.WorkId, presentId, CommitSequence: 2),
    ];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.AllDispatched.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.CompletionCapture.FirstEventWorkId.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Applied).IsEquivalentTo(["p"]);
    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(gone.WorkId);
    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(present.WorkId);
  }

  /// <summary>
  /// A failed collective stops the queue where it failed: nothing behind it applies ahead of it, and nothing is
  /// completed, so the failure is retried in its place.
  /// </summary>
  [Test]
  public async Task CollectiveSink_OrderedQueue_AFailureStopsTheQueueInPlaceAsync() {
    var streamId = TrackedGuid.New().Value;
    var failing = _sinkWork(streamId);
    var behind = _sinkWork(streamId);
    var failingId = Guid.CreateVersion7();
    var behindId = Guid.CreateVersion7();
    var dispatcher = new FlipDispatcher(expected: 1) { FailOn = "x" };

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [failing, behind],
      eventStore: new EventStore {
        Deserialized = [
          _envelope(failingId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "x" }),
          _envelope(behindId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "y" }),
        ],
      },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher);
    coordinator.SinkQueue = [
      new CollectiveSinkQueueEntry(failing.WorkId, failingId, CommitSequence: 1),
      new CollectiveSinkQueueEntry(behind.WorkId, behindId, CommitSequence: 2),
    ];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Applied.Count).IsEqualTo(0)
      .Because("The collective behind a failed one must not apply ahead of it.");
    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(failing.WorkId);
    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(behind.WorkId);
  }

  // ── Busy apply lock (#964): not a failure ──────────────────────────────────────────────────

  /// <summary>
  /// A collective that did not get its apply lock has not failed: another batch holds the lock for the same table and
  /// scope. It is not reported as a failure, so the failure count that drives dead-lettering does not move, and its row
  /// is not completed, so it is applied later in the same place.
  /// </summary>
  [Test]
  public async Task CollectiveSink_BusyApplyLock_IsNotReportedAsAFailure_AndKeepsItsRowAsync() {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var sinkWork = _sinkWork(streamId);
    var dispatcher = new BusyLockDispatcher();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") })] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* teardown */ }

    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse();
    await Assert.That(coordinator.ReportedFailures.Count).IsEqualTo(0)
      .Because("A busy lock is not a failed apply; reporting it would count toward dead-lettering a good event.");
    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(sinkWork.WorkId)
      .Because("Nothing applied, so the row stays and the collective applies later, in its place.");
  }

  /// <summary>The reversible default: an option restores the accounting from before, a busy lock as a failure.</summary>
  [Test]
  public async Task CollectiveSink_BusyApplyLock_CountsAsAFailure_WhenConfiguredToAsync() {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var dispatcher = new BusyLockDispatcher();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") })] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      lockBusyCountsAsFailure: true);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(coordinator.ReportedFailures.Count).IsEqualTo(1);
  }

  /// <summary>
  /// A collective whose every batch committed is complete at once, even though one behind it in the same run found its
  /// lock busy, so the retry starts at the busy one and the one before it is not applied again (#1003).
  /// </summary>
  [Test]
  public async Task CollectiveSink_BusyApplyLock_CollectivesAppliedBeforeIt_AreCompleteAndNotReappliedAsync() {
    var streamId = TrackedGuid.New().Value;
    var applied = _sinkWork(streamId);
    var busy = _sinkWork(streamId);
    var appliedId = Guid.CreateVersion7();
    var busyId = Guid.CreateVersion7();
    var dispatcher = new FlipDispatcher(expected: 2) { BusyOnceOn = "b" };
    var receptorInvoker = new CapturingReceptorInvoker();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [applied, busy],
      eventStore: new EventStore {
        Deserialized = [
          _envelope(appliedId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "a" }),
          _envelope(busyId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "b" }),
        ],
      },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher,
      receptorInvoker: receptorInvoker);
    coordinator.SinkQueue = [
      new CollectiveSinkQueueEntry(applied.WorkId, appliedId, CommitSequence: 1),
      new CollectiveSinkQueueEntry(busy.WorkId, busyId, CommitSequence: 2),
    ];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.Busy.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.CompletionCapture.EventWorkIdsCaptured(applied.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    await receptorInvoker.FirstPostAllPerspectives.WaitAsync(TimeSpan.FromSeconds(10));
    var completedBeforeTheRetry = harness.CompletionCapture.EventWorkIds.ToList();
    List<Guid> postApplyBeforeTheRetry;
    lock (receptorInvoker.Invocations) {
      postApplyBeforeTheRetry = [.. receptorInvoker.Invocations.Where(i => i.Stage == LifecycleStage.PostAllPerspectivesDetached).Select(i => i.EventId)];
    }
    // The retry: the busy collective's row is leased again; the applied one's row is complete and is not.
    coordinator.OfferWork([busy]);
    await dispatcher.AllDispatched.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.CompletionCapture.EventWorkIdsCaptured(busy.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(completedBeforeTheRetry).DoesNotContain(busy.WorkId)
      .Because("A collective that found its lock busy is not complete; it applies in full on the retry.");
    await Assert.That(postApplyBeforeTheRetry).IsEquivalentTo([appliedId])
      .Because("The applied collective is complete, so its completion receptors fire without waiting for the busy one.");
    await Assert.That(dispatcher.Applied).IsEquivalentTo(["a", "b"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("The retry starts at the busy collective; the one applied before it is not applied again.");
    await Assert.That(coordinator.ReportedFailures.Count).IsEqualTo(0);
  }

  /// <summary>
  /// A collective whose every batch committed is complete at once, even though the one behind it in the same run
  /// failed, so the failure's retry does not apply it again (#1003).
  /// </summary>
  [Test]
  public async Task CollectiveSink_Failure_CollectivesAppliedBeforeIt_AreCompleteAsync() {
    var streamId = TrackedGuid.New().Value;
    var applied = _sinkWork(streamId);
    var failing = _sinkWork(streamId);
    var appliedId = Guid.CreateVersion7();
    var failingId = Guid.CreateVersion7();
    var dispatcher = new FlipDispatcher(expected: 1) { FailOn = "x" };

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [applied, failing],
      eventStore: new EventStore {
        Deserialized = [
          _envelope(appliedId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "a" }),
          _envelope(failingId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "x" }),
        ],
      },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher);
    coordinator.SinkQueue = [
      new CollectiveSinkQueueEntry(applied.WorkId, appliedId, CommitSequence: 1),
      new CollectiveSinkQueueEntry(failing.WorkId, failingId, CommitSequence: 2),
    ];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.CompletionCapture.EventWorkIdsCaptured(applied.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(failing.WorkId);
    await Assert.That(coordinator.ReportedFailures.Single().LastEventId).IsEqualTo(appliedId)
      .Because("The failure is reported at the collective that failed, after the one that applied.");
  }

  /// <summary>
  /// An engine without a sink queue reads the stream after its cursor. A busy lock there moves the cursor past the
  /// collectives applied before it, so the retry reads from the busy one (#1003).
  /// </summary>
  [Test]
  public async Task CollectiveSink_BusyApplyLock_WithoutAQueue_MovesTheCursorPastTheAppliedOnesAsync() {
    var streamId = TrackedGuid.New().Value;
    var appliedId = Guid.CreateVersion7();
    var busyId = Guid.CreateVersion7();
    var sinkWork = _sinkWork(streamId);
    var dispatcher = new FlipDispatcher(expected: 1) { BusyOnceOn = "b" };

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new EventStore {
        Envelopes = {
          [streamId] = [
            _envelope(appliedId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "a" }),
            _envelope(busyId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "b" }),
          ],
        },
      },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.Busy.WaitAsync(TimeSpan.FromSeconds(10));
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    PerspectiveCursorCompletion cursor;
    lock (coordinator.ReportedCompletions) {
      cursor = coordinator.ReportedCompletions.Single(c => c.StreamId == streamId);
    }
    await Assert.That(cursor.LastEventId).IsEqualTo(appliedId);
    await Assert.That(cursor.ProcessedEventIds).IsEquivalentTo([appliedId]);
    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(sinkWork.WorkId)
      .Because("Without a queue the run's rows are not tied to collectives, so none is completed until all apply.");
  }

  [Test]
  public async Task PerspectiveWorkerOptions_BusyApplyLock_IsNotAFailureByDefaultAsync() {
    await Assert.That(new PerspectiveWorkerOptions().CollectiveLockBusyCountsAsFailure).IsFalse();
  }

  private static PerspectiveWork _sinkWork(Guid streamId) => new() {
    WorkId = Guid.CreateVersion7(),
    StreamId = streamId,
    PerspectiveName = CollectiveRouting.SINK_PERSPECTIVE_NAME,
    LastProcessedEventId = null,
    PartitionNumber = 1
  };

  private static MessageEnvelope<IEvent> _envelope(Guid eventId, IEvent payload) => new() {
    MessageId = new MessageId(eventId),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private static StreamEventData _raw(Guid streamId, Guid eventId, int attempts = 0, Guid? eventWorkId = null, int failures = 0) => new() {
    StreamId = streamId,
    EventId = eventId,
    EventType = "collective",
    EventData = "{}",
    Metadata = null,
    Scope = null,
    EventWorkId = eventWorkId ?? Guid.CreateVersion7(),
    PerspectiveName = CollectiveRouting.SINK_PERSPECTIVE_NAME,
    Attempts = attempts,
    Failures = failures
  };

  private static (PerspectiveWorker Worker, Whizbang.Testing.Workers.PerspectiveWorkerTestHarness Harness, FakeCoordinator Coordinator) _createWorker(
      List<PerspectiveWork> work, EventStore eventStore, Registry registry, ICollectiveDispatcher? dispatcher,
      List<Guid>? drainStreamIds = null, List<StreamEventData>? streamEvents = null,
      int? maxPerspectiveEventAttempts = null, IDeadLetterStore? deadLetterStore = null,
      IReceptorInvoker? receptorInvoker = null, ILeaseRenewalChannel? leaseRenewalChannel = null,
      LeaseRegistry? leaseRegistry = null, IProcessedEventCacheObserver? processedEventCacheObserver = null,
      CompositeMetrics? compositeMetrics = null, bool lockBusyCountsAsFailure = false,
      ISyncEventTracker? syncEventTracker = null, TimeProvider? timeProvider = null, int predecessorWaitSeconds = 30,
      PerspectiveMetrics? metrics = null) {
    var instanceProvider = new InstanceProvider();
    var strategy = new InstantCompletionStrategy(logger: NullLogger<InstantCompletionStrategy>.Instance);
    var harness = new Whizbang.Testing.Workers.PerspectiveWorkerTestHarness();
    var coordinator = new FakeCoordinator(work) { DrainStreamIds = drainStreamIds ?? [], StreamEvents = streamEvents ?? [] };

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    if (dispatcher is not null) {
      services.AddSingleton<ICollectiveDispatcher>(dispatcher);
      services.AddSingleton<ICollectiveSessionAccessor>(new StubSessionAccessor());
    }
    if (receptorInvoker is not null) {
      services.AddSingleton<IReceptorInvoker>(receptorInvoker);
    }
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 50,
        MaxPerspectiveEventAttempts = maxPerspectiveEventAttempts,
        CollectiveLockBusyCountsAsFailure = lockBusyCountsAsFailure,
        CollectivePredecessorWaitSeconds = predecessorWaitSeconds,
      }),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: strategy,
      eventTypeProvider: registry,
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: syncEventTracker ?? new SyncEventTracker(),
      logger: NullLogger<PerspectiveWorker>.Instance,
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: processedEventCacheObserver ?? NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(new PerspectiveRewindOptions()),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      leaseRenewalChannel: leaseRenewalChannel ?? new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: harness.DrainChannel,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: deadLetterStore ?? NullDeadLetterStore.Instance,
      generationProvider: (deadLetterStore is null ? null : new DefaultGenerationProvider()) ?? new DefaultGenerationProvider() ?? new DefaultGenerationProvider() ?? new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor((Options.Create(new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 50,
        MaxPerspectiveEventAttempts = maxPerspectiveEventAttempts
      })).Value),
      metrics: metrics,
      timeProvider: timeProvider,
      leaseRegistry: leaseRegistry,
      compositeMetrics: compositeMetrics);
    return (worker, harness, coordinator);
  }

  private sealed record TestCollectiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  private sealed record FlipCollectiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
    public string Chosen { get; init; } = "";
    public Guid? PredecessorId { get; init; }
    public string? PredecessorType { get; init; }
  }

  /// <summary>
  /// Applies a flip: the state becomes the event's choice, so the final state is whichever collective applied last.
  /// Throws for the choice named by <see cref="FailOn"/>.
  /// </summary>
  private sealed class FlipDispatcher(int expected) : ICollectiveDispatcher {
    private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Applied { get; } = [];
    public Task FirstApplied => _firstApplied.Task;
    public string? State { get; private set; }
    public string? FailOn { get; init; }
    /// <summary>A choice whose first apply finds its lock busy after committing one batch; it applies on its retry.</summary>
    public string? BusyOnceOn { get; init; }
    private int _busyOnce;
    private readonly TaskCompletionSource _busy = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Completes when the busy choice has found its lock busy.</summary>
    public Task Busy => _busy.Task;
    public Task AllDispatched => _all.Task;

    public Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) {
      var chosen = ((FlipCollectiveEvent)evt).Chosen;
      if (chosen == FailOn) {
        throw new InvalidOperationException("simulated collective apply failure");
      }
      if (chosen == BusyOnceOn && Interlocked.Exchange(ref _busyOnce, 1) == 0) {
        _busy.TrySetResult();
        throw new CollectiveApplyLockBusyException("wh_per_probe", 30);
      }
      lock (Applied) {
        Applied.Add(chosen);
        State = chosen;
        _firstApplied.TrySetResult();
        if (Applied.Count >= expected) {
          _all.TrySetResult();
        }
      }
      return Task.FromResult(new CollectiveDispatchResult(1, 1));
    }
  }

  private sealed class RecordingDispatcher : ICollectiveDispatcher {
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<(ICollectiveEvent Event, Guid EventId)> Calls { get; } = [];

    /// <summary>Completes on first dispatch — deterministic signal for the async drain path.</summary>
    public Task FirstDispatch => _first.Task;

    public Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) {
      lock (Calls) {
        Calls.Add((evt, collectiveEventId));
      }
      _first.TrySetResult();
      return Task.FromResult(new CollectiveDispatchResult(1, 1) { ChangedProperties = ChangedProperties });
    }

    /// <summary>What the apply reports its specs assigned.</summary>
    public IReadOnlyDictionary<Type, IReadOnlyList<string>> ChangedProperties { get; init; } =
      new Dictionary<Type, IReadOnlyList<string>>();
  }

  /// <summary>Dispatcher that reports N apply batches through onBatchApplied — the long-apply shape.</summary>
  private sealed class BatchReportingDispatcher(int batches) : ICollectiveDispatcher {
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task FirstDispatch => _first.Task;

    public async Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) {
      for (var i = 0; i < batches; i++) {
        if (onBatchApplied is not null) {
          await onBatchApplied(cancellationToken);
        }
      }
      _first.TrySetResult();
      return new CollectiveDispatchResult(1, batches);
    }
  }

  /// <summary>Reports one apply batch, then parks until released — keeps the dispatch (and any
  /// lease handles the worker holds for it) alive while the renewal flush runs, deterministically.</summary>
  private sealed class GatedBatchReportingDispatcher : ICollectiveDispatcher {
    public TaskCompletionSource ReleaseDispatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) {
      if (onBatchApplied is not null) {
        await onBatchApplied(cancellationToken);
      }
      await ReleaseDispatch.Task.WaitAsync(cancellationToken);
      return new CollectiveDispatchResult(1, 1);
    }
  }

  /// <summary>Reports one batch at a time, awaiting an ack between batches — used to prove each
  /// batch's renewal individually traversed the real flush before the next batch reports.</summary>
  private sealed class AckedBatchReportingDispatcher(int batches, Func<int, Task> ackForBatch) : ICollectiveDispatcher {
    public async Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) {
      for (var batch = 1; batch <= batches; batch++) {
        if (onBatchApplied is not null) {
          await onBatchApplied(cancellationToken);
        }
        await ackForBatch(batch).WaitAsync(cancellationToken);
      }
      return new CollectiveDispatchResult(1, batches);
    }
  }

  /// <summary>Coordinator for the REAL LeaseRenewalWorker's flush scope — records what actually
  /// reaches RenewLeasesAsync (i.e. what survived the LeaseRegistry handle filter).</summary>
  private sealed class RenewCapturingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    private readonly TaskCompletionSource<(WorkCategory Category, IReadOnlyList<Guid> Ids)> _first =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, int> _renewalCounts = [];
    private readonly List<(Guid Id, int Count, TaskCompletionSource Signal)> _waiters = [];

    public Task<(WorkCategory Category, IReadOnlyList<Guid> Ids)> FirstRenewal => _first.Task;

    public int RenewalCount(Guid id) {
      lock (_lock) {
        return _renewalCounts.GetValueOrDefault(id);
      }
    }

    /// <summary>Completes once <paramref name="id"/> has been renewed at least <paramref name="count"/> times.</summary>
    public Task WaitForRenewalCountAsync(Guid id, int count) {
      lock (_lock) {
        if (_renewalCounts.GetValueOrDefault(id) >= count) {
          return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add((id, count, tcs));
        return tcs.Task;
      }
    }

    public Task<int> RenewLeasesAsync(WorkCategory category, IReadOnlyList<Guid> ids, int leaseSeconds = 300, CancellationToken cancellationToken = default) {
      _first.TrySetResult((category, [.. ids]));
      lock (_lock) {
        foreach (var id in ids) {
          _renewalCounts[id] = _renewalCounts.GetValueOrDefault(id) + 1;
        }
        for (var i = _waiters.Count - 1; i >= 0; i--) {
          var (id, count, signal) = _waiters[i];
          if (_renewalCounts.GetValueOrDefault(id) >= count) {
            signal.TrySetResult();
            _waiters.RemoveAt(i);
          }
        }
      }
      return Task.FromResult(ids.Count);
    }
  }

  private sealed class BusyLockDispatcher : ICollectiveDispatcher {
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task FirstDispatch => _first.Task;

    public Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) {
      _first.TrySetResult();
      throw new CollectiveApplyLockBusyException("wh_per_probe", 30);
    }
  }

  private sealed class ThrowingDispatcher : ICollectiveDispatcher {
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public Task FirstDispatch => _first.Task;

    public Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _calls);
      _first.TrySetResult();
      throw new InvalidOperationException(
        "simulated collective apply failure (e.g. SetProperty does not represent a valid property to be set)");
    }
  }

  /// <summary>Signals when the sink dedups a re-offered, already-completed work row.</summary>
  private sealed class SinkDedupObserver : IProcessedEventCacheObserver {
    private readonly TaskCompletionSource _firstSink = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<(IReadOnlyList<Guid> Ids, string PerspectiveName, Guid StreamId)> Deduped { get; } = [];
    public Task FirstSinkDedup => _firstSink.Task;

    public void OnEventsDeduped(IReadOnlyList<Guid> dedupedEventIds, string perspectiveName, Guid streamId) {
      lock (Deduped) {
        Deduped.Add((dedupedEventIds, perspectiveName, streamId));
      }
      if (perspectiveName == CollectiveRouting.SINK_PERSPECTIVE_NAME) {
        _firstSink.TrySetResult();
      }
    }

    public void OnEventsMarkedInFlight(IReadOnlyList<Guid> eventIds) { }
    public void OnRetentionActivated(int count) { }
    public void OnEvicted(int count) { }
    public void OnEventsRemoved(IReadOnlyList<Guid> eventIds) { }
  }

  private sealed class StubSessionAccessor : ICollectiveSessionAccessor {
    public object GetSession(IServiceProvider scopedServiceProvider) => new();
  }

  /// <summary>A post-apply receptor that throws — the apply already committed, so the sink must isolate this
  /// and neither crash nor undo the completion. Records that it was invoked so the test can assert it ran.</summary>
  private sealed class ThrowingReceptorInvoker : IReceptorInvoker {
    private readonly TaskCompletionSource _firstInvoke = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task FirstInvoke => _firstInvoke.Task;
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      _firstInvoke.TrySetResult();
      throw new InvalidOperationException("simulated post-apply completion-receptor failure");
    }
  }

  /// <summary>Captures every (eventId, stage) the sink drives through the lifecycle after a collective apply.</summary>
  private sealed class CapturingReceptorInvoker : IReceptorInvoker {
    private readonly TaskCompletionSource _firstPostAll = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<(Guid EventId, LifecycleStage Stage)> Invocations { get; } = [];
    public List<(LifecycleStage Stage, MessageChanges? Changes)> Changes { get; } = [];
    /// <summary>Completes on the first PostAllPerspectives* invocation — deterministic signal for the async path.</summary>
    public Task FirstPostAllPerspectives => _firstPostAll.Task;

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (Invocations) {
        Invocations.Add((envelope.MessageId.Value, stage));
        Changes.Add((stage, (context as LifecycleExecutionContext)?.Changes));
      }
      if (stage is LifecycleStage.PostAllPerspectivesInline or LifecycleStage.PostAllPerspectivesDetached) {
        _firstPostAll.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class RecordingDeadLetterStore : IDeadLetterStore {
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<(string SourceTable, Guid SourceId, MessageFailureReason Reason)> Moves { get; } = [];
    /// <summary>Completes on the first dead-letter move — deterministic signal for the async drain path.</summary>
    public Task FirstMove => _first.Task;

    public Task<Guid?> MoveAsync(
        Guid deadLetterId, string sourceTable, Guid sourceId, MessageFailureReason failureReason,
        string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      lock (Moves) {
        Moves.Add((sourceTable, sourceId, failureReason));
      }
      _first.TrySetResult();
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private sealed class FakeCoordinator(List<PerspectiveWork> work) : NoOpWorkCoordinator, IWorkCoordinator {
    private int _cycle;
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _waiters = new();
    private readonly TaskCompletionSource _firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<Guid> DrainStreamIds { get; init; } = [];
    public List<StreamEventData> StreamEvents { get; init; } = [];
    public List<PerspectiveCursorFailure> ReportedFailures { get; } = [];
    /// <summary>Completes on the first reported perspective failure — the collective-apply-failed signal.</summary>
    public Task FirstFailure => _firstFailure.Task;
    private readonly TaskCompletionSource _firstCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<PerspectiveCursorCompletion> ReportedCompletions { get; } = [];
    /// <summary>Completes on the first reported cursor completion.</summary>
    public Task FirstCompletion => _firstCompletion.Task;
    private readonly ConcurrentDictionary<(Guid StreamId, string PerspectiveName), Guid> _cursors = new();
    Task IWorkCoordinator.ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken) {
      lock (ReportedCompletions) { ReportedCompletions.Add(completion); }
      _cursors[(completion.StreamId, completion.PerspectiveName)] = completion.LastEventId;
      _firstCompletion.TrySetResult();
      return Task.CompletedTask;
    }
    // The cursor a reported completion advanced, as the real coordinator keeps it. The worker's consumer loops share
    // one work channel, so a stream's sink rows can reach two runs; the second starts from the cursor the first left.
    Task<PerspectiveCursorInfo?> IWorkCoordinator.GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken) =>
      Task.FromResult(_cursors.TryGetValue((streamId, perspectiveName), out var last)
        ? new PerspectiveCursorInfo { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = last }
        : null);
    Task IWorkCoordinator.ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken) {
      lock (ReportedFailures) { ReportedFailures.Add(failure); }
      _firstFailure.TrySetResult();
      return Task.CompletedTask;
    }
    public Task WaitForCyclesAsync(int minCycles, TimeSpan timeout) =>
      _waiters.GetOrAdd(minCycles, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(timeout);
    private List<PerspectiveWork>? _laterWork;
    /// <summary>Offers <paramref name="later"/> on the next claim cycle.</summary>
    public void OfferWork(List<PerspectiveWork> later) => Interlocked.Exchange(ref _laterWork, later);
    private int _extraDrainOffers;
    /// <summary>Re-offers the drain stream ids on the next claim cycle — models the production
    /// drain refetch re-serving a stream whose completion flush hasn't landed yet.</summary>
    public void OfferDrainAgain() => Interlocked.Increment(ref _extraDrainOffers);
    public new Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      var c = Interlocked.Increment(ref _cycle);
      foreach (var kv in _waiters) { if (c >= kv.Key) { kv.Value.TrySetResult(); } }
      var later = Interlocked.Exchange(ref _laterWork, null);
      List<PerspectiveWork> pw = c == 1 ? [.. work] : later ?? [];
      var reoffer = Interlocked.Exchange(ref _extraDrainOffers, 0) > 0;
      var sids = c == 1 || reoffer ? new List<Guid>(DrainStreamIds) : [];
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = pw, PerspectiveStreamIds = sids });
    }
    /// <summary>The stream's collective queue, or null for an engine without one (the legacy after-cursor read).</summary>
    public List<CollectiveSinkQueueEntry>? SinkQueue { get; set; }
    private readonly TaskCompletionSource _sinkQueueRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Completes once the sink has read its queue.</summary>
    public Task SinkQueueRead => _sinkQueueRead.Task;
    public List<Guid> EventsFetchedById { get; } = [];
    /// <summary>Per-stream queues, for a test with more than one sink stream; a stream not here reads <see cref="SinkQueue"/>.</summary>
    public Dictionary<Guid, List<CollectiveSinkQueueEntry>> SinkQueues { get; } = [];
    private int _sinkQueueReads;
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _sinkQueueReadWaiters = new();
    /// <summary>Completes once the sink has read a queue <paramref name="count"/> times, counting reads before this call.</summary>
    public Task SinkQueueReads(int count) {
      var waiter = _sinkQueueReadWaiters.GetOrAdd(count, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      if (Volatile.Read(ref _sinkQueueReads) >= count) {
        waiter.TrySetResult();
      }
      return waiter.Task;
    }
    Task<IReadOnlyList<CollectiveSinkQueueEntry>?> IWorkCoordinator.FetchCollectiveSinkQueueAsync(Guid streamId, CancellationToken cancellationToken) {
      var reads = Interlocked.Increment(ref _sinkQueueReads);
      foreach (var (count, waiter) in _sinkQueueReadWaiters) {
        if (reads >= count) {
          waiter.TrySetResult();
        }
      }
      _sinkQueueRead.TrySetResult();
      return Task.FromResult<IReadOnlyList<CollectiveSinkQueueEntry>?>(SinkQueues.TryGetValue(streamId, out var own) ? own : SinkQueue);
    }
    /// <summary>Events the store does not hold: a predecessor that has not arrived.</summary>
    public HashSet<Guid> MissingEventIds { get; } = [];
    Task<IReadOnlyList<StreamEventData>> IWorkCoordinator.FetchEventsByIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken) {
      lock (EventsFetchedById) { EventsFetchedById.AddRange(eventIds); }
      lock (MissingEventIds) {
        return Task.FromResult<IReadOnlyList<StreamEventData>>([.. eventIds.Where(id => !MissingEventIds.Contains(id)).Select(id => _raw(Guid.Empty, id))]);
      }
    }
    // Explicit interface implementation so the worker's interface call routes here, overriding the
    // IWorkCoordinator default (which returns empty and would short-circuit the drain fetch).
    Task<List<StreamEventData>> IWorkCoordinator.GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken) =>
      Task.FromResult(new List<StreamEventData>(StreamEvents));
  }

  private sealed class EventStore : IEventStore {
    public ConcurrentDictionary<Guid, List<MessageEnvelope<IEvent>>> Envelopes { get; } = new();
    /// <summary>Envelopes the drain fetch's <see cref="DeserializeStreamEvents"/> yields (the fake
    /// stand-in for AOT JSON deserialization of the raw rows).</summary>
    public List<MessageEnvelope<IEvent>> Deserialized { get; init; } = [];
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(Envelopes.TryGetValue(streamId, out var e) ? e.ToList() : []);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [.. Deserialized];
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) => _empty<TMessage>();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) => _empty<TMessage>();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
    private static async IAsyncEnumerable<MessageEnvelope<T>> _empty<T>() { await Task.CompletedTask; yield break; }
  }

  private sealed class Registry(IReadOnlyList<Type> eventTypes) : IPerspectiveRunnerRegistry {
    // Sink perspective has no runner — return null so the worker would fall through (but the sink guard fires first).
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    // Must be non-empty: the worker skips building _perspectivesPerEventType when no perspectives are
    // registered (PerspectiveWorker._initializePerspectiveRegistryAsync), which then short-circuits the
    // drain fetch. A dummy non-collective registration keeps the map non-null without affecting the sink
    // (the collective branch in _collectDrainModePerspectiveNames fires before this map is consulted).
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [
      new PerspectiveRegistrationInfo(
        "Test.NonCollectivePerspective",
        "global::Test.NonCollectivePerspective",
        "global::Test.Model",
        [.. eventTypes.Select(TypeNameFormatter.Format)])
    ];
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class TrackingRunner : IPerspectiveRunner {
    public int RunWithEventsCount { get; private set; }
    public Type PerspectiveType => typeof(object);
    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = Guid.Empty, Status = PerspectiveProcessingStatus.Completed });
    public Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
      RunWithEventsCount++;
      return Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = Guid.Empty, Status = PerspectiveProcessingStatus.Completed });
    }
    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = Guid.Empty, Status = PerspectiveProcessingStatus.Completed });
    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private sealed class InstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "CollectiveSinkTest";
    public string HostName => "test-host";
    public int ProcessId => 1234;
    ServiceInstanceInfo IServiceInstanceProvider.ToInfo() =>
      new() { ServiceName = ServiceName, InstanceId = InstanceId, HostName = HostName, ProcessId = ProcessId };
  }
}
