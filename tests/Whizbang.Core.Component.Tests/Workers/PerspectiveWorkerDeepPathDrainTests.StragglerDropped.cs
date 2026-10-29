// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1862: an ephemeral perspective that cannot rewind for a late event (no snapshot floor precedes it and the bodies a
/// replay would read are reaped) drops the event. The drop has to retire the event's work, once. Before, the runner
/// reported only "nothing replayed", so the work stayed pending, the drain loop and every later claim found the same
/// event behind the cursor, and the same rewind was skipped and the same drop logged without end, surviving restarts
/// because the pending work lives in the database.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public partial class PerspectiveWorkerDeepPathDrainTests {
  private const string STRAGGLERS_DROPPED = "whizbang.perspective.stragglers_dropped";

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task DrainMode_EphemeralStragglerWithNoSnapshotFloor_IsDroppedOnceAcrossLapsAsync(bool stragglerRedeliveredAsNewRows) {
    // A late event 28 commit sequences behind the cursor arrives with one ordinary event after the cursor. The drain
    // loop refetches between laps, and the straggler comes back each time: its own row (not flushed yet), or the same
    // event delivered again under a new row.
    var streamId = Guid.CreateVersion7();
    var straggler = Guid.CreateVersion7();
    var frontier = Guid.CreateVersion7();
    var ahead = Guid.CreateVersion7();
    var coordinator = _coordinatorWithCursor(_cursorAt(streamId, frontier, 100));
    var eventStore = new DrainEventStore();
    const int laps = 3;
    var stragglerWork = Guid.CreateVersion7();
    var firstStragglerWork = stragglerWork;
    var aheadWork = Guid.CreateVersion7();
    for (var lap = 0; lap < laps; lap++) {
      if (stragglerRedeliveredAsNewRows && lap > 0) {
        stragglerWork = Guid.CreateVersion7();
      }
      coordinator.EnqueueStreamEvents([
        _raw(streamId, straggler, stragglerWork, commitSequence: 72),
        _raw(streamId, ahead, aheadWork, commitSequence: 101),
      ]);
      eventStore.EnqueueDeserialized([
        _envelope(straggler, new DrainDeepEvent("late")),
        _envelope(ahead, new DrainDeepEvent("ahead")),
      ]);
    }
    var runner = new DrainRunner { Stragglers = [straggler] };
    using var meterFactory = new TestMeterFactory();
    var metrics = _metricsOf(meterFactory);
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(runner),
      configure: opts => opts.DrainLoopMaxIterations = laps,
      cooldownCache: new RecentlyProcessedEventCache(new SystemTimeProvider()),
      metrics: metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    // Completions are handed to the flush when the worker goes idle, so the drain and all its laps have ended here.
    await harness.CompletionCapture.WaitForEventWorkIdsAsync(TimeSpan.FromSeconds(20), firstStragglerWork, aheadWork);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(runner.RewindCalls.Count).IsEqualTo(1)
      .Because("the straggler is dropped once; a lap that finds it again must not skip the same rewind again");
    await Assert.That(runner.ReceivedBatches.Select(b => string.Join(",", b))).IsEquivalentTo([ahead.ToString()])
      .Because("the event after the cursor is still applied, in the same pass, and the straggler never reaches an apply");
    await Assert.That(_sum(meters, STRAGGLERS_DROPPED)).IsEqualTo(1d)
      .Because("one straggler, one drop, whatever number of laps or rows bring it back");
    await Assert.That(coordinator.Completions.Any(c => c.LastEventId == straggler)).IsFalse()
      .Because("the cursor is never moved backward to the dropped event");
  }

  [Test]
  public async Task DrainMode_StragglersLeftPendingByAnOlderBuild_StopAfterOneClaimAsync() {
    // The state an older build leaves on disk: the cursor carries the rewind flag and two late events are still
    // pending, because every earlier attempt skipped the rewind and retired nothing. One claim after the upgrade
    // retires both, and the laps after it find nothing to do.
    var streamId = Guid.CreateVersion7();
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();
    var frontier = Guid.CreateVersion7();
    var coordinator = _coordinatorWithCursor(_cursorAt(streamId, frontier, 100) with {
      Status = PerspectiveProcessingStatus.Completed | PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = first
    });
    var eventStore = new DrainEventStore();
    const int laps = 3;
    var firstWork = Guid.CreateVersion7();
    var secondWork = Guid.CreateVersion7();
    for (var lap = 0; lap < laps; lap++) {
      coordinator.EnqueueStreamEvents([
        _raw(streamId, first, firstWork, commitSequence: 72),
        _raw(streamId, second, secondWork, commitSequence: 73),
      ]);
      eventStore.EnqueueDeserialized([
        _envelope(first, new DrainDeepEvent("late-1")),
        _envelope(second, new DrainDeepEvent("late-2")),
      ]);
    }
    var runner = new DrainRunner { Stragglers = [first, second] };
    using var meterFactory = new TestMeterFactory();
    var metrics = _metricsOf(meterFactory);
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(runner),
      configure: opts => opts.DrainLoopMaxIterations = laps,
      cooldownCache: new RecentlyProcessedEventCache(new SystemTimeProvider()),
      metrics: metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await harness.CompletionCapture.WaitForEventWorkIdsAsync(TimeSpan.FromSeconds(20), firstWork, secondWork);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(runner.RewindCalls.Count).IsEqualTo(1)
      .Because("the loop an older build left behind stops at the first claim after the upgrade");
    await Assert.That(runner.ReceivedBatches.Select(b => string.Join(",", b))).IsEquivalentTo([second.ToString()])
      .Because("the second straggler is handed on once, the runner drops it in turn, and nothing is applied");
    await Assert.That(_sum(meters, STRAGGLERS_DROPPED)).IsEqualTo(2d)
      .Because("each straggler is counted once");
    await Assert.That(coordinator.Completions).IsNotEmpty()
      .Because("the pair is reported, which is what lets the cursor's rewind flag clear once its work is gone");
    await Assert.That(coordinator.Completions.All(c => c.LastEventId == Guid.Empty && c.StreamId == streamId)).IsTrue()
      .Because("the cursor stays where it was: nothing was replayed and nothing moves it backward");
  }

  [Test]
  public async Task DrainMode_RunThatDropsAStragglerBelowTheRow_RetiresItAndAppliesTheRestAsync() {
    // No cursor inversion is visible to the worker (the cursor is cold), but the runner finds the late event below its
    // row's own position, rewinds for it, and drops it. The worker retires it and hands the rest back once.
    var streamId = Guid.CreateVersion7();
    var straggler = Guid.CreateVersion7();
    var ahead = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    var eventStore = new DrainEventStore();
    const int laps = 3;
    var stragglerWork = Guid.CreateVersion7();
    var aheadWork = Guid.CreateVersion7();
    for (var lap = 0; lap < laps; lap++) {
      coordinator.EnqueueStreamEvents([
        _raw(streamId, straggler, stragglerWork, commitSequence: 72),
        _raw(streamId, ahead, aheadWork, commitSequence: 101),
      ]);
      eventStore.EnqueueDeserialized([
        _envelope(straggler, new DrainDeepEvent("late")),
        _envelope(ahead, new DrainDeepEvent("ahead")),
      ]);
    }
    var runner = new DrainRunner { Stragglers = [straggler] };
    using var meterFactory = new TestMeterFactory();
    var metrics = _metricsOf(meterFactory);
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(runner),
      configure: opts => opts.DrainLoopMaxIterations = laps,
      cooldownCache: new RecentlyProcessedEventCache(new SystemTimeProvider()),
      metrics: metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await harness.CompletionCapture.WaitForEventWorkIdsAsync(TimeSpan.FromSeconds(20), stragglerWork, aheadWork);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    var batches = runner.ReceivedBatches.ToList();
    await Assert.That(batches).Count().IsEqualTo(2)
      .Because("the run that dropped the straggler is followed by one run of what is left, and later laps find both "
             + "events already handled");
    await Assert.That(batches[0]).IsEquivalentTo([straggler, ahead]);
    await Assert.That(batches[1]).IsEquivalentTo([ahead])
      .Because("what is handed back is the batch without the dropped straggler");
    await Assert.That(runner.RewindCalls.Count).IsEqualTo(0);
    await Assert.That(_sum(meters, STRAGGLERS_DROPPED)).IsEqualTo(1d);
  }

  [Test]
  public async Task DrainMode_RunReportsADropOutsideItsBatch_RetiresNothingAndDoesNotRunAgainAsync() {
    // A runner can only drop an event it was handed. One that names another is not believed: nothing of the batch is
    // retired on its word, and the batch is not handed back in a loop.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var workId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, workId, commitSequence: 5)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("only"))]);
    var runner = new DrainRunner { RunWithEventsReportsDropped = [Guid.CreateVersion7()] };
    using var meterFactory = new TestMeterFactory();
    var metrics = _metricsOf(meterFactory);
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, harness, _) = _createWorker(coordinator, eventStore, _registry(runner), metrics: metrics);
    var cycleComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnBatchCycleComplete += () => cycleComplete.TrySetResult();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await cycleComplete.Task.WaitAsync(TimeSpan.FromSeconds(20));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(1);
    await Assert.That(_sum(meters, STRAGGLERS_DROPPED)).IsEqualTo(0d);
    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(workId)
      .Because("an event the runner did not drop and did not apply is still owed an apply");
  }

  [Test]
  public async Task DrainMode_RewindThatCompletes_CountsNoDroppedStragglerAsync() {
    // Guard: a perspective that can rewind (not ephemeral, or a snapshot floor is there) keeps the rewind path it had.
    var streamId = Guid.CreateVersion7();
    var late = Guid.CreateVersion7();
    var frontier = Guid.CreateVersion7();
    var workId = Guid.CreateVersion7();
    var coordinator = _coordinatorWithCursor(_cursorAt(streamId, frontier, 100));
    coordinator.EnqueueStreamEvents([_raw(streamId, late, workId, commitSequence: 72)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(late, new DrainDeepEvent("late"))]);
    var runner = new DrainRunner { Frontier = (frontier, 100) };
    using var meterFactory = new TestMeterFactory();
    var metrics = _metricsOf(meterFactory);
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, harness, _) = _createWorker(coordinator, eventStore, _registry(runner), metrics: metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await harness.CompletionCapture.WaitForEventWorkIdsAsync(TimeSpan.FromSeconds(20), workId);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(runner.RewindCalls.Count).IsEqualTo(1);
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(0)
      .Because("a rewind that replayed the stream leaves nothing to hand back");
    await Assert.That(_sum(meters, STRAGGLERS_DROPPED)).IsEqualTo(0d);
    await Assert.That(coordinator.Completions.Any(c => c.LastEventId == frontier)).IsTrue()
      .Because("the completed rewind still moves the cursor to where the replay ended");
  }

  private static PerspectiveCursorInfo _cursorAt(Guid streamId, Guid lastEventId, long lastCommitSequence) => new() {
    StreamId = streamId,
    PerspectiveName = PERSPECTIVE,
    LastEventId = lastEventId,
    LastCommitSequence = lastCommitSequence,
    Status = PerspectiveProcessingStatus.Completed
  };

  /// <summary>
  /// A coordinator holding the stream's persisted cursor: the batch prefetch returns it, and so does the per-stream read
  /// a cold cursor cache falls back to, the way the database answers both after a rewind invalidates the cache.
  /// </summary>
  private static DrainWorkCoordinator _coordinatorWithCursor(PerspectiveCursorInfo cursor) {
    var coordinator = new DrainWorkCoordinator { CursorsBatchToReturn = [cursor] };
    coordinator.CursorOverrides[(cursor.PerspectiveName, cursor.StreamId)] = cursor;
    return coordinator;
  }

  private static PerspectiveMetrics _metricsOf(TestMeterFactory meterFactory) =>
    new(new WhizbangMetrics(meterFactory));

  private static double _sum(MetricAssertionHelper meters, string instrumentName) =>
    meters.GetByName(instrumentName).Sum(m => m.Value);
}
