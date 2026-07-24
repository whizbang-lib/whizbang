using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="IEventCompletionAwaiter"/> and <see cref="EventCompletionAwaiter"/>.
/// </summary>
/// <remarks>
/// These tests verify the event completion awaiter which waits for events to be
/// fully processed by ALL perspectives, not just one.
/// </remarks>
/// <docs>core-concepts/perspectives/event-completion</docs>
public class EventCompletionAwaiterTests {
  // ==========================================================================
  // WaitForEventsAsync tests
  // ==========================================================================

  [Test]
  public async Task WaitForEventsAsync_WaitsForAllPerspectivesToCompleteAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    // Track event for TWO perspectives
    tracker.TrackEvent(typeof(string), eventId, streamId, "Perspective1");
    tracker.TrackEvent(typeof(string), eventId, streamId, "Perspective2");

    var waitTask = awaiter.WaitForEventsAsync([eventId], TimeSpan.FromSeconds(5));

    // Act - process first perspective
    tracker.MarkProcessedByPerspective([eventId], "Perspective1");

    // Should NOT complete yet - Perspective2 still pending
    await Task.Delay(50);
    await Assert.That(waitTask.IsCompleted).IsFalse();

    // Process second perspective
    tracker.MarkProcessedByPerspective([eventId], "Perspective2");

