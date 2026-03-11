using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Integration-style tests for partial cancellation scenarios where multiple awaiters
/// wait on the same events and some cancel while others continue.
/// </summary>
/// <tests>Whizbang.Core/Perspectives/Sync/SyncEventTracker.cs</tests>
[Category("PerspectiveSync")]
public class SyncEventTrackerPartialCancellationTests {
  private sealed record TestEventA;
  private sealed record TestEventB;

  // ==========================================================================
  // Two awaiters, cancel one, other completes
  // ==========================================================================

  [Test]
  public async Task TwoAwaiters_SameEvent_CancelOne_OtherCompletesAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var perspectiveName = "TestPerspective";

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, perspectiveName);

    var awaiter1Id = Guid.NewGuid();
    var awaiter2Id = Guid.NewGuid();

    // Start both awaiters waiting
    var task1 = tracker.WaitForPerspectiveEventsAsync(
        [eventId], perspectiveName, TimeSpan.FromSeconds(5), awaiter1Id);
    var task2 = tracker.WaitForPerspectiveEventsAsync(
        [eventId], perspectiveName, TimeSpan.FromSeconds(5), awaiter2Id);

    // Act - Cancel awaiter 1
    tracker.UnregisterAwaiter(awaiter1Id);

    // Signal the event as processed
    tracker.MarkProcessedByPerspective([eventId], perspectiveName);

    // Assert
    var result1 = await task1;
    var result2 = await task2;

    // Awaiter 1 was cancelled → false
    await Assert.That(result1).IsFalse();
    // Awaiter 2 completed normally → true
    await Assert.That(result2).IsTrue();
  }

  [Test]
  public async Task TwoAwaiters_SameEvent_CancelOne_OtherTimesOutAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var perspectiveName = "TestPerspective";

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, perspectiveName);

    var awaiter1Id = Guid.NewGuid();
    var awaiter2Id = Guid.NewGuid();

    // Awaiter 1 with short timeout, awaiter 2 also short timeout
    var task1 = tracker.WaitForPerspectiveEventsAsync(
        [eventId], perspectiveName, TimeSpan.FromMilliseconds(200), awaiter1Id);
    var task2 = tracker.WaitForPerspectiveEventsAsync(
        [eventId], perspectiveName, TimeSpan.FromMilliseconds(200), awaiter2Id);

    // Act - Cancel awaiter 1 only
    tracker.UnregisterAwaiter(awaiter1Id);

    // Don't process the event — awaiter 2 should time out

    // Assert
    var result1 = await task1;
    var result2 = await task2;

    await Assert.That(result1).IsFalse().Because("Awaiter 1 was cancelled");
    await Assert.That(result2).IsFalse().Because("Awaiter 2 timed out");
  }

  // ==========================================================================
  // Three awaiters on overlapping event sets, cancel middle
  // ==========================================================================

  [Test]
  public async Task ThreeAwaiters_OverlappingEvents_CancelMiddle_RemainingCompleteAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var event1 = Guid.NewGuid();
    var event2 = Guid.NewGuid();
    var event3 = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var perspectiveName = "TestPerspective";

    tracker.TrackEvent(typeof(TestEventA), event1, streamId, perspectiveName);
    tracker.TrackEvent(typeof(TestEventA), event2, streamId, perspectiveName);
    tracker.TrackEvent(typeof(TestEventB), event3, streamId, perspectiveName);

    var awaiter1Id = Guid.NewGuid(); // waits for event1, event2
    var awaiter2Id = Guid.NewGuid(); // waits for event2, event3 (overlaps with both)
    var awaiter3Id = Guid.NewGuid(); // waits for event3

    var task1 = tracker.WaitForPerspectiveEventsAsync(
        [event1, event2], perspectiveName, TimeSpan.FromSeconds(5), awaiter1Id);
    var task2 = tracker.WaitForPerspectiveEventsAsync(
        [event2, event3], perspectiveName, TimeSpan.FromSeconds(5), awaiter2Id);
    var task3 = tracker.WaitForPerspectiveEventsAsync(
        [event3], perspectiveName, TimeSpan.FromSeconds(5), awaiter3Id);

    // Act - Cancel the middle awaiter
    tracker.UnregisterAwaiter(awaiter2Id);

    // Process all events
    tracker.MarkProcessedByPerspective([event1, event2, event3], perspectiveName);

    // Assert
    var result1 = await task1;
    var result2 = await task2;
    var result3 = await task3;

    await Assert.That(result1).IsTrue().Because("Awaiter 1 completed normally");
    await Assert.That(result2).IsFalse().Because("Awaiter 2 was cancelled");
    await Assert.That(result3).IsTrue().Because("Awaiter 3 completed normally");
  }

  // ==========================================================================
  // UnregisterAwaiter for non-existent awaiter is safe
  // ==========================================================================

  [Test]
  public async Task UnregisterAwaiter_NonExistentId_DoesNothingAsync() {
    var tracker = new SyncEventTracker();
    var nonExistentId = Guid.NewGuid();

    // Should not throw
    tracker.UnregisterAwaiter(nonExistentId);

    // No exception thrown — test passes by reaching this point
    await Task.CompletedTask;
  }

  // ==========================================================================
  // UnregisterAwaiter cleans up across all waiter types
  // ==========================================================================

  [Test]
  public async Task UnregisterAwaiter_CleansUpAllWaiterTypesAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var perspectiveName = "TestPerspective";

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, perspectiveName);

    var awaiterId = Guid.NewGuid();

    // Start waiting via all three wait methods
    var perspectiveTask = tracker.WaitForPerspectiveEventsAsync(
        [eventId], perspectiveName, TimeSpan.FromSeconds(5), awaiterId);
    var allPerspectivesTask = tracker.WaitForAllPerspectivesAsync(
        [eventId], TimeSpan.FromSeconds(5), awaiterId);
    var eventsTask = tracker.WaitForEventsAsync(
        [eventId], TimeSpan.FromSeconds(5), awaiterId);

    // Act - Unregister should clean up all three
    tracker.UnregisterAwaiter(awaiterId);

    // Assert - All should return false (cancelled)
    var perspectiveResult = await perspectiveTask;
    var allResult = await allPerspectivesTask;
    var eventsResult = await eventsTask;

    await Assert.That(perspectiveResult).IsFalse();
    await Assert.That(allResult).IsFalse();
    await Assert.That(eventsResult).IsFalse();
  }

  // ==========================================================================
  // Event still tracked after awaiter cancellation
  // ==========================================================================

  [Test]
  public async Task UnregisterAwaiter_EventStillTracked_NewAwaiterCanWaitAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var perspectiveName = "TestPerspective";

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, perspectiveName);

    var awaiter1Id = Guid.NewGuid();
    var task1 = tracker.WaitForPerspectiveEventsAsync(
        [eventId], perspectiveName, TimeSpan.FromSeconds(5), awaiter1Id);

    // Cancel first awaiter
    tracker.UnregisterAwaiter(awaiter1Id);
    var result1 = await task1;
    await Assert.That(result1).IsFalse();

    // Act - New awaiter can still wait for the same event
    var awaiter2Id = Guid.NewGuid();
    var task2 = tracker.WaitForPerspectiveEventsAsync(
        [eventId], perspectiveName, TimeSpan.FromSeconds(5), awaiter2Id);

    // Process the event
    tracker.MarkProcessedByPerspective([eventId], perspectiveName);

    // Assert
    var result2 = await task2;
    await Assert.That(result2).IsTrue()
        .Because("Event was still tracked, new awaiter should complete");
  }
}
