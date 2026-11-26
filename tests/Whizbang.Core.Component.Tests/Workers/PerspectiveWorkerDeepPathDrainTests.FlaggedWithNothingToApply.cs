// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2601: a cursor flagged RewindRequired whose queued rows hold no event its perspective applies is still rewound. The
/// drain forms a pair only for an event the perspective reads, and the rewind ran only from a pair, so a trigger the
/// perspective does not read (one a database flagged before migration 602, or a type this service does not register) was
/// never replayed: its row was leased on every pass with nothing recorded and the cursor stayed flagged. The worker now
/// runs the flagged rewind from the stored trigger and completes the rows; only a flagged cursor takes this path.
/// </summary>
public partial class PerspectiveWorkerDeepPathDrainTests {
  /// <summary>An event type the worker does not register: the store reads no event for it.</summary>
  /// <summary>How long a wait on a signal may take before the test reports what it was waiting for.</summary>
  private static readonly TimeSpan _signalWait = TimeSpan.FromSeconds(20);

  private const string UNREAD_EVENT_TYPE = "Elsewhere.Contracts.StepApprovedEvent, Elsewhere.Contracts";

  /// <summary>An event type the worker registers that no perspective here reads.</summary>
  private sealed record UnreadDeepEvent(string Data) : IEvent;

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_AFlaggedCursorWhoseRowHoldsATypeThisServiceDoesNotRegister_IsRewound_AndItsRowCompletedAsync(
      CancellationToken cancellationToken) {
    var (streamId, trigger, work) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    var coordinator = _flaggedAt(streamId, trigger);
    coordinator.EnqueueStreamEvents([_raw(streamId, trigger, work) with { EventType = UNREAD_EVENT_TYPE, Attempts = 62 }]);
    var runner = new DrainRunner();
    var (worker, harness, _) = _createWorker(coordinator, new DrainEventStore(), _registry(runner));

    await _drainOnceAsync(worker, harness, streamId, harness.CompletionCapture.WaitForEventWorkIdsAsync(_signalWait, work), cancellationToken);

    await Assert.That(runner.RewindCalls.Select(c => c.TriggerEventId)).IsEquivalentTo([trigger])
      .Because("the stored cursor asks for a rewind from its trigger, which a pair can never form for");
    await Assert.That(coordinator.Completions.Any(c => c.StreamId == streamId && c.Status == PerspectiveProcessingStatus.Completed))
      .IsTrue().Because("the rewind's completion is reported, which clears the cursor's flag");
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(0);
    await Assert.That(harness.FailureCapture.Items).IsEmpty();
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_AFlaggedCursorWhoseRowHoldsAKnownTypeItDoesNotRead_IsRewound_AndItsRowCompletedAsync(
      CancellationToken cancellationToken) {
    var (streamId, trigger, work) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    var coordinator = _flaggedAt(streamId, trigger);
    coordinator.EnqueueStreamEvents([_raw(streamId, trigger, work) with { EventType = TypeNameFormatter.Format(typeof(UnreadDeepEvent)) }]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(trigger, new UnreadDeepEvent("not read"))]);
    var runner = new DrainRunner();
    var registry = new MultiRunnerRegistry([typeof(DrainDeepEvent), typeof(UnreadDeepEvent)], [typeof(DrainDeepEvent)]);
    registry.Add(PERSPECTIVE, runner);
    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry, registryFactory: _ => registry);

    await _drainOnceAsync(worker, harness, streamId, harness.CompletionCapture.WaitForEventWorkIdsAsync(_signalWait, work), cancellationToken);

