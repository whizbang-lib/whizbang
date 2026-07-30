using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Unit tests for the PerspectiveWorker collective-event sink seam (<c>_processCollectiveSinkAsync</c>):
/// a perspective-work item for the <see cref="CollectiveRouting.SINK_PERSPECTIVE_NAME"/> sink is dispatched
/// through <see cref="ICollectiveDispatcher"/> exactly once and bypasses the per-stream runner. Covers the
/// dispatch path, the not-configured short-circuit, and the no-collective-event short-circuit.
/// </summary>
[NotInParallel("CollectiveSinkWorker")]
public class PerspectiveWorkerCollectiveSinkTests {

  [Test]
  public async Task CollectiveSink_DispatchesEventOnceAndSkipsRunner_Async() {
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new _recordingDispatcher();
    var runner = new _trackingRunner();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new _eventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new _registry(runner, [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.WaitForCyclesAsync(2, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

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
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-lease") };
    var dispatcher = new _batchReportingDispatcher(batches: 3);
    var leaseChannel = new Whizbang.Testing.Workers.CapturingLeaseRenewalChannel();
    var sinkWork = _sinkWork(streamId);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new _eventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher,
      leaseRenewalChannel: leaseChannel);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.WaitForCyclesAsync(2, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

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
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-lease-real") };
    var dispatcher = new _gatedBatchReportingDispatcher();
    var sinkWork = _sinkWork(streamId);

    var leaseRegistry = new LeaseRegistry();
    var renewCoordinator = new _renewCapturingCoordinator();
    var renewalServices = new ServiceCollection();
    renewalServices.AddSingleton<IWorkCoordinator>(renewCoordinator);
    await using var renewalProvider = renewalServices.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var renewalWorker = new LeaseRenewalWorker(
      renewalProvider.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new LeaseRenewalWorkerOptions {
        Flusher = new BatchFlusherOptions { MaxBatchSize = 10, CoalesceWindowMs = 10, ImmediateFlushThreshold = 1 }
      }),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaseRenewalWorker>.Instance,
      leaseRegistry);
    await renewalWorker.StartAsync(CancellationToken.None);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new _eventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher,
      leaseRenewalChannel: renewalWorker,
      leaseRegistry: leaseRegistry);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    try {
      var renewed = await renewCoordinator.FirstRenewal.WaitAsync(TimeSpan.FromSeconds(10));
      await Assert.That(renewed.Category).IsEqualTo(WorkCategory.PerspectiveEvent);
      await Assert.That(renewed.Ids).Contains(sinkWork.WorkId)
        .Because("an enqueued sink renewal must survive the registry filter and reach " +
                 "IWorkCoordinator.RenewLeasesAsync — without a registered LeaseHandle the flush " +
                 "silently drops it and the DB lease still expires mid-apply.");
    } finally {
      dispatcher.ReleaseDispatch.TrySetResult();
      cts.Cancel();
      try { await workerTask; } catch (OperationCanceledException) { }
      try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
      await renewalWorker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task CollectiveSink_ManyBatchApply_RenewalsAreNotCappedAsync() {
    // Renewal enqueues from the sink are PROGRESS-GATED: one fires only when an apply batch has
    // committed, so a hung apply stops enqueuing and its lease expires naturally. The
    // MaxRenewalsPerWork cap exists to surface renewals that fire regardless of progress (timers);
    // applied to the sink it makes every apply longer than the cap lose renewal protection at
    // batch N+1 and spam a warning per remaining batch. Eight acked batches must therefore produce
    // eight renewals through the REAL worker + registry — the default cap of six fails batch 7.
    const int BATCHES = 8;
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-lease-uncapped") };
    var sinkWork = _sinkWork(streamId);

    var leaseRegistry = new LeaseRegistry();
    var renewCoordinator = new _renewCapturingCoordinator();
    var dispatcher = new _ackedBatchReportingDispatcher(
      BATCHES, batchNumber => renewCoordinator.WaitForRenewalCountAsync(sinkWork.WorkId, batchNumber));
    var renewalServices = new ServiceCollection();
    renewalServices.AddSingleton<IWorkCoordinator>(renewCoordinator);
    await using var renewalProvider = renewalServices.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var renewalWorker = new LeaseRenewalWorker(
      renewalProvider.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new LeaseRenewalWorkerOptions {
        Flusher = new BatchFlusherOptions { MaxBatchSize = 10, CoalesceWindowMs = 10, ImmediateFlushThreshold = 1 }
      }),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaseRenewalWorker>.Instance,
      leaseRegistry);
    await renewalWorker.StartAsync(CancellationToken.None);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new _eventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher,
      leaseRenewalChannel: renewalWorker,
      leaseRegistry: leaseRegistry);

    var workerTask = worker.StartAsync(cts.Token);
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
      cts.Cancel();
      try { await workerTask; } catch (OperationCanceledException) { }
      try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
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
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var workId = TrackedGuid.NewMedo().Value;
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-echo") };
    var dispatcher = new _recordingDispatcher();
    var observer = new _sinkDedupObserver();
    var envelope = _envelope(eventId, collectiveEvent);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new _eventStore { Envelopes = { [streamId] = [envelope] }, Deserialized = [envelope] },
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [streamId],
      streamEvents: [_raw(streamId, eventId, eventWorkId: workId)],
      processedEventCacheObserver: observer);

    var workerTask = worker.StartAsync(cts.Token);
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
      cts.Cancel();
      try { await workerTask; } catch (OperationCanceledException) { }
      try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
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
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new _recordingDispatcher();
    var runner = new _trackingRunner();
    var envelope = _envelope(eventId, collectiveEvent);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [], // nothing on the channel/standard path
      eventStore: new _eventStore {
        Envelopes = { [streamId] = [envelope] },
        Deserialized = [envelope] // drain fetch → deserialize yields the collective envelope
      },
      registry: new _registry(runner, [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [streamId],
      streamEvents: [_raw(streamId, eventId)]);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    // Deterministic wait on the actual dispatch — the drain channel processes the claimed stream
    // asynchronously, so cycle counting would race it.
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

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
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var sinkWork = _sinkWork(streamId);
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new _recordingDispatcher();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new _eventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    // The fix enqueues the sink's event_work_id for deletion; the worker's idle-tick flush drains it to
    // the completion channel. Wait on that deterministically instead of racing the idle loop.
    await harness.CompletionCapture.FirstEventWorkId.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(dispatcher.Calls.Count).IsEqualTo(1)
      .Because("The collective event is dispatched exactly once.");
    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(sinkWork.WorkId)
      .Because("A successful sink dispatch must delete its own __collective__ work row by event_work_id, " +
        "so claim_orphaned can't re-lease it into the re-dispatch loop that caused the production bloat.");
  }

  /// <summary>
  /// Same regression as <see cref="CollectiveSink_SuccessfulDispatch_CompletesSinkWorkRowByEventWorkId_Async"/>,
  /// but via the production DRAIN route (a collective event claimed as a stream, not injected as channel work).
  /// The drain twin of the sink guard must resolve the sink row's <c>event_work_id</c> from the drain batch's
  /// raw rows and complete it the same way.
  /// </summary>
  [Test]
  public async Task CollectiveSink_ViaDrainPath_CompletesSinkWorkRowByEventWorkId_Async() {
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var workId = TrackedGuid.NewMedo().Value;
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new _recordingDispatcher();
    var envelope = _envelope(eventId, collectiveEvent);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new _eventStore { Envelopes = { [streamId] = [envelope] }, Deserialized = [envelope] },
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [streamId],
      streamEvents: [_raw(streamId, eventId, eventWorkId: workId)]);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.CompletionCapture.FirstEventWorkId.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

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
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new _recordingDispatcher();
    var invoker = new _capturingReceptorInvoker();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new _eventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher,
      receptorInvoker: invoker);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    await invoker.FirstPostAllPerspectives.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(dispatcher.Calls.Count).IsEqualTo(1);
    await Assert.That(invoker.Invocations.Any(i => i.EventId == eventId && i.Stage == LifecycleStage.PostAllPerspectivesInline)).IsTrue()
      .Because("A successful collective apply must run its event through PostAllPerspectives so the completion " +
        "receptor/tag fires — otherwise the frontend's completion toast waits forever.");
  }

  /// <summary>
  /// The mirror guard: a FAILED collective apply must NOT fire the PostAllPerspectives lifecycle — the apply
  /// did not complete, so emitting a "completed" signal would be a lie (and would prematurely dismiss the toast).
  /// </summary>
  [Test]
  public async Task CollectiveSink_DispatchThrows_DoesNotFirePostAllPerspectives_Async() {
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new _throwingDispatcher();
    var invoker = new _capturingReceptorInvoker();
    var envelope = _envelope(eventId, collectiveEvent);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new _eventStore { Envelopes = { [streamId] = [envelope] }, Deserialized = [envelope] },
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [streamId],
      streamEvents: [_raw(streamId, eventId)],
      receptorInvoker: invoker);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

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
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var sinkWork = _sinkWork(streamId);
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new _recordingDispatcher();
    var invoker = new _throwingReceptorInvoker();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new _eventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher,
      receptorInvoker: invoker);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    await invoker.FirstInvoke.WaitAsync(TimeSpan.FromSeconds(10));
    // The apply succeeded and its work row must still be completed despite the throwing post-apply receptor.
    await harness.CompletionCapture.FirstEventWorkId.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { } catch { /* host must not have faulted */ }
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(dispatcher.Calls.Count).IsEqualTo(1);
    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(sinkWork.WorkId)
      .Because("A throwing post-apply receptor must not undo the completed apply's work-row deletion.");
  }

  [Test]
  public async Task CollectiveSink_NoDispatcherRegistered_DoesNotThrow_Async() {
    var streamId = TrackedGuid.NewMedo().Value;
    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new _eventStore { Envelopes = { [streamId] = [_envelope(TrackedGuid.NewMedo().Value, new _testCollectiveEvent { Scope = new TenantCollectiveScope("t") })] } },
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: null); // not configured

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.WaitForCyclesAsync(2, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }
    // No assertion needed beyond "did not throw" — the not-configured branch logs and returns.
  }

  [Test]
  public async Task CollectiveSink_NoCollectiveEventOnStream_NoDispatch_Async() {
    var streamId = TrackedGuid.NewMedo().Value;
    var sinkWork = _sinkWork(streamId);
    var dispatcher = new _recordingDispatcher();
    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new _eventStore(), // empty — no events on the stream
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    // A leased sink row with no collective event on the stream (cursor already advanced past it, or a
    // stale re-lease) must still be completed — otherwise it re-leases forever. Wait on that deletion.
    await harness.CompletionCapture.FirstEventWorkId.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

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
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new _throwingDispatcher();
    var envelope = _envelope(eventId, collectiveEvent);

    // DRAIN path (the production route: a collective event arriving via transport is claimed as a stream).
    // Its caller catches only OperationCanceledException, so an un-guarded apply failure propagates and faults
    // the worker host — exactly the dev crash-loop.
    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new _eventStore { Envelopes = { [streamId] = [envelope] }, Deserialized = [envelope] },
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [streamId],
      streamEvents: [_raw(streamId, eventId)]);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(TimeSpan.FromSeconds(10));
    // The fix catches the apply failure and REPORTS it as a perspective failure (instead of letting it
    // propagate and fault the host). The un-guarded code throws raw and never reports — so this wait times
    // out, which is the RED signal.
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { } catch { /* host must not have faulted */ }
    // Fully stop the background worker so it doesn't linger and starve sibling [NotInParallel] tests.
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

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
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var workId = TrackedGuid.NewMedo().Value;
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new _throwingDispatcher();
    var deadLetters = new _recordingDeadLetterStore();
    var envelope = _envelope(eventId, collectiveEvent);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new _eventStore { Envelopes = { [streamId] = [envelope] }, Deserialized = [envelope] },
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [streamId],
      streamEvents: [_raw(streamId, eventId, attempts: 5, eventWorkId: workId)],
      maxPerspectiveEventAttempts: 2,
      deadLetterStore: deadLetters);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await deadLetters.FirstMove.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

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

  private static StreamEventData _raw(Guid streamId, Guid eventId, int attempts = 0, Guid? eventWorkId = null) => new() {
    StreamId = streamId,
    EventId = eventId,
    EventType = "collective",
    EventData = "{}",
    Metadata = null,
    Scope = null,
    EventWorkId = eventWorkId ?? Guid.CreateVersion7(),
    PerspectiveName = CollectiveRouting.SINK_PERSPECTIVE_NAME,
    Attempts = attempts
  };

  private static (PerspectiveWorker Worker, Whizbang.Testing.Workers.PerspectiveWorkerTestHarness Harness, _coordinator Coordinator) _createWorker(
      List<PerspectiveWork> work, _eventStore eventStore, _registry registry, ICollectiveDispatcher? dispatcher,
      List<Guid>? drainStreamIds = null, List<StreamEventData>? streamEvents = null,
      int? maxPerspectiveEventAttempts = null, IDeadLetterStore? deadLetterStore = null,
      IReceptorInvoker? receptorInvoker = null, ILeaseRenewalChannel? leaseRenewalChannel = null,
      LeaseRegistry? leaseRegistry = null, IProcessedEventCacheObserver? processedEventCacheObserver = null) {
    var instanceProvider = new _instanceProvider();
    var strategy = new InstantCompletionStrategy();
    var harness = new Whizbang.Testing.Workers.PerspectiveWorkerTestHarness();
    var coordinator = new _coordinator(work) { DrainStreamIds = drainStreamIds ?? [], StreamEvents = streamEvents ?? [] };

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    if (dispatcher is not null) {
      services.AddSingleton<ICollectiveDispatcher>(dispatcher);
      services.AddSingleton<ICollectiveSessionAccessor>(new _stubSessionAccessor());
    }
    if (receptorInvoker is not null) {
      services.AddSingleton<IReceptorInvoker>(receptorInvoker);
    }
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 50,
        MaxPerspectiveEventAttempts = maxPerspectiveEventAttempts
      }),
      tracingOptions: null,
      strategy,
      eventTypeProvider: registry,
      processedEventCacheObserver: processedEventCacheObserver,
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      leaseRenewalChannel: leaseRenewalChannel,
      perspectiveDrainChannel: harness.DrainChannel,
      deadLetterStore: deadLetterStore,
      generationProvider: deadLetterStore is null ? null : new DefaultGenerationProvider(),
      leaseRegistry: leaseRegistry);
    return (worker, harness, coordinator);
  }

  private sealed record _testCollectiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  private sealed class _recordingDispatcher : ICollectiveDispatcher {
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<(ICollectiveEvent Event, Guid EventId)> Calls { get; } = [];

    /// <summary>Completes on first dispatch — deterministic signal for the async drain path.</summary>
    public Task FirstDispatch => _first.Task;

    public Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, Func<CancellationToken, ValueTask>? onBatchApplied, CancellationToken cancellationToken) {
      lock (Calls) {
        Calls.Add((evt, collectiveEventId));
      }
      _first.TrySetResult();
      return Task.FromResult(new CollectiveDispatchResult(1, 1));
    }
  }

  /// <summary>Dispatcher that reports N apply batches through onBatchApplied — the long-apply shape.</summary>
  private sealed class _batchReportingDispatcher(int batches) : ICollectiveDispatcher {
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task FirstDispatch => _first.Task;

    public async Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, Func<CancellationToken, ValueTask>? onBatchApplied, CancellationToken cancellationToken) {
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
  private sealed class _gatedBatchReportingDispatcher : ICollectiveDispatcher {
    public TaskCompletionSource ReleaseDispatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, Func<CancellationToken, ValueTask>? onBatchApplied, CancellationToken cancellationToken) {
      if (onBatchApplied is not null) {
        await onBatchApplied(cancellationToken);
      }
      await ReleaseDispatch.Task.WaitAsync(cancellationToken);
      return new CollectiveDispatchResult(1, 1);
    }
  }

  /// <summary>Reports one batch at a time, awaiting an ack between batches — used to prove each
  /// batch's renewal individually traversed the real flush before the next batch reports.</summary>
  private sealed class _ackedBatchReportingDispatcher(int batches, Func<int, Task> ackForBatch) : ICollectiveDispatcher {
    public async Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, Func<CancellationToken, ValueTask>? onBatchApplied, CancellationToken cancellationToken) {
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
  private sealed class _renewCapturingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
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

  private sealed class _throwingDispatcher : ICollectiveDispatcher {
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public Task FirstDispatch => _first.Task;

    public Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, Func<CancellationToken, ValueTask>? onBatchApplied, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _calls);
      _first.TrySetResult();
      throw new InvalidOperationException(
        "simulated collective apply failure (e.g. SetProperty does not represent a valid property to be set)");
    }
  }

  /// <summary>Signals when the sink dedups a re-offered, already-completed work row.</summary>
  private sealed class _sinkDedupObserver : IProcessedEventCacheObserver {
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

  private sealed class _stubSessionAccessor : ICollectiveSessionAccessor {
    public object GetSession(IServiceProvider scopedServiceProvider) => new object();
  }

  /// <summary>A post-apply receptor that throws — the apply already committed, so the sink must isolate this
  /// and neither crash nor undo the completion. Records that it was invoked so the test can assert it ran.</summary>
  private sealed class _throwingReceptorInvoker : IReceptorInvoker {
    private readonly TaskCompletionSource _firstInvoke = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task FirstInvoke => _firstInvoke.Task;
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      _firstInvoke.TrySetResult();
      throw new InvalidOperationException("simulated post-apply completion-receptor failure");
    }
  }

  /// <summary>Captures every (eventId, stage) the sink drives through the lifecycle after a collective apply.</summary>
  private sealed class _capturingReceptorInvoker : IReceptorInvoker {
    private readonly TaskCompletionSource _firstPostAll = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<(Guid EventId, LifecycleStage Stage)> Invocations { get; } = [];
    /// <summary>Completes on the first PostAllPerspectives* invocation — deterministic signal for the async path.</summary>
    public Task FirstPostAllPerspectives => _firstPostAll.Task;

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (Invocations) {
        Invocations.Add((envelope.MessageId.Value, stage));
      }
      if (stage is LifecycleStage.PostAllPerspectivesInline or LifecycleStage.PostAllPerspectivesDetached) {
        _firstPostAll.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class _recordingDeadLetterStore : IDeadLetterStore {
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

  private sealed class _coordinator(List<PerspectiveWork> work) : NoOpWorkCoordinator, IWorkCoordinator {
    private int _cycle;
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _waiters = new();
    private readonly TaskCompletionSource _firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<Guid> DrainStreamIds { get; init; } = [];
    public List<StreamEventData> StreamEvents { get; init; } = [];
    public List<PerspectiveCursorFailure> ReportedFailures { get; } = [];
    /// <summary>Completes on the first reported perspective failure — the collective-apply-failed signal.</summary>
    public Task FirstFailure => _firstFailure.Task;
    Task IWorkCoordinator.ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct) {
      lock (ReportedFailures) { ReportedFailures.Add(failure); }
      _firstFailure.TrySetResult();
      return Task.CompletedTask;
    }
    public Task WaitForCyclesAsync(int minCycles, TimeSpan timeout) =>
      _waiters.GetOrAdd(minCycles, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(timeout);
    private int _extraDrainOffers;
    /// <summary>Re-offers the drain stream ids on the next claim cycle — models the production
    /// drain refetch re-serving a stream whose completion flush hasn't landed yet.</summary>
    public void OfferDrainAgain() => Interlocked.Increment(ref _extraDrainOffers);
    public new Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken ct = default) {
      var c = Interlocked.Increment(ref _cycle);
      foreach (var kv in _waiters) { if (c >= kv.Key) { kv.Value.TrySetResult(); } }
      var pw = c == 1 ? new List<PerspectiveWork>(work) : [];
      var reoffer = Interlocked.Exchange(ref _extraDrainOffers, 0) > 0;
      var sids = c == 1 || reoffer ? new List<Guid>(DrainStreamIds) : [];
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = pw, PerspectiveStreamIds = sids });
    }
    // Explicit interface implementation so the worker's interface call routes here, overriding the
    // IWorkCoordinator default (which returns empty and would short-circuit the drain fetch).
    Task<List<StreamEventData>> IWorkCoordinator.GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken ct) =>
      Task.FromResult(new List<StreamEventData>(StreamEvents));
  }

  private sealed class _eventStore : IEventStore {
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
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
    private static async IAsyncEnumerable<MessageEnvelope<T>> _empty<T>([EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
  }

  private sealed class _registry(_trackingRunner runner, IReadOnlyList<Type> eventTypes) : IPerspectiveRunnerRegistry, IEventTypeProvider {
    // Sink perspective has no runner — return null so the worker would fall through (but the sink guard fires first).
    public IPerspectiveRunner? GetRunner(string name, IServiceProvider serviceProvider) => null;
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
    public _trackingRunner Runner => runner;
  }

  private sealed class _trackingRunner : IPerspectiveRunner {
    public int RunWithEventsCount { get; private set; }
    public Type PerspectiveType => typeof(object);
    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) =>
      Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = Guid.Empty, Status = PerspectiveProcessingStatus.Completed });
    public Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
      RunWithEventsCount++;
      return Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = Guid.Empty, Status = PerspectiveProcessingStatus.Completed });
    }
    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = Guid.Empty, Status = PerspectiveProcessingStatus.Completed });
    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private sealed class _instanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "CollectiveSinkTest";
    public string HostName => "test-host";
    public int ProcessId => 1234;
    ServiceInstanceInfo IServiceInstanceProvider.ToInfo() =>
      new() { ServiceName = ServiceName, InstanceId = InstanceId, HostName = HostName, ProcessId = ProcessId };
  }
}
