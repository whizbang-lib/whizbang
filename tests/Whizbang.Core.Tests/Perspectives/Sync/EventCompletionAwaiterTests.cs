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
}