    await Assert.That(runner.RewindCalls.Select(c => c.TriggerEventId)).IsEquivalentTo([trigger]);
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_AnUnflaggedCursorsRowItDoesNotRead_IsLeftAsBefore_WithNoRewindAsync(CancellationToken cancellationToken) {
    var (streamId, eventId, work) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    var coordinator = new DrainWorkCoordinator {
      CursorsBatchToReturn = [_flaggedCursor(streamId, eventId) with { Status = PerspectiveProcessingStatus.Completed, RewindTriggerEventId = null }],
    };
    var (eventStore, companionWork) = _withACompanionStream(coordinator, _raw(streamId, eventId, work) with { EventType = UNREAD_EVENT_TYPE });
    var runner = new DrainRunner();
    var (worker, harness, _) = _createWorker(coordinator, eventStore, _registry(runner));

    await _drainOnceAsync(worker, harness, streamId, harness.CompletionCapture.WaitForEventWorkIdsAsync(_signalWait, companionWork), cancellationToken);

    await Assert.That(runner.RewindCalls).IsEmpty().Because("only a flagged cursor takes the rewind path");
    await Assert.That(harness.CompletionCapture.EventWorkIds).IsEquivalentTo([companionWork])
      .Because("a type a newer instance may read is left for it, as before (the rolling-deploy hedge)");
    await Assert.That(harness.FailureCapture.Items).IsEmpty();
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_AFlaggedCursorWhoseRewindThrows_FailsItsRowsForTheBoundedRetryAsync(CancellationToken cancellationToken) {
    var (streamId, trigger, work) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    var coordinator = _flaggedAt(streamId, trigger);
    coordinator.EnqueueStreamEvents([_raw(streamId, trigger, work) with { EventType = UNREAD_EVENT_TYPE }]);
    var runner = new DrainRunner { RewindException = new InvalidOperationException("replay failed") };
    var (worker, harness, _) = _createWorker(coordinator, new DrainEventStore(), _registry(runner));

    await _drainOnceAsync(worker, harness, streamId, harness.FailureCapture.WaitForCountAsync(1, cancellationToken), cancellationToken);

    var (category, failure) = harness.FailureCapture.Items.Single();
    await Assert.That(category).IsEqualTo(WorkCategory.PerspectiveEvent);
    await Assert.That(failure.MessageId).IsEqualTo(work).Because("the failure is recorded against the row, so the dead-letter bound applies");
    await Assert.That(failure.Error).Contains("replay failed", StringComparison.Ordinal);
    await Assert.That(harness.CompletionCapture.EventWorkIds).IsEmpty();
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_AFlaggedCursorWhoseRewindDoesNotComplete_LeavesItsRowsPendingAsync(CancellationToken cancellationToken) {
    var (streamId, trigger, work) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    var coordinator = _flaggedAt(streamId, trigger);
    coordinator.EnqueueStreamEvents([_raw(streamId, trigger, work) with { EventType = UNREAD_EVENT_TYPE }]);
    var runner = new DrainRunner { RewindCompletionStatus = PerspectiveProcessingStatus.None };
    var (worker, harness, _) = _createWorker(coordinator, new DrainEventStore(), _registry(runner));

    await _drainOnceAsync(worker, harness, streamId, coordinator.FirstCompletion, cancellationToken);

    await Assert.That(runner.RewindCalls.Count).IsEqualTo(1);
    await Assert.That(harness.CompletionCapture.EventWorkIds).IsEmpty()
      .Because("a rewind that did not complete (a stream lock held elsewhere) leaves the rows for the next lease");
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_AFlaggedCursorOfAPerspectiveWithNoRunnerHere_IsNotRewoundHereAsync(CancellationToken cancellationToken) {
    var (streamId, trigger, work) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    const string retired = "Retired.Perspective+Projection";
    var coordinator = new DrainWorkCoordinator {
      CursorsBatchToReturn = [_flaggedCursor(streamId, trigger) with { PerspectiveName = retired }],
    };
    var (eventStore, companionWork) = _withACompanionStream(
      coordinator, _raw(streamId, trigger, work, perspectiveName: retired) with { EventType = UNREAD_EVENT_TYPE });
    var runner = new DrainRunner();
    var (worker, harness, _) = _createWorker(coordinator, eventStore, _registry(runner));

    await _drainOnceAsync(worker, harness, streamId, harness.CompletionCapture.WaitForEventWorkIdsAsync(_signalWait, companionWork), cancellationToken);

    await Assert.That(runner.RewindCalls).IsEmpty().Because("a perspective no longer registered is the cleanup step's, not the drain's");
    await Assert.That(harness.CompletionCapture.EventWorkIds).IsEquivalentTo([companionWork]);
  }

  /// <summary>
  /// Fetches <paramref name="row"/> in one batch with a healthy stream's row, which is applied after the batch's rows are
  /// sorted: its completion is the signal that the batch was handled. Returns the store and the healthy row's work id.
  /// </summary>
  private static (DrainEventStore Store, Guid CompanionWork) _withACompanionStream(DrainWorkCoordinator coordinator, StreamEventData row) {
    var (companionStream, companionEvent, companionWork) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    coordinator.EnqueueStreamEvents([row, _raw(companionStream, companionEvent, companionWork)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(companionEvent, new DrainDeepEvent("healthy"))]);
    return (eventStore, companionWork);
  }

  private static DrainWorkCoordinator _flaggedAt(Guid streamId, Guid trigger) =>
    new() { CursorsBatchToReturn = [_flaggedCursor(streamId, trigger)] };

  private static PerspectiveCursorInfo _flaggedCursor(Guid streamId, Guid trigger) => new() {
    StreamId = streamId,
    PerspectiveName = PERSPECTIVE,
    LastEventId = Guid.CreateVersion7(),
    LastCommitSequence = 100,
    Status = PerspectiveProcessingStatus.Completed | PerspectiveProcessingStatus.RewindRequired,
    RewindTriggerEventId = trigger,
  };

  /// <summary>Starts the worker, hands it the stream, waits on <paramref name="done"/> and stops it.</summary>
  private static async Task _drainOnceAsync(PerspectiveWorker worker, PerspectiveWorkerTestHarness harness, Guid streamId, Task done,
      CancellationToken cancellationToken) {
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await done.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
  }
}
