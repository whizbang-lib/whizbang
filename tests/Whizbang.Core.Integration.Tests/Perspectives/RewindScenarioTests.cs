using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Integration.Tests.Perspectives;

/// <summary>
/// Lock-in integration tests for the perspective rewind path — handler idempotency per event id.
/// </summary>
/// <remarks>
/// <para>
/// Scenario under test (Service A → Service B):
/// <list type="number">
///   <item>A fires events 1-10.</item>
///   <item>B receives 1,2,4,5 in order → handlers fire, perspective applies.</item>
///   <item>B receives 3,6,7,8 → event 3 is out-of-order, triggering a rewind.</item>
///   <item>Rewind replays 1..8 in memory while 9,10 may arrive concurrently.</item>
///   <item>Handlers must fire for {3,6,7,8,9,10} (never-processed) and must NOT re-fire for {1,2,4,5}.</item>
/// </list>
/// </para>
/// <para>
/// These tests drive the real <see cref="PerspectiveWorker"/> with in-process fakes so they run
/// deterministically in CI without Postgres. SQL-level checks (store_perspective_events detection,
/// LEFT JOIN against wh_perspective_events) live in the Postgres integration projects.
/// </para>
/// </remarks>
/// <docs>operations/workers/perspective-worker#rewind-flow</docs>
[Category("Integration")]
[NotInParallel("RewindScenarioIntegration")]
public class RewindScenarioTests {

  // Event ordering: Guid.CreateVersion7() returns monotonically increasing UUIDv7 values,
  // so the order in which we create the envelopes gives us our stream ordering.

  // ==================== CONTRACT: Out-of-order trigger event gets its handlers fired ====================

  [Test]
  public async Task Rewind_OutOfOrderTriggerEvent_FiresPostPerspectiveHandlers_Async() {
    // Scenario: events 1,2,4,5 already processed (cursor at event 5).
    // Event 3 arrives late, triggering rewind. Event 3's id < cursor, but its handlers must still fire.
    var streamId = Guid.CreateVersion7();
    var events = _createSequentialEvents(streamId, count: 5);
    var event1Id = events[0].MessageId.Value;
    var event2Id = events[1].MessageId.Value;
    var event3Id = events[2].MessageId.Value;
    var event4Id = events[3].MessageId.Value;
    var event5Id = events[4].MessageId.Value;

    var perspectiveName = "Test.RewindPerspective";

    // Cursor says: last processed was event 5, but a rewind is required because event 3 arrived late.
    var cursor = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = event5Id,
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = event3Id
    };