    // Assert - should complete now
    var result = await waitTask;
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForEventsAsync_ReturnsImmediatelyWhenNoEventsTrackedAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);
    var eventId = Guid.NewGuid();

    // Act - event is not tracked
    var result = await awaiter.WaitForEventsAsync([eventId], TimeSpan.FromSeconds(1));

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForEventsAsync_ReturnsImmediatelyWhenEmptyListAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);

    // Act
    var result = await awaiter.WaitForEventsAsync([], TimeSpan.FromSeconds(1));

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForEventsAsync_ReturnsImmediatelyWhenNullListAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);

    // Act
    var result = await awaiter.WaitForEventsAsync(null!, TimeSpan.FromSeconds(1));

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForEventsAsync_TimeoutsWhenPerspectiveNeverCompletesAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    // Track event for a perspective but never mark it processed
    tracker.TrackEvent(typeof(string), eventId, streamId, "Perspective1");

    // Act - wait with short timeout
    var result = await awaiter.WaitForEventsAsync([eventId], TimeSpan.FromMilliseconds(100));

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task WaitForEventsAsync_HandlesMultipleEventsAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    // Track two events for same perspective
    tracker.TrackEvent(typeof(string), eventId1, streamId, "Perspective1");
    tracker.TrackEvent(typeof(string), eventId2, streamId, "Perspective1");

    var waitTask = awaiter.WaitForEventsAsync([eventId1, eventId2], TimeSpan.FromSeconds(5));

    // Act - process first event
    tracker.MarkProcessedByPerspective([eventId1], "Perspective1");

    // Should NOT complete yet - eventId2 still pending
    await Task.Delay(50);
    await Assert.That(waitTask.IsCompleted).IsFalse();

    // Process second event
    tracker.MarkProcessedByPerspective([eventId2], "Perspective1");

    // Assert - should complete now
    var result = await waitTask;
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForEventsAsync_SupportsCancellationAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    tracker.TrackEvent(typeof(string), eventId, streamId, "Perspective1");

    using var cts = new CancellationTokenSource();

    // Act - start waiting then cancel
    var waitTask = awaiter.WaitForEventsAsync([eventId], TimeSpan.FromSeconds(30), cts.Token);
    await Task.Delay(50);
    cts.Cancel();

    // Assert - should return false (cancelled)
    var result = await waitTask;
    await Assert.That(result).IsFalse();
  }

  // ==========================================================================
  // AreEventsFullyProcessed tests
  // ==========================================================================

  [Test]
  public async Task AreEventsFullyProcessed_ReturnsTrueWhenNoPerspectivesRemainAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    // Track and then process the event
    tracker.TrackEvent(typeof(string), eventId, streamId, "Perspective1");
    tracker.MarkProcessedByPerspective([eventId], "Perspective1");

    // Act
    var isFullyProcessed = awaiter.AreEventsFullyProcessed([eventId]);

    // Assert
    await Assert.That(isFullyProcessed).IsTrue();
  }

  [Test]
  public async Task AreEventsFullyProcessed_ReturnsFalseWhenPerspectivesPendingAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    // Track event but don't process it
    tracker.TrackEvent(typeof(string), eventId, streamId, "Perspective1");

    // Act
    var isFullyProcessed = awaiter.AreEventsFullyProcessed([eventId]);

    // Assert
    await Assert.That(isFullyProcessed).IsFalse();
  }

  [Test]
  public async Task AreEventsFullyProcessed_ReturnsTrueWhenEventNeverTrackedAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);
    var eventId = Guid.NewGuid();

    // Act - event was never tracked
    var isFullyProcessed = awaiter.AreEventsFullyProcessed([eventId]);

    // Assert
    await Assert.That(isFullyProcessed).IsTrue();
  }

  [Test]
  public async Task AreEventsFullyProcessed_ReturnsTrueForEmptyListAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);

    // Act
    var isFullyProcessed = awaiter.AreEventsFullyProcessed([]);

    // Assert
    await Assert.That(isFullyProcessed).IsTrue();
  }

  [Test]
  public async Task AreEventsFullyProcessed_ReturnsTrueForNullListAsync() {
    // Arrange
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);

    // Act
    var isFullyProcessed = awaiter.AreEventsFullyProcessed(null!);

    // Assert
    await Assert.That(isFullyProcessed).IsTrue();
  }

  // ==========================================================================
  // Constructor tests
  // ==========================================================================

  [Test]
  public async Task Constructor_ThrowsWhenTrackerIsNullAsync() {
    // Act & Assert
    await Assert.That(() => new EventCompletionAwaiter(null!))
        .Throws<ArgumentNullException>()
        .WithMessageContaining("syncEventTracker");
  }

  // ==========================================================================
  // Partial completion tests
  // ==========================================================================

  [Test]
  public async Task WaitForEventsAsync_FivePerspectives_FourComplete_TimesOutAsync() {
    // Arrange — track event for 5 perspectives, only mark 4
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    for (var i = 1; i <= 5; i++) {
      tracker.TrackEvent(typeof(object), eventId, streamId, $"Perspective{i}");
    }

    var awaiter = new EventCompletionAwaiter(tracker);

    // Mark 4 of 5 perspectives
    for (var i = 1; i <= 4; i++) {
      tracker.MarkProcessedByPerspective([eventId], $"Perspective{i}");
    }

    // Act — should timeout because Perspective5 never completes
    var result = await awaiter.WaitForEventsAsync([eventId], TimeSpan.FromMilliseconds(100));

    // Assert
    await Assert.That(result).IsFalse()
      .Because("Should timeout when one perspective hasn't completed");
  }

  [Test]
  public async Task WaitForEventsAsync_NeverTrackedEvent_ReturnsTrueImmediatelyAsync() {
    // Arrange — event was never tracked (not in registry, or never emitted)
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);
    var untrackedEventId = Guid.NewGuid();

    // Act — should return true immediately (nothing to wait for)
    var result = await awaiter.WaitForEventsAsync([untrackedEventId], TimeSpan.FromSeconds(5));

    // Assert — this is documented behavior: "never tracked" = "completed"
    await Assert.That(result).IsTrue()
      .Because("Events that were never tracked are considered completed");
  }

  // ==========================================================================
  // "Fully applied" invariant — locks the contract that distinguishes
  // IEventCompletionAwaiter from IPerspectiveSyncAwaiter.
  // ==========================================================================

  /// <summary>
  /// Sanity-check: the awaiter MUST NOT return after a single subscribing perspective
  /// processes the event. If one event has N subscribing perspectives, the wait stays
  /// blocked until ALL N have called MarkProcessedByPerspective. This is the contract
  /// that callers rely on when they want "event fully applied" semantics — and it's
  /// what distinguishes <see cref="IEventCompletionAwaiter"/> from
  /// <see cref="IPerspectiveSyncAwaiter"/> (which waits for ONE perspective only).
  /// </summary>
  /// <remarks>
  /// Regression-locks the failure mode that surfaced as flaky tests on PR #204
  /// (work-pump-decomposition): a test relying on a counter of perspective "fires"
  /// can be satisfied early if one event-application increments the counter past the
  /// threshold while another subscriber is still processing. The right primitive is
  /// "wait until no perspective is still tracking the event", which this test pins.
  /// </remarks>
  [Test]
  public async Task WaitForEventsAsync_OneEventThreeSubscribers_OnlyReturnsAfterAllThreeApplyAsync() {
    // Arrange — one event with three subscribing perspectives (e.g., ProductCreatedEvent
    // subscribed to by ProductCatalog + InventoryLevels + AnalyticsView).
    var tracker = new SyncEventTracker();
    var awaiter = new EventCompletionAwaiter(tracker);
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    tracker.TrackEvent(typeof(string), eventId, streamId, "ProductCatalog");
    tracker.TrackEvent(typeof(string), eventId, streamId, "InventoryLevels");
    tracker.TrackEvent(typeof(string), eventId, streamId, "AnalyticsView");

    var waitTask = awaiter.WaitForEventsAsync([eventId], TimeSpan.FromSeconds(5));

    // Act — apply on subscriber 1; wait MUST stay blocked.
    tracker.MarkProcessedByPerspective([eventId], "ProductCatalog");
    await Task.Delay(50);
    await Assert.That(waitTask.IsCompleted).IsFalse()
      .Because("Wait must not signal after just ONE of three subscribers applied — the event is only PARTIALLY applied.");

    // Apply on subscriber 2; wait MUST still stay blocked.
    tracker.MarkProcessedByPerspective([eventId], "InventoryLevels");
    await Task.Delay(50);
    await Assert.That(waitTask.IsCompleted).IsFalse()
      .Because("Wait must not signal after two of three subscribers applied — the event is still partial.");

    // Apply on subscriber 3; NOW the wait completes.
    tracker.MarkProcessedByPerspective([eventId], "AnalyticsView");

    var result = await waitTask;
    await Assert.That(result).IsTrue()
      .Because("Once all three subscribers have applied the event, the wait must return true.");
  }

  /// <summary>
  /// Cross-perspective ordering doesn't matter: applying perspectives in any order
  /// produces the same "fully applied" signal. Locks that the tracker's "no remaining
  /// perspective" check is set-based, not order-based.
  /// </summary>
  [Test]
  public async Task WaitForEventsAsync_OneEventTwoSubscribers_OrderOfApplyDoesNotMatterAsync() {
    // Run twice with reversed apply order. Both runs must produce the same outcome.
    foreach (var reverseOrder in new[] { false, true }) {
      var tracker = new SyncEventTracker();
      var awaiter = new EventCompletionAwaiter(tracker);
      var eventId = Guid.NewGuid();
      var streamId = Guid.NewGuid();

      tracker.TrackEvent(typeof(string), eventId, streamId, "A");
      tracker.TrackEvent(typeof(string), eventId, streamId, "B");

      var waitTask = awaiter.WaitForEventsAsync([eventId], TimeSpan.FromSeconds(5));

      if (reverseOrder) {
        tracker.MarkProcessedByPerspective([eventId], "B");
        await Task.Delay(50);
        await Assert.That(waitTask.IsCompleted).IsFalse()
          .Because($"reverse={reverseOrder}: still partial after B");
        tracker.MarkProcessedByPerspective([eventId], "A");
      } else {
        tracker.MarkProcessedByPerspective([eventId], "A");
        await Task.Delay(50);
        await Assert.That(waitTask.IsCompleted).IsFalse()
          .Because($"reverse={reverseOrder}: still partial after A");
        tracker.MarkProcessedByPerspective([eventId], "B");
      }

      await Assert.That(await waitTask).IsTrue()
        .Because($"reverse={reverseOrder}: wait returns true once both applied");
    }
  }
}
