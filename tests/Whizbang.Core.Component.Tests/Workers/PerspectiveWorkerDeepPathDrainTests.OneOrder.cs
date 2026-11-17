// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2366: the drain path in the stream's one order. A cursor flagged RewindRequired is resolved by a rewind, never by
/// applying its pending events live; and an event a completed replay read through (the frontier the cursor stores) is
/// already applied, so its work row is completed without a rewind, on a cold cache too.
/// </summary>
/// <docs>fundamentals/perspectives/rewind#one-order-for-a-stream</docs>
public partial class PerspectiveWorkerDeepPathDrainTests {

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_ACursorFlaggedForARewind_IsRewound_NotAppliedLiveAsync(CancellationToken cancellationToken) {
    var streamId = Guid.CreateVersion7();
    var trigger = Guid.CreateVersion7();
    var pending = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    // The pending event is ahead of the cursor, so no inversion is found: only the flag asks for the rewind.
    coordinator.CursorOverrides[(PERSPECTIVE, streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = PERSPECTIVE,
      LastEventId = Guid.CreateVersion7(),
      LastCommitSequence = 100,
      Status = PerspectiveProcessingStatus.Completed | PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = trigger,
    };
    coordinator.EnqueueStreamEvents([_raw(streamId, pending, Guid.CreateVersion7(), commitSequence: 150)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(pending, new DrainDeepEvent("ahead"))]);
    var runner = new DrainRunner();
    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(runner), configure: opts => opts.DrainLoopMaxIterations = 1);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(runner.RewindCalls.Count).IsEqualTo(1)
      .Because("the stored cursor asks for a rewind; applying its events live would clear the flag without ever replaying the trigger");
    await Assert.That(runner.RewindCalls.TryPeek(out var call) ? call.TriggerEventId : Guid.Empty).IsEqualTo(trigger);
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  [Arguments(false)]
  [Arguments(true)]
  public async Task DrainMode_AnEventTheStoredReplayFrontierCovers_IsCompletedWithoutARewindAsync(bool flagged, CancellationToken cancellationToken) {
    // A restarted worker: nothing in memory, only what the cursor stores. The copies below its commit sequence were
    // read by the replay that stored the frontier, so they are not inversions; on a flagged cursor they are the
    // exception the flag allows, already covered.
    var streamId = Guid.CreateVersion7();
    var (eventA, eventB) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    var coordinator = new DrainWorkCoordinator {
      CursorsBatchToReturn = [
        new PerspectiveCursorInfo {
          StreamId = streamId,
          PerspectiveName = PERSPECTIVE,
          LastEventId = Guid.CreateVersion7(),
          LastCommitSequence = 100,
          ReplayedThroughCommitSequence = 100,
          Status = flagged ? PerspectiveProcessingStatus.RewindRequired : PerspectiveProcessingStatus.Completed,
          RewindTriggerEventId = flagged ? eventA : null,
        }
      ]
    };
    coordinator.EnqueueStreamEvents([
      _raw(streamId, eventA, Guid.CreateVersion7(), commitSequence: 50),
      _raw(streamId, eventB, Guid.CreateVersion7(), commitSequence: 51),
    ]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventA, new DrainDeepEvent("a")), _envelope(eventB, new DrainDeepEvent("b"))]);
    var runner = new DrainRunner();
    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(runner), configure: opts => opts.DrainLoopMaxIterations = 1);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    // The refetch after the lap: the lap's decision is made.
    await coordinator.WaitForStreamEventsCallsAsync(2, cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(runner.RewindCalls.Count).IsEqualTo(0)
      .Because("both events are stamped below the frontier a completed replay read through, so they are applied already");
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_AfterAReplayFromTheStart_AnEventBelowItsFrontier_IsNotRewoundAgainAsync(CancellationToken cancellationToken) {
    // The counterpart of DrainMode_AfterARewind_TheCommitSequenceCursorMovesWithTheEventIdCursorAsync: there the replay
    // started from a snapshot and says nothing about 150; here it read the stream from its start through 200, so the
    // event at 150, delivered again, was read by it.
    var streamId = Guid.CreateVersion7();
    var (lateEvent, laterEvent, frontierEvent) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    var coordinator = new DrainWorkCoordinator();
    coordinator.CursorOverrides[(PERSPECTIVE, streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = PERSPECTIVE,
      LastEventId = Guid.CreateVersion7(),
      LastCommitSequence = 100,
      Status = PerspectiveProcessingStatus.None
    };
    coordinator.EnqueueStreamEvents([_raw(streamId, lateEvent, Guid.CreateVersion7(), commitSequence: 50)]);
    coordinator.EnqueueStreamEvents([_raw(streamId, laterEvent, Guid.CreateVersion7(), commitSequence: 150)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(lateEvent, new DrainDeepEvent("late"))]);
    eventStore.EnqueueDeserialized([_envelope(laterEvent, new DrainDeepEvent("later"))]);
    var runner = new DrainRunner { Frontier = (frontierEvent, 200), ReplayedThrough = 200 };
    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(runner), configure: opts => opts.DrainLoopRefetchMinBatch = 1);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.WaitForStreamEventsCallsAsync(3, cancellationToken);
    await coordinator.FirstCompletion.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(runner.RewindCalls.Count).IsEqualTo(1)
      .Because("the first rewind read every event stamped through 200; the one at 150 is covered, not a new inversion");
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(0);
  }
}