    // Work queue has a pending row for event 3 (the triggering event). Events 1,2,4,5 have already
    // been completed — their rows were deleted (see migration 037_CompletePerspectiveEvents.sql).
    var workForEvent3 = new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = event5Id,
      PartitionNumber = 1
    };

    var coordinator = new _cursorAwareCoordinator {
      CursorPerStream = { [(streamId, perspectiveName)] = cursor }
    };
    coordinator.WorkPerCycle.Add([workForEvent3]);

    var runner = new _rewindTrackingRunner { RewindResultEventId = event5Id };
    var eventStore = new _rangeFilteringEventStore();
    eventStore.EventsPerStream[streamId] = [.. events];
    var spy = new _recordingReceptorInvoker();
    var eventTypeProvider = new _fakeEventTypeProvider();

    var worker = _createWorker(
      coordinator,
      new _singleRunnerRegistry(runner, perspectiveName),
      receptorInvoker: spy,
      eventStore: eventStore,
      eventTypeProvider: eventTypeProvider);

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRewindAsync(TimeSpan.FromSeconds(5));
    await coordinator.WaitForCyclesAsync(3, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN: Rewind was triggered with event 3 as the trigger.
    await Assert.That(runner.RewindTriggerEventIds).Contains(event3Id)
      .Because("Rewind path must be invoked with the out-of-order trigger event id.");

    // LOCK-IN: PostPerspectiveInline must fire for event 3 (the out-of-order triggering event).
    var postPerspectiveForEvent3 = spy.Invocations
      .Where(i => i.Stage == LifecycleStage.PostPerspectiveInline && i.EventId == event3Id)
      .ToList();
    await Assert.That(postPerspectiveForEvent3.Count).IsGreaterThanOrEqualTo(1)
      .Because("LOCK-IN: PostPerspectiveInline must fire for the out-of-order triggering event " +
               "(event 3). Today the worker loads events in the (cursor, result.LastEventId] range " +
               "which excludes event 3 — this test proves the gap.");

    // LOCK-IN: PostPerspectiveInline must NOT fire for already-processed events {1,2,4,5}.
    var alreadyProcessed = new HashSet<Guid> { event1Id, event2Id, event4Id, event5Id };
    var doubleFires = spy.Invocations
      .Where(i => i.Stage == LifecycleStage.PostPerspectiveInline && alreadyProcessed.Contains(i.EventId))
      .ToList();
    await Assert.That(doubleFires.Count).IsEqualTo(0)
      .Because("LOCK-IN: Already-processed events ({1,2,4,5}) must NOT have their PostPerspective " +
               "handlers re-fire during rewind. Idempotency per event id.");
  }

  // ==================== CONTRACT: events arriving after rewind fire in Live mode ====================

  [Test]
  public async Task PostRewind_EventsArrivingAfterRewindCycle_FireInLiveModeExactlyOnce_Async() {
    // Scenario: event 3 triggers rewind in cycle 1. In cycle 2, new events 6 and 7 arrive
    // (they weren't present during the rewind). They must process in Live mode (not Replay)
    // and fire handlers exactly once each. Already-processed events (1,2,4,5) and the
    // rewind trigger (3) must not fire again.
    var streamId = Guid.CreateVersion7();
    var events = _createSequentialEvents(streamId, count: 7);
    var event3Id = events[2].MessageId.Value;
    var event5Id = events[4].MessageId.Value;
    var event6Id = events[5].MessageId.Value;
    var event7Id = events[6].MessageId.Value;

    var perspectiveName = "Test.PostRewindPerspective";

    // Pre-rewind cursor: 1,2,4,5 processed, event 3 flagged out-of-order.
    var preRewindCursor = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = event5Id,
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = event3Id
    };

    var workForRewind = new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = event5Id,
      PartitionNumber = 1
    };
    var workForEvents6_7 = new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = event5Id,
      PartitionNumber = 1
    };

    var coordinator = new _cursorAwareCoordinator {
      CursorPerStream = { [(streamId, perspectiveName)] = preRewindCursor }
    };
    coordinator.WorkPerCycle.Add([workForRewind]);
    coordinator.WorkPerCycle.Add([workForEvents6_7]);

    var runner = new _rewindTrackingRunner {
      RewindResultEventId = event5Id,
      NormalRunResultEventId = event7Id
    };
    var eventStore = new _rangeFilteringEventStore();
    eventStore.EventsPerStream[streamId] = [.. events];
    var spy = new _recordingReceptorInvoker();
    var eventTypeProvider = new _fakeEventTypeProvider();

    var worker = _createWorker(
      coordinator,
      new _singleRunnerRegistry(runner, perspectiveName),
      receptorInvoker: spy,
      eventStore: eventStore,
      eventTypeProvider: eventTypeProvider);

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRewindAsync(TimeSpan.FromSeconds(5));
    // Wait enough cycles for cycle 2's work batch to be delivered and processed.
    await coordinator.WaitForCyclesAsync(4, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN: Cycle 2 ran the normal (non-rewind) path.
    await Assert.That(runner.NormalRunCount).IsGreaterThanOrEqualTo(1)
      .Because("After rewind completes the cursor must clear RewindRequired and route to RunAsync.");

    // LOCK-IN: Events 6 and 7 fired PostPerspectiveInline in Live mode, exactly once each.
    var postPerspectiveInvocations = spy.Invocations
      .Where(i => i.Stage == LifecycleStage.PostPerspectiveInline)
      .ToList();
    var event6Invocations = postPerspectiveInvocations.Where(i => i.EventId == event6Id).ToList();
    var event7Invocations = postPerspectiveInvocations.Where(i => i.EventId == event7Id).ToList();

    await Assert.That(event6Invocations.Count).IsEqualTo(1)
      .Because("LOCK-IN: Event 6 arriving after rewind must fire PostPerspectiveInline exactly once.");
    await Assert.That(event7Invocations.Count).IsEqualTo(1)
      .Because("LOCK-IN: Event 7 arriving after rewind must fire PostPerspectiveInline exactly once.");
    await Assert.That(event6Invocations[0].ProcessingMode).IsNotEqualTo(ProcessingMode.Replay)
      .Because("LOCK-IN: New events post-rewind must be invoked in Live mode, not Replay.");
    await Assert.That(event7Invocations[0].ProcessingMode).IsNotEqualTo(ProcessingMode.Replay)
      .Because("LOCK-IN: New events post-rewind must be invoked in Live mode, not Replay.");

    // LOCK-IN: Already-processed events (1,2,4,5) must never fire PostPerspective.
    var alreadyProcessedIds = new HashSet<Guid> {
      events[0].MessageId.Value, events[1].MessageId.Value,
      events[3].MessageId.Value, event5Id
    };
    var wrongFires = postPerspectiveInvocations
      .Where(i => alreadyProcessedIds.Contains(i.EventId))
      .ToList();
    await Assert.That(wrongFires.Count).IsEqualTo(0)
      .Because("LOCK-IN: Already-processed events must not re-fire after rewind + new events.");

    // LOCK-IN: The rewind trigger event (3) fires exactly once (in cycle 1).
    var event3Invocations = postPerspectiveInvocations.Where(i => i.EventId == event3Id).ToList();
    await Assert.That(event3Invocations.Count).IsEqualTo(1)
      .Because("LOCK-IN: Rewind trigger event must fire exactly once on the rewind cycle.");
  }

  // ==================== CONTRACT: 30-event burst with 8 out-of-order arrivals — per-event idempotency ====================

  [Test]
  public async Task Burst_30EventsWith8OutOfOrderArrivals_EachHandlerFiresExactlyOnce_Async() {
    // Large-scale lock-in: across 30 events delivered across many cycles, with 8 of them
    // arriving out-of-order (each triggering its own rewind), every event's PostPerspective
    // handler must fire exactly once. No over-fires, no misses.
    //
    // Delivery script — event indices in logical order; parenthesised entries arrive LATE
    // (below the current cursor when they appear):
    //   0,1,2, 4,5,(3), 6,7,8, 10,11,(9), 12,13, 15,(14), 16,17, 19,20,(18), 21, 24,(22),
    //   (23), 25, 27,(26), 28,29
    var streamId = Guid.CreateVersion7();
    const int total = 30;
    var events = _createSequentialEvents(streamId, total);
    var eventIds = events.Select(e => e.MessageId.Value).ToArray();

    // Delivery order by index (late arrivals marked with logical position but delivered here):
    int[] deliveryOrder = [
      0, 1, 2,
      4, 5, 3,
      6, 7, 8,
      10, 11, 9,
      12, 13,
      15, 14,
      16, 17,
      19, 20, 18,
      21,
      24, 22, 23,
      25,
      27, 26,
      28, 29
    ];
    var lateIndices = new HashSet<int> { 3, 9, 14, 18, 22, 23, 26 };
    const string perspectiveName = "Test.BurstPerspective";

    var coordinator = new _arrivalScriptCoordinator(perspectiveName, streamId, eventIds, deliveryOrder, lateIndices);
    var runner = new _rewindTrackingRunner {
      HighestProcessedIdProvider = () => coordinator.HighestArrivedNonLateId
    };
    var eventStore = new _arrivalAwareEventStore(events, coordinator);
    var spy = new _recordingReceptorInvoker();
    var eventTypeProvider = new _fakeEventTypeProvider();

    var worker = _createWorker(
      coordinator,
      new _singleRunnerRegistry(runner, perspectiveName),
      receptorInvoker: spy,
      eventStore: eventStore,
      eventTypeProvider: eventTypeProvider);

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForAllArrivalsProcessedAsync(TimeSpan.FromSeconds(20));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN: Each of the 30 events fired PostPerspectiveInline exactly once.
    var postPerspective = spy.Invocations
      .Where(i => i.Stage == LifecycleStage.PostPerspectiveInline)
      .ToList();
    for (var i = 0; i < total; i++) {
      var id = eventIds[i];
      var fires = postPerspective.Count(inv => inv.EventId == id);
      await Assert.That(fires).IsEqualTo(1)
        .Because($"LOCK-IN: Event index {i} must fire PostPerspectiveInline exactly once across the whole burst (late={lateIndices.Contains(i)}).");
    }

    // LOCK-IN: Every rewind corresponded to a late arrival. No spurious rewinds.
    await Assert.That(runner.RewindTriggerEventIds.Count).IsEqualTo(lateIndices.Count)
      .Because($"LOCK-IN: Expected exactly {lateIndices.Count} rewinds (one per late arrival).");

    // LOCK-IN: Each late arrival's id was the trigger of exactly one rewind.
    foreach (var lateIdx in lateIndices) {
      var triggerMatches = runner.RewindTriggerEventIds.Count(t => t == eventIds[lateIdx]);
      await Assert.That(triggerMatches).IsEqualTo(1)
        .Because($"LOCK-IN: Late event index {lateIdx} must have been the trigger of exactly one rewind.");
    }
  }

  // ==================== CONTRACT: multiple concurrent late arrivals all fire handlers ====================

  [Test]
  public async Task ConcurrentLateArrivals_AllLateEventsFirePostPerspective_Async() {
    // Events 1,2,4,5 already processed. Then events 3, 2, 1 all arrive within the same
    // rewind window (before the worker picks up the rewind and completes it). The
    // coordinator updates the RewindTriggerEventId to the minimum each time, ending with
    // trigger=event1. When the rewind completes, handlers must fire for events 3, 2, AND 1
    // — not just the final trigger event.
    var streamId = Guid.CreateVersion7();
    var events = _createSequentialEvents(streamId, count: 5);
    var eventIds = events.Select(e => e.MessageId.Value).ToArray();
    const string perspectiveName = "Test.ConcurrentLatePerspective";

    // Pre-rewind cursor: events 1,2,4,5 already processed (indices 0,1,3,4 — i.e., all
    // in-order events). We're using a scaffold where indices 0,1 are events "1,2",
    // index 3 is "4", index 4 is "5", and index 2 is "3" (the late one — plus we'll
    // inject two more "before" it to simulate multiple concurrent late arrivals).
    var cursorBefore = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = eventIds[4],
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = eventIds[0] // the lowest of three late arrivals (events[0], events[1], events[2])
    };

    // Three late events pending: events[0], events[1], events[2]. All have id < cursor.
    // Coordinator reports them as pending work (one PerspectiveWork row each).
    var workItems = new List<PerspectiveWork> {
      _lateWork(streamId, perspectiveName, eventIds[4]),
      _lateWork(streamId, perspectiveName, eventIds[4]),
      _lateWork(streamId, perspectiveName, eventIds[4])
    };

    var coordinator = new _cursorAwareCoordinator {
      CursorPerStream = { [(streamId, perspectiveName)] = cursorBefore }
    };
    coordinator.WorkPerCycle.Add(workItems);

    var runner = new _rewindTrackingRunner { RewindResultEventId = eventIds[4] };
    var eventStore = new _rangeFilteringEventStore();
    eventStore.EventsPerStream[streamId] = [.. events];
    var spy = new _recordingReceptorInvoker();
    var eventTypeProvider = new _fakeEventTypeProvider();

    // Reader annotates events 0, 1, 2 (the three late arrivals) as is_new=true.
    // Events 3, 4 (indices for events "4" and "5") were already processed.
    var replayReader = new _scriptedReplayReader(
      events,
      isNew: new HashSet<Guid> { eventIds[0], eventIds[1], eventIds[2] });

    var worker = _createWorker(
      coordinator,
      new _singleRunnerRegistry(runner, perspectiveName),
      receptorInvoker: spy,
      eventStore: eventStore,
      eventTypeProvider: eventTypeProvider,
      replayReader: replayReader);

    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRewindAsync(TimeSpan.FromSeconds(5));
    await coordinator.WaitForCyclesAsync(3, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // LOCK-IN: Each of the three late events fires PostPerspectiveInline exactly once.
    var postPerspective = spy.Invocations
      .Where(i => i.Stage == LifecycleStage.PostPerspectiveInline)
      .ToList();
    for (var i = 0; i < 3; i++) {
      var id = eventIds[i];
      var fires = postPerspective.Count(inv => inv.EventId == id);
      await Assert.That(fires).IsEqualTo(1)
        .Because($"LOCK-IN: Late event {i} ({id}) must fire PostPerspectiveInline exactly once when three late arrivals accumulate in a single rewind window. Narrow trigger-event-only fix does not handle this.");
    }

    // LOCK-IN: Already-processed events (indices 3, 4 = events 4, 5) must not re-fire.
    var alreadyProcessedIds = new HashSet<Guid> { eventIds[3], eventIds[4] };
    var wrongFires = postPerspective
      .Where(i => alreadyProcessedIds.Contains(i.EventId))
      .ToList();
    await Assert.That(wrongFires.Count).IsEqualTo(0)
      .Because("LOCK-IN: Already-processed events must not fire during rewind with multiple late arrivals.");
  }

  private static PerspectiveWork _lateWork(Guid streamId, string perspectiveName, Guid cursorLastEventId) => new() {
    WorkId = Guid.CreateVersion7(),
    StreamId = streamId,
    PerspectiveName = perspectiveName,
    LastProcessedEventId = cursorLastEventId,
    PartitionNumber = 1
  };

  // ==================== CONTRACT: rewind from a snapshot skips pre-snapshot handler firing ====================

  [Test]
  public async Task RewindFromSnapshot_Over20Events_OnlyIsNewFirePostPerspective_Async() {
    // 25-event stream. Snapshot taken at event index 10 — events 0..10 persisted into the
    // model via snapshot, no replay needed. Events 11..20 have been processed normally.
    // Events 21..24 pending; plus a late event (index 15) arrives after events 21-23 landed.
    // The reader returns events from the snapshot forward, annotated. Only is_new events
    // fire PostPerspective*; pre-snapshot and already-fired events do not.
    var streamId = Guid.CreateVersion7();
    const int total = 25;
    var events = _createSequentialEvents(streamId, total);
    var eventIds = events.Select(e => e.MessageId.Value).ToArray();
    const string perspectiveName = "Test.SnapshotRewindPerspective";

    // Late event: index 15 (middle of already-processed range). Cursor was at event 23.
    var cursorBefore = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = eventIds[23],
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = eventIds[15]
    };

    // is_new set = events never before fired handlers: the late event + any not-yet-processed
    // events above cursor (here: event 24 pending).
    var isNewSet = new HashSet<Guid> { eventIds[15], eventIds[24] };
    var reader = new _scriptedReplayReader(events, isNewSet);

    var coordinator = new _cursorAwareCoordinator {
      CursorPerStream = { [(streamId, perspectiveName)] = cursorBefore }
    };
    coordinator.WorkPerCycle.Add([_lateWork(streamId, perspectiveName, eventIds[23])]);

    var runner = new _rewindTrackingRunner { RewindResultEventId = eventIds[24] };
    var spy = new _recordingReceptorInvoker();
    var worker = _createWorker(
      coordinator,
      new _singleRunnerRegistry(runner, perspectiveName),
      receptorInvoker: spy,
      eventStore: new _rangeFilteringEventStore { EventsPerStream = { [streamId] = [.. events] } },
      eventTypeProvider: new _fakeEventTypeProvider(),
      replayReader: reader);

    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRewindAsync(TimeSpan.FromSeconds(5));
    await coordinator.WaitForCyclesAsync(3, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    var postPerspective = spy.Invocations
      .Where(i => i.Stage == LifecycleStage.PostPerspectiveInline)
      .ToList();

    // LOCK-IN: Exactly the is_new set fires PostPerspectiveInline.
    foreach (var id in isNewSet) {
      var fires = postPerspective.Count(i => i.EventId == id);
      await Assert.That(fires).IsEqualTo(1)
        .Because($"LOCK-IN: is_new event {id} must fire PostPerspectiveInline exactly once after snapshot-based rewind.");
    }

    // LOCK-IN: No event outside is_new fires (events 0..14 pre-snapshot/pre-rewind, 16..23 already-processed).
    var extraneous = postPerspective
      .Where(i => !isNewSet.Contains(i.EventId))
      .ToList();
    await Assert.That(extraneous.Count).IsEqualTo(0)
      .Because("LOCK-IN: No pre-snapshot or already-processed events fire handlers on a snapshot-based rewind.");
  }

  // ==================== CONTRACT: rewind from zero (no snapshot) still only fires is_new ====================

  [Test]
  public async Task RewindFromZero_NoSnapshot_OnlyIsNewFirePostPerspective_Async() {
    // 15-event stream, no snapshot. Cursor at event 12. Late event at index 4 triggers rewind.
    // Reader annotates: events 4 (late), 13, 14 (pending) as is_new. Everything else is a
    // full in-memory replay for model reconstruction only — handlers must not fire.
    var streamId = Guid.CreateVersion7();
    const int total = 15;
    var events = _createSequentialEvents(streamId, total);
    var eventIds = events.Select(e => e.MessageId.Value).ToArray();
    const string perspectiveName = "Test.RewindFromZeroPerspective";

    var cursorBefore = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = eventIds[12],
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = eventIds[4]
    };
    var isNewSet = new HashSet<Guid> { eventIds[4], eventIds[13], eventIds[14] };

    var coordinator = new _cursorAwareCoordinator {
      CursorPerStream = { [(streamId, perspectiveName)] = cursorBefore }
    };
    coordinator.WorkPerCycle.Add([_lateWork(streamId, perspectiveName, eventIds[12])]);

    var runner = new _rewindTrackingRunner { RewindResultEventId = eventIds[14] };
    var spy = new _recordingReceptorInvoker();
    var worker = _createWorker(
      coordinator,
      new _singleRunnerRegistry(runner, perspectiveName),
      receptorInvoker: spy,
      eventStore: new _rangeFilteringEventStore { EventsPerStream = { [streamId] = [.. events] } },
      eventTypeProvider: new _fakeEventTypeProvider(),
      replayReader: new _scriptedReplayReader(events, isNewSet));

    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRewindAsync(TimeSpan.FromSeconds(5));
    await coordinator.WaitForCyclesAsync(3, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    var postPerspective = spy.Invocations
      .Where(i => i.Stage == LifecycleStage.PostPerspectiveInline)
      .ToList();
    foreach (var id in isNewSet) {
      await Assert.That(postPerspective.Count(i => i.EventId == id)).IsEqualTo(1)
        .Because($"LOCK-IN: is_new event {id} must fire exactly once on a from-zero rewind.");
    }
    var extraneous = postPerspective.Where(i => !isNewSet.Contains(i.EventId)).ToList();
    await Assert.That(extraneous.Count).IsEqualTo(0)
      .Because("LOCK-IN: From-zero rewind must not fire handlers for the 12 already-processed events.");
  }

  // ==================== CONTRACT: chained rewinds — 3 rewinds back-to-back ====================

  [Test]
  public async Task ChainedRewinds_ThreeRewindsInSuccession_NoHandlerReFires_Async() {
    // Cycle 1: late event A triggers rewind. Cycle 2: late event B triggers a fresh rewind.
    // Cycle 3: late event C triggers another fresh rewind. Assert: each late event fires
    // handlers exactly once, no handler re-fires for any event across the three rewinds.
    var streamId = Guid.CreateVersion7();
    var events = _createSequentialEvents(streamId, count: 10);
    var eventIds = events.Select(e => e.MessageId.Value).ToArray();
    const string perspectiveName = "Test.ChainedRewindsPerspective";

    // Cursor starts at event 9 (all ahead already processed). Three late arrivals: 1, 3, 5.
    var cursor = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = eventIds[9],
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = eventIds[1]
    };

    var coordinator = new _sequentialRewindCoordinator(
      streamId, perspectiveName, eventIds, lateIndices: [1, 3, 5], baseCursor: cursor);

    var runner = new _rewindTrackingRunner {
      HighestProcessedIdProvider = () => eventIds[9]
    };
    var spy = new _recordingReceptorInvoker();
    // Each cycle's rewind sees a single is_new event (the trigger for that cycle).
    var reader = new _cyclicReplayReader(events, coordinator);

    var worker = _createWorker(
      coordinator,
      new _singleRunnerRegistry(runner, perspectiveName),
      receptorInvoker: spy,
      eventStore: new _rangeFilteringEventStore { EventsPerStream = { [streamId] = [.. events] } },
      eventTypeProvider: new _fakeEventTypeProvider(),
      replayReader: reader);

    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForAllRewindsAsync(TimeSpan.FromSeconds(15));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    var postPerspective = spy.Invocations
      .Where(i => i.Stage == LifecycleStage.PostPerspectiveInline)
      .ToList();
    foreach (var lateIdx in new[] { 1, 3, 5 }) {
      await Assert.That(postPerspective.Count(i => i.EventId == eventIds[lateIdx])).IsEqualTo(1)
        .Because($"LOCK-IN: Late event index {lateIdx} must fire PostPerspectiveInline exactly once across three chained rewinds.");
    }

    // LOCK-IN: Every other event (0, 2, 4, 6..9) must NOT fire — they were already processed.
    var alreadyProcessedIdx = new[] { 0, 2, 4, 6, 7, 8, 9 };
    foreach (var idx in alreadyProcessedIdx) {
      await Assert.That(postPerspective.Count(i => i.EventId == eventIds[idx])).IsEqualTo(0)
        .Because($"LOCK-IN: Already-processed event index {idx} must NOT fire during any of the three chained rewinds.");
    }

    await Assert.That(runner.RewindTriggerEventIds.Count).IsEqualTo(3)
      .Because("LOCK-IN: Exactly three rewinds must fire, one per late arrival.");
  }

  // ==================== CONTRACT: stage-level idempotency per event ====================

  [Test]
  public async Task Rewind_StageLevelIdempotency_EachStageFiresOncePerEvent_Async() {
    // For the rewind trigger event, every PostPerspective* stage fires exactly once.
    // (Detached, Inline, AllPerspectivesDetached, AllPerspectivesInline, LifecycleDetached,
    // LifecycleInline — whatever the worker emits for this event.)
    var streamId = Guid.CreateVersion7();
    var events = _createSequentialEvents(streamId, count: 5);
    var eventIds = events.Select(e => e.MessageId.Value).ToArray();
    const string perspectiveName = "Test.StageIdempotencyPerspective";

    var cursor = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = eventIds[4],
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = eventIds[2]
    };

    var coordinator = new _cursorAwareCoordinator {
      CursorPerStream = { [(streamId, perspectiveName)] = cursor }
    };
    coordinator.WorkPerCycle.Add([_lateWork(streamId, perspectiveName, eventIds[4])]);

    var runner = new _rewindTrackingRunner { RewindResultEventId = eventIds[4] };
    var spy = new _recordingReceptorInvoker();
    var worker = _createWorker(
      coordinator,
      new _singleRunnerRegistry(runner, perspectiveName),
      receptorInvoker: spy,
      eventStore: new _rangeFilteringEventStore { EventsPerStream = { [streamId] = [.. events] } },
      eventTypeProvider: new _fakeEventTypeProvider(),
      replayReader: new _scriptedReplayReader(events, new HashSet<Guid> { eventIds[2] }));

    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRewindAsync(TimeSpan.FromSeconds(5));
    await coordinator.WaitForCyclesAsync(3, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // The once-per-event stages are the "Inline" variants of each logical phase.
    // ImmediateDetached / other Detached variants are fire-and-forget auxiliary hooks
    // that may fire multiple times as a consequence of how stages chain through the
    // worker pipeline — they are not under the once-per-event contract here.
    var onceStages = new[] {
      LifecycleStage.PrePerspectiveInline,
      LifecycleStage.PostPerspectiveInline,
      LifecycleStage.PostAllPerspectivesInline,
      LifecycleStage.PostLifecycleInline,
    };
    var triggerInvocations = spy.Invocations.Where(i => i.EventId == eventIds[2]).ToList();
    foreach (var stage in onceStages) {
      var count = triggerInvocations.Count(i => i.Stage == stage);
      await Assert.That(count).IsLessThanOrEqualTo(1)
        .Because($"LOCK-IN: Stage {stage} must fire at most once for a given event, even on the rewind path.");
    }

    // Must have fired at least one PostPerspective* stage for the trigger event.
    await Assert.That(triggerInvocations.Any(i => i.Stage == LifecycleStage.PostPerspectiveInline))
      .IsTrue()
      .Because("LOCK-IN: PostPerspectiveInline must fire for the rewind trigger event.");
  }

  // ==================== CONTRACT: PostAllPerspectives / PostLifecycle fire once per is_new event ====================

  [Test]
  public async Task Rewind_PostAllPerspectivesAndLifecycle_FireOncePerIsNewEvent_Async() {
    // PostAllPerspectives + PostLifecycle are gated by the LifecycleCoordinator's WhenAll.
    // This test asserts: on the rewind path, they fire for is_new events only, exactly once,
    // and never for already-processed events.
    var streamId = Guid.CreateVersion7();
    var events = _createSequentialEvents(streamId, count: 8);
    var eventIds = events.Select(e => e.MessageId.Value).ToArray();
    const string perspectiveName = "Test.PostLifecycleRewindPerspective";

    var cursor = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = eventIds[5],
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = eventIds[2]
    };
    var isNewSet = new HashSet<Guid> { eventIds[2], eventIds[6], eventIds[7] };

    var coordinator = new _cursorAwareCoordinator {
      CursorPerStream = { [(streamId, perspectiveName)] = cursor }
    };
    coordinator.WorkPerCycle.Add([_lateWork(streamId, perspectiveName, eventIds[5])]);

    var runner = new _rewindTrackingRunner { RewindResultEventId = eventIds[7] };
    var spy = new _recordingReceptorInvoker();
    var worker = _createWorker(
      coordinator,
      new _singleRunnerRegistry(runner, perspectiveName),
      receptorInvoker: spy,
      eventStore: new _rangeFilteringEventStore { EventsPerStream = { [streamId] = [.. events] } },
      eventTypeProvider: new _fakeEventTypeProvider(),
      replayReader: new _scriptedReplayReader(events, isNewSet));

    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRewindAsync(TimeSpan.FromSeconds(5));
    await coordinator.WaitForCyclesAsync(3, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Every stage that appears in the log for an already-processed event is a violation.
    var processedIds = new HashSet<Guid> {
      eventIds[0], eventIds[1], eventIds[3], eventIds[4], eventIds[5]
    };
    var violations = spy.Invocations.Where(i => processedIds.Contains(i.EventId)).ToList();
    await Assert.That(violations.Count).IsEqualTo(0)
      .Because("LOCK-IN: PostAllPerspectives / PostLifecycle must not fire for already-processed events during rewind.");

    // PostPerspectiveInline fires for is_new events.
    foreach (var id in isNewSet) {
      var fires = spy.Invocations.Count(i => i.EventId == id && i.Stage == LifecycleStage.PostPerspectiveInline);
      await Assert.That(fires).IsEqualTo(1)
        .Because($"LOCK-IN: PostPerspectiveInline must fire exactly once for is_new event {id}.");
    }
  }

  // ==================== Helpers ====================

  private static List<MessageEnvelope<IEvent>> _createSequentialEvents(Guid streamId, int count) {
    // TrackedGuid.NewMedo() wraps Medo.Uuid7 which has sub-millisecond precision and
    // guaranteed monotonicity within a tight loop — preferred throughout Whizbang over
    // Guid.CreateVersion7() (ms precision only).
    var list = new List<MessageEnvelope<IEvent>>(count);
    for (var i = 0; i < count; i++) {
      list.Add(new MessageEnvelope<IEvent> {
        MessageId = MessageId.From(TrackedGuid.NewMedo().Value),
        Payload = new _fakeEvent(i + 1),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      });
    }
    return list;
  }

  private static PerspectiveWorker _createWorker(
    IWorkCoordinator coordinator,
    IPerspectiveRunnerRegistry registry,
    IReceptorInvoker? receptorInvoker = null,
    IEventStore? eventStore = null,
    IEventTypeProvider? eventTypeProvider = null,
    IPerspectiveReplayReader? replayReader = null) {

    var instanceProvider = new _fakeInstanceProvider();
    var databaseReadiness = new _fakeDatabaseReadiness();
    // Use Instant strategy so completion flows through to the coordinator immediately,
    // making cursor-state transitions deterministic between cycles.
    IPerspectiveCompletionStrategy strategy = new InstantCompletionStrategy();

    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();

    if (receptorInvoker is not null) {
      services.AddSingleton(receptorInvoker);
    }
    if (eventStore is not null) {
      services.AddSingleton(eventStore);
    }
    if (replayReader is not null) {
      services.AddSingleton(replayReader);
    }

    var serviceProvider = services.BuildServiceProvider();

    return new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      strategy,
      databaseReadiness,
      eventTypeProvider: eventTypeProvider);
  }

  // ==================== Test Fakes ====================

  private sealed record _fakeEvent(int Sequence) : IEvent;

  /// <summary>
  /// Coordinator that serves a configured cursor per (stream, perspective) and a configurable
  /// batch of work per cycle. After cycle 1 completes successfully the RewindRequired flag is
  /// cleared on the cursor to simulate the real completion flow.
  /// </summary>
  private sealed class _cursorAwareCoordinator : IWorkCoordinator {
    private int _cycleCount;
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _cycleWaiters = new();

    public int CycleCount => _cycleCount;
    public Dictionary<(Guid StreamId, string PerspectiveName), PerspectiveCursorInfo> CursorPerStream { get; } = new();
    /// <summary>One list per cycle, in order. Cycles beyond this return empty work.</summary>
    public List<List<PerspectiveWork>> WorkPerCycle { get; } = [];

    public Task WaitForCyclesAsync(int count, TimeSpan timeout) {
      var waiter = _cycleWaiters.GetOrAdd(count, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      return waiter.Task.WaitAsync(timeout);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      var current = Interlocked.Increment(ref _cycleCount);
      foreach (var kvp in _cycleWaiters) {
        if (current >= kvp.Key) {
          kvp.Value.TrySetResult();
        }
      }
      var idx = current - 1;
      var work = idx < WorkPerCycle.Count ? [.. WorkPerCycle[idx]] : new List<PerspectiveWork>();
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = work });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      // Mirror the real coordinator: on any successful completion (Completed flag set), clear
      // RewindRequired and advance LastEventId so the next cycle routes through the normal path.
      var key = (completion.StreamId, completion.PerspectiveName);
      if (CursorPerStream.TryGetValue(key, out var existing)
          && completion.Status.HasFlag(PerspectiveProcessingStatus.Completed)) {
        var advancedEventId = completion.LastEventId != Guid.Empty
          ? completion.LastEventId
          : existing.LastEventId;
        CursorPerStream[key] = existing with {
          LastEventId = advancedEventId,
          Status = PerspectiveProcessingStatus.None,
          RewindTriggerEventId = null
        };
      }
      return Task.CompletedTask;
    }
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult(CursorPerStream.TryGetValue((streamId, perspectiveName), out var c) ? c : null);
  }

  /// <summary>
  /// Runner that records every RewindAndRunAsync and RunAsync call so tests can assert
  /// which code path exercised each cycle and what was processed.
  /// </summary>
  private sealed class _rewindTrackingRunner : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object);
    private readonly ConcurrentBag<Guid> _rewindTriggers = [];
    private readonly TaskCompletionSource _firstRewind = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstNormalRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _normalRunCount;

    public Guid RewindResultEventId { get; set; }
    /// <summary>
    /// When set and non-empty, RunAsync returns Status=Completed with this as LastEventId.
    /// Otherwise it returns Status=None (no events to process on this cycle).
    /// </summary>
    public Guid NormalRunResultEventId { get; set; }
    /// <summary>
    /// When set, both RunAsync and RewindAndRunAsync use the returned value as LastEventId.
    /// Takes precedence over NormalRunResultEventId/RewindResultEventId and simulates a
    /// real runner that processes up to the highest known event. Returns null/empty when
    /// nothing new is available, yielding Status=None on the normal path.
    /// </summary>
    public Func<Guid?>? HighestProcessedIdProvider { get; set; }
    public IReadOnlyCollection<Guid> RewindTriggerEventIds => [.. _rewindTriggers];
    public int NormalRunCount => _normalRunCount;

    public Task WaitForRewindAsync(TimeSpan timeout) => _firstRewind.Task.WaitAsync(timeout);
    public Task WaitForNormalRunAsync(TimeSpan timeout) => _firstNormalRun.Task.WaitAsync(timeout);

    public Task<PerspectiveCursorCompletion> RunAsync(
      Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _normalRunCount);
      if (HighestProcessedIdProvider is not null) {
        var latest = HighestProcessedIdProvider();
        if (latest is { } id
            && (lastProcessedEventId is null || _uuidV7Comparer.Compare(id, lastProcessedEventId.Value) > 0)) {
          _firstNormalRun.TrySetResult();
          return Task.FromResult(new PerspectiveCursorCompletion {
            StreamId = streamId,
            PerspectiveName = perspectiveName,
            LastEventId = id,
            Status = PerspectiveProcessingStatus.Completed,
            EventsProcessed = 1
          });
        }
        return Task.FromResult(new PerspectiveCursorCompletion {
          StreamId = streamId,
          PerspectiveName = perspectiveName,
          LastEventId = lastProcessedEventId ?? Guid.Empty,
          Status = PerspectiveProcessingStatus.None
        });
      }
      if (NormalRunResultEventId != Guid.Empty) {
        _firstNormalRun.TrySetResult();
        return Task.FromResult(new PerspectiveCursorCompletion {
          StreamId = streamId,
          PerspectiveName = perspectiveName,
          LastEventId = NormalRunResultEventId,
          Status = PerspectiveProcessingStatus.Completed,
          EventsProcessed = 1
        });
      }
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = lastProcessedEventId ?? Guid.Empty,
        Status = PerspectiveProcessingStatus.None
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
      Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) {
      _rewindTriggers.Add(triggeringEventId);
      _firstRewind.TrySetResult();
      var resultEventId = HighestProcessedIdProvider?.Invoke() ?? RewindResultEventId;
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = resultEventId,
        Status = PerspectiveProcessingStatus.Completed,
        EventsProcessed = 5
      });
    }

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  /// <summary>
  /// Records every receptor invocation by (event id, stage) so assertions can verify
  /// handler firing per event.
  /// </summary>
  private sealed class _recordingReceptorInvoker : IReceptorInvoker {
    private readonly ConcurrentBag<InvocationRecord> _invocations = [];

    public IReadOnlyCollection<InvocationRecord> Invocations => [.. _invocations];

    public ValueTask InvokeAsync(
      IMessageEnvelope envelope,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default) {
      _invocations.Add(new InvocationRecord(
        envelope.MessageId.Value,
        stage,
        context?.ProcessingMode));
      return ValueTask.CompletedTask;
    }

    public sealed record InvocationRecord(Guid EventId, LifecycleStage Stage, ProcessingMode? ProcessingMode);
  }

  /// <summary>
  /// Fake event store that returns only events strictly between afterEventId (exclusive)
  /// and upToEventId (inclusive), ordered by Guid (UUIDv7 time-ordered).
  /// This mirrors the real GetEventsBetweenPolymorphicAsync semantics needed to exercise
  /// the worker's rewind-path range logic.
  /// </summary>
  private sealed class _rangeFilteringEventStore : IEventStore {
    public ConcurrentDictionary<Guid, List<MessageEnvelope<IEvent>>> EventsPerStream { get; } = new();

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId, Guid? afterEventId, Guid upToEventId,
      IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      if (!EventsPerStream.TryGetValue(streamId, out var list)) {
        return Task.FromResult(new List<MessageEnvelope<IEvent>>());
      }
      var filtered = list
        .Where(e => (afterEventId is null || _uuidV7Comparer.Compare(e.MessageId.Value, afterEventId.Value) > 0)
                 && (upToEventId == Guid.Empty || _uuidV7Comparer.Compare(e.MessageId.Value, upToEventId) <= 0))
        .OrderBy(e => e.MessageId.Value, _uuidV7Comparer)
        .ToList();
      return Task.FromResult(filtered);
    }


    // Drain-mode deserialization — not exercised by these tests; return empty.
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(
      IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) => _empty<IEvent>(cancellationToken);
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);

    private static async IAsyncEnumerable<MessageEnvelope<T>> _empty<T>([EnumeratorCancellation] CancellationToken ct = default) {
      await Task.CompletedTask;
      yield break;
    }
  }

  private sealed class _fakeEventTypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(_fakeEvent)];
  }

  private sealed class _singleRunnerRegistry(IPerspectiveRunner runner, string perspectiveName) : IPerspectiveRunnerRegistry {
    public Type PerspectiveType => typeof(object);
    public IPerspectiveRunner? GetRunner(string name, IServiceProvider serviceProvider) =>
      name == perspectiveName ? runner : null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(perspectiveName, $"global::{perspectiveName}", "global::Test.Model", ["global::Test.Event"])];
    public IReadOnlyList<Type> GetEventTypes() => [typeof(_fakeEvent)];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  /// <summary>
  /// Coordinator that drives a scripted arrival sequence for one (stream, perspective) pair.
  /// Each poll advances one step in the delivery order. Out-of-order detection is simulated
  /// by comparing the arriving event id to the current cursor's LastEventId — if it is below,
  /// RewindRequired is flipped with that event as the trigger.
  /// Completion report advances the cursor to the reported LastEventId and clears the flag.
  /// </summary>
  private sealed class _arrivalScriptCoordinator : IWorkCoordinator {
    private readonly string _perspectiveName;
    private readonly Guid _streamId;
    private readonly Guid[] _eventIds;
    private readonly int[] _deliveryOrder;
    private readonly HashSet<int> _lateIndices;
    private int _nextArrivalIdx;
    private readonly TaskCompletionSource _allProcessed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PerspectiveCursorInfo _cursor;
    private readonly IComparer<Guid> _cmp = _uuidV7Comparer;
    private readonly HashSet<Guid> _arrived = [];

    public IReadOnlySet<Guid> Arrived => _arrived;

    /// <summary>
    /// Highest arrived event id (by UUIDv7 time order) that is NOT itself a late arrival —
    /// i.e., the cursor head that the runner should return from RunAsync / RewindAndRunAsync.
    /// Returns null until at least one non-late event has arrived.
    /// </summary>
    public Guid? HighestArrivedNonLateId {
      get {
        Guid? best = null;
        for (var i = 0; i < _nextArrivalIdx; i++) {
          var idx = _deliveryOrder[i];
          if (_lateIndices.Contains(idx)) {
            continue;
          }
          var id = _eventIds[idx];
          if (best is null || _uuidV7Comparer.Compare(id, best.Value) > 0) {
            best = id;
          }
        }
        return best;
      }
    }

    public _arrivalScriptCoordinator(
      string perspectiveName,
      Guid streamId,
      Guid[] eventIds,
      int[] deliveryOrder,
      HashSet<int> lateIndices) {
      _perspectiveName = perspectiveName;
      _streamId = streamId;
      _eventIds = eventIds;
      _deliveryOrder = deliveryOrder;
      _lateIndices = lateIndices;
      _cursor = new PerspectiveCursorInfo {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = null,
        Status = PerspectiveProcessingStatus.None
      };
    }

    public Task WaitForAllArrivalsProcessedAsync(TimeSpan timeout) =>
      _allProcessed.Task.WaitAsync(timeout);

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      // If there's a pending rewind, keep returning the triggering work item until the
      // worker reports completion. Otherwise deliver the next scripted arrival.
      if (_cursor.Status.HasFlag(PerspectiveProcessingStatus.RewindRequired)) {
        return Task.FromResult(new WorkBatch {
          OutboxWork = [],
          InboxWork = [],
          PerspectiveWork = [_makeWork()]
        });
      }

      if (_nextArrivalIdx >= _deliveryOrder.Length) {
        if (_arrived.Count == _eventIds.Length) {
          _allProcessed.TrySetResult();
        }
        return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
      }

      var idx = _deliveryOrder[_nextArrivalIdx++];
      var arrivingId = _eventIds[idx];
      _arrived.Add(arrivingId);

      // Out-of-order detection: incoming id < cursor.LastEventId → rewind required.
      if (_cursor.LastEventId is { } last && _cmp.Compare(arrivingId, last) < 0) {
        _cursor = _cursor with {
          Status = PerspectiveProcessingStatus.RewindRequired,
          RewindTriggerEventId = arrivingId
        };
      }

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [_makeWork()]
      });
    }

    private PerspectiveWork _makeWork() => new() {
      WorkId = Guid.CreateVersion7(),
      StreamId = _streamId,
      PerspectiveName = _perspectiveName,
      LastProcessedEventId = _cursor.LastEventId,
      PartitionNumber = 1
    };

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      if (completion.Status.HasFlag(PerspectiveProcessingStatus.Completed)) {
        var advancedId = completion.LastEventId != Guid.Empty
          ? completion.LastEventId
          : _cursor.LastEventId;
        _cursor = _cursor with {
          LastEventId = advancedId,
          Status = PerspectiveProcessingStatus.None,
          RewindTriggerEventId = null
        };
        if (_nextArrivalIdx >= _deliveryOrder.Length && _arrived.Count == _eventIds.Length) {
          _allProcessed.TrySetResult();
        }
      }
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(streamId == _streamId && perspectiveName == _perspectiveName ? _cursor : null);
  }

  /// <summary>
  /// Event store fake that only returns envelopes for event ids that have already arrived
  /// via the coordinator — mirrors real-world behavior where the store does not contain
  /// events that have not yet been appended.
  /// </summary>
  private sealed class _arrivalAwareEventStore : IEventStore {
    private readonly List<MessageEnvelope<IEvent>> _allEvents;
    private readonly _arrivalScriptCoordinator _coordinator;

    public _arrivalAwareEventStore(List<MessageEnvelope<IEvent>> allEvents, _arrivalScriptCoordinator coordinator) {
      _allEvents = allEvents;
      _coordinator = coordinator;
    }

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId, Guid? afterEventId, Guid upToEventId,
      IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      var arrived = _coordinator.Arrived;
      var filtered = _allEvents
        .Where(e => arrived.Contains(e.MessageId.Value))
        .Where(e => (afterEventId is null || _uuidV7Comparer.Compare(e.MessageId.Value, afterEventId.Value) > 0)
                 && (upToEventId == Guid.Empty || _uuidV7Comparer.Compare(e.MessageId.Value, upToEventId) <= 0))
        .OrderBy(e => e.MessageId.Value, _uuidV7Comparer)
        .ToList();
      return Task.FromResult(filtered);
    }

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(
      IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) => _empty<IEvent>(cancellationToken);
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);

    private static async IAsyncEnumerable<MessageEnvelope<T>> _empty<T>([EnumeratorCancellation] CancellationToken ct = default) {
      await Task.CompletedTask;
      yield break;
    }
  }

  // UUIDv7 comparer used by both fakes. Matches the framework: generated EventId /
  // StreamId CompareTo overloads delegate to Guid.CompareTo, which on little-endian
  // platforms compares the first 6 bytes (the v7 timestamp) in big-endian byte order
  // because of how Guid packs its fields. Keeping Comparer<Guid>.Default aligns the
  // test fakes with how Whizbang's generated identity types actually order themselves.
  internal static readonly IComparer<Guid> _uuidV7Comparer = Comparer<Guid>.Default;

  /// <summary>
  /// Coordinator for the chained-rewinds scenario. Emits three distinct rewind cycles in
  /// sequence — each cycle returns a cursor with RewindTriggerEventId set to the next late
  /// index, and a single pending work item. On completion the cursor clears and advances.
  /// The worker sees three separate rewinds rather than a single collapsed one.
  /// </summary>
  private sealed class _sequentialRewindCoordinator : IWorkCoordinator {
    private readonly Guid _streamId;
    private readonly string _perspectiveName;
    private readonly Guid[] _eventIds;
    private readonly int[] _lateIndices;
    private int _currentLateIdx;
    private readonly PerspectiveCursorInfo _baseCursor;
    private PerspectiveCursorInfo _cursor;
    private readonly TaskCompletionSource _allRewinds = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public _sequentialRewindCoordinator(
      Guid streamId, string perspectiveName,
      Guid[] eventIds, int[] lateIndices,
      PerspectiveCursorInfo baseCursor) {
      _streamId = streamId;
      _perspectiveName = perspectiveName;
      _eventIds = eventIds;
      _lateIndices = lateIndices;
      _baseCursor = baseCursor;
      _cursor = baseCursor;
    }

    public int CurrentLateIndex => _currentLateIdx < _lateIndices.Length ? _lateIndices[_currentLateIdx] : -1;

    public Task WaitForAllRewindsAsync(TimeSpan timeout) => _allRewinds.Task.WaitAsync(timeout);

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      if (_currentLateIdx >= _lateIndices.Length) {
        return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
      }
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [new PerspectiveWork {
          WorkId = Guid.CreateVersion7(),
          StreamId = _streamId,
          PerspectiveName = _perspectiveName,
          LastProcessedEventId = _baseCursor.LastEventId,
          PartitionNumber = 1
        }]
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      if (completion.Status.HasFlag(PerspectiveProcessingStatus.Completed)) {
        _currentLateIdx++;
        if (_currentLateIdx < _lateIndices.Length) {
          // Set cursor for the NEXT rewind.
          _cursor = _baseCursor with {
            Status = PerspectiveProcessingStatus.RewindRequired,
            RewindTriggerEventId = _eventIds[_lateIndices[_currentLateIdx]]
          };
        } else {
          _cursor = _baseCursor with {
            Status = PerspectiveProcessingStatus.None,
            RewindTriggerEventId = null
          };
          _allRewinds.TrySetResult();
        }
      }
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(streamId == _streamId && perspectiveName == _perspectiveName ? _cursor : null);
  }

  /// <summary>
  /// Replay reader that, each time it's called, returns the CURRENT rewind's trigger event
  /// as is_new. Simulates the LEFT JOIN behavior in a sequence of rewinds: each cycle sees
  /// exactly one is_new event (the late arrival that triggered that cycle).
  /// </summary>
  private sealed class _cyclicReplayReader : IPerspectiveReplayReader {
    private readonly List<MessageEnvelope<IEvent>> _events;
    private readonly _sequentialRewindCoordinator _coordinator;

    public _cyclicReplayReader(List<MessageEnvelope<IEvent>> events, _sequentialRewindCoordinator coordinator) {
      _events = events;
      _coordinator = coordinator;
    }

    public async IAsyncEnumerable<ReplayEventEnvelope> ReadReplayEventsAsync(
      Guid streamId, string perspectiveName, int fromVersionExclusive,
      IReadOnlyCollection<Type> eventTypes, [EnumeratorCancellation] CancellationToken cancellationToken) {
      await Task.CompletedTask;
      var lateIdx = _coordinator.CurrentLateIndex;
      foreach (var env in _events) {
        var isNew = lateIdx >= 0 && env.MessageId.Value == _events[lateIdx].MessageId.Value;
        yield return new ReplayEventEnvelope(env, isNew);
      }
    }
  }

  /// <summary>
  /// Replay reader that returns the provided envelopes, each annotated with IsNew=true
  /// if its id appears in the preset is_new set. Simulates the LEFT JOIN against the
  /// perspective work queue that the real Postgres implementation will do.
  /// </summary>
  private sealed class _scriptedReplayReader : IPerspectiveReplayReader {
    private readonly List<MessageEnvelope<IEvent>> _events;
    private readonly HashSet<Guid> _isNew;

    public _scriptedReplayReader(List<MessageEnvelope<IEvent>> events, HashSet<Guid> isNew) {
      _events = events;
      _isNew = isNew;
    }

    public async IAsyncEnumerable<ReplayEventEnvelope> ReadReplayEventsAsync(
      Guid streamId,
      string perspectiveName,
      int fromVersionExclusive,
      IReadOnlyCollection<Type> eventTypes,
      [EnumeratorCancellation] CancellationToken cancellationToken) {
      await Task.CompletedTask;
      foreach (var env in _events) {
        yield return new ReplayEventEnvelope(env, _isNew.Contains(env.MessageId.Value));
      }
    }
  }

  private sealed class _fakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;
    ServiceInstanceInfo IServiceInstanceProvider.ToInfo() =>
      new() { ServiceName = ServiceName, InstanceId = InstanceId, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class _fakeDatabaseReadiness : IDatabaseReadinessCheck {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
  }
}
