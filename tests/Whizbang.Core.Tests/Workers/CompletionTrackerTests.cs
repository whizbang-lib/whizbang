using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Unit tests for CompletionTracker with acknowledgement-before-clear pattern.
/// </summary>
public class CompletionTrackerTests {
  [Test]
  public async Task Add_NewCompletion_StatusIsPending_Async() {
    // Arrange
    var tracker = new CompletionTracker<TestCompletion>();
    var completion = new TestCompletion { Id = Guid.NewGuid(), Data = "Test" };

    // Act
    tracker.Add(completion);
    var pending = tracker.GetPending();

    // Assert
    await Assert.That(pending.Length).IsEqualTo(1);
    await Assert.That(pending[0].Status).IsEqualTo(CompletionStatus.Pending);
    await Assert.That(pending[0].Completion).IsEqualTo(completion);
  }

  [Test]
  public async Task Add_MultipleCompletions_AllArePending_Async() {
    // Arrange
    var tracker = new CompletionTracker<TestCompletion>();
    var completions = new[] {
      new TestCompletion { Id = Guid.NewGuid(), Data = "Test1" },
      new TestCompletion { Id = Guid.NewGuid(), Data = "Test2" },
      new TestCompletion { Id = Guid.NewGuid(), Data = "Test3" }
    };

    // Act
    foreach (var c in completions) {
      tracker.Add(c);
    }
    var pending = tracker.GetPending();

    // Assert
    await Assert.That(pending.Length).IsEqualTo(3);
    await Assert.That(tracker.PendingCount).IsEqualTo(3);
  }

  [Test]
  public async Task MarkAsSent_PendingItems_StatusChangesSent_Async() {
    // Arrange
    var tracker = new CompletionTracker<TestCompletion>();
    var completion = new TestCompletion { Id = Guid.NewGuid(), Data = "Test" };
    tracker.Add(completion);
    var pending = tracker.GetPending();
    var sentAt = DateTimeOffset.UtcNow;

    // Act
    tracker.MarkAsSent(pending, sentAt);

    // Assert
    await Assert.That(pending[0].Status).IsEqualTo(CompletionStatus.Sent);
    await Assert.That(pending[0].SentAt).IsEqualTo(sentAt);
    await Assert.That(tracker.PendingCount).IsEqualTo(0);
    await Assert.That(tracker.SentCount).IsEqualTo(1);
  }

  [Test]
  public async Task MarkAsSent_EmptyArray_NoError_Async() {
    // Arrange
    var tracker = new CompletionTracker<TestCompletion>();
    var empty = Array.Empty<TrackedCompletion<TestCompletion>>();

    // Act
    tracker.MarkAsSent(empty, DateTimeOffset.UtcNow);

    // Assert - no exception thrown
    await Assert.That(tracker.SentCount).IsEqualTo(0);
  }

  [Test]
  public async Task MarkAsAcknowledged_SentItems_MarksOldestNItems_Async() {
    // Arrange
    var tracker = new CompletionTracker<TestCompletion>();
    var completions = new[] {
      new TestCompletion { Id = Guid.NewGuid(), Data = "Test1" },
      new TestCompletion { Id = Guid.NewGuid(), Data = "Test2" },
      new TestCompletion { Id = Guid.NewGuid(), Data = "Test3" },
      new TestCompletion { Id = Guid.NewGuid(), Data = "Test4" },
      new TestCompletion { Id = Guid.NewGuid(), Data = "Test5" }
    };

    // Add all and mark as sent with staggered timestamps
    var baseTime = DateTimeOffset.UtcNow;
    for (var i = 0; i < completions.Length; i++) {
      tracker.Add(completions[i]);
    }
    var pending = tracker.GetPending();
    for (var i = 0; i < pending.Length; i++) {
      tracker.MarkAsSent(new[] { pending[i] }, baseTime.AddSeconds(i));
    }

    // Act: Acknowledge oldest 3
    tracker.MarkAsAcknowledged(3);

    // Assert
    await Assert.That(tracker.SentCount).IsEqualTo(2); // 2 still sent
    await Assert.That(tracker.AcknowledgedCount).IsEqualTo(3); // 3 acknowledged
  }

  [Test]
  public async Task MarkAsAcknowledged_MoreThanAvailable_MarksAllSent_Async() {
    // Arrange
    var tracker = new CompletionTracker<TestCompletion>();
    tracker.Add(new TestCompletion { Id = Guid.NewGuid(), Data = "Test1" });
    tracker.Add(new TestCompletion { Id = Guid.NewGuid(), Data = "Test2" });
    var pending = tracker.GetPending();
    tracker.MarkAsSent(pending, DateTimeOffset.UtcNow);

    // Act: Try to acknowledge 10 items (but only 2 exist)
    tracker.MarkAsAcknowledged(10);

    // Assert
    await Assert.That(tracker.AcknowledgedCount).IsEqualTo(2);
    await Assert.That(tracker.SentCount).IsEqualTo(0);
  }

  [Test]
  public async Task ClearAcknowledged_RemovesOnlyAcknowledged_Async() {
    // Arrange
    var tracker = new CompletionTracker<TestCompletion>();
    tracker.Add(new TestCompletion { Id = Guid.NewGuid(), Data = "Pending" });
    tracker.Add(new TestCompletion { Id = Guid.NewGuid(), Data = "ToSend" });
    tracker.Add(new TestCompletion { Id = Guid.NewGuid(), Data = "ToAck" });

    var pending = tracker.GetPending();
    tracker.MarkAsSent(new[] { pending[1], pending[2] }, DateTimeOffset.UtcNow);
    tracker.MarkAsAcknowledged(1);

    // Act
    tracker.ClearAcknowledged();

    // Assert
    await Assert.That(tracker.PendingCount).IsEqualTo(1); // Still pending
    await Assert.That(tracker.SentCount).IsEqualTo(1); // Still sent
    await Assert.That(tracker.AcknowledgedCount).IsEqualTo(0); // Cleared
  }

  [Test]
  public async Task ResetStale_ExponentialBackoff_IncreasesTimeout_Async() {
    // Arrange
    var baseTimeout = TimeSpan.FromSeconds(10); // 10s for testing
    var tracker = new CompletionTracker<TestCompletion>(
      baseTimeout: baseTimeout,
      backoffMultiplier: 2.0,
      maxTimeout: TimeSpan.FromMinutes(5)
    );

    tracker.Add(new TestCompletion { Id = Guid.NewGuid(), Data = "Test" });
    var pending = tracker.GetPending();
    var sentAt = DateTimeOffset.UtcNow.AddSeconds(-15); // 15 seconds ago
    tracker.MarkAsSent(pending, sentAt);

    // Act: First reset (after 15s, > 10s timeout)
    tracker.ResetStale(DateTimeOffset.UtcNow);

    // Assert: Should reset to Pending, RetryCount = 1
    var pending1 = tracker.GetPending();
    await Assert.That(pending1.Length).IsEqualTo(1);
    await Assert.That(pending1[0].RetryCount).IsEqualTo(1);

    // Act: Mark as sent again, wait 25 seconds (> 20s timeout for retry 1)
    tracker.MarkAsSent(pending1, DateTimeOffset.UtcNow.AddSeconds(-25));
    tracker.ResetStale(DateTimeOffset.UtcNow);

    // Assert: Should reset to Pending, RetryCount = 2
    var pending2 = tracker.GetPending();
    await Assert.That(pending2.Length).IsEqualTo(1);
    await Assert.That(pending2[0].RetryCount).IsEqualTo(2);
  }

  [Test]
  public async Task ResetStale_NotStale_RemainsInSentStatus_Async() {
    // Arrange
    var tracker = new CompletionTracker<TestCompletion>(
      baseTimeout: TimeSpan.FromMinutes(5)
    );

    tracker.Add(new TestCompletion { Id = Guid.NewGuid(), Data = "Test" });
    var pending = tracker.GetPending();
    tracker.MarkAsSent(pending, DateTimeOffset.UtcNow); // Just sent

    // Act: Try to reset stale (but not stale yet)
    tracker.ResetStale(DateTimeOffset.UtcNow);

    // Assert: Should still be Sent
    await Assert.That(tracker.SentCount).IsEqualTo(1);
    await Assert.That(tracker.PendingCount).IsEqualTo(0);
  }

  [Test]
  public async Task ResetStale_MaxTimeout_CapsExponentialBackoff_Async() {
    // Arrange
    var tracker = new CompletionTracker<TestCompletion>(
      baseTimeout: TimeSpan.FromSeconds(10),
      backoffMultiplier: 2.0,
      maxTimeout: TimeSpan.FromSeconds(30) // Cap at 30s
    );

    tracker.Add(new TestCompletion { Id = Guid.NewGuid(), Data = "Test" });
    var pending = tracker.GetPending();

    // Simulate many retries to exceed max timeout
    for (var i = 0; i < 10; i++) {
      var sentAt = DateTimeOffset.UtcNow.AddSeconds(-100); // Way past any timeout
      tracker.MarkAsSent(pending, sentAt);
      tracker.ResetStale(DateTimeOffset.UtcNow);
      pending = tracker.GetPending();
    }

    // Assert: RetryCount should be 10, but timeout calculation is capped
    await Assert.That(pending[0].RetryCount).IsEqualTo(10);
    // We can't directly test the timeout calculation, but it should be capped at 30s
  }

  [Test]
  public async Task CountProperties_ReflectCurrentState_Async() {
    // Arrange
    var tracker = new CompletionTracker<TestCompletion>();
    tracker.Add(new TestCompletion { Id = Guid.NewGuid(), Data = "Pending1" });
    tracker.Add(new TestCompletion { Id = Guid.NewGuid(), Data = "Pending2" });
    tracker.Add(new TestCompletion { Id = Guid.NewGuid(), Data = "ToSend" });
    tracker.Add(new TestCompletion { Id = Guid.NewGuid(), Data = "ToAck" });

    // Act & Assert initial state
    await Assert.That(tracker.PendingCount).IsEqualTo(4);
    await Assert.That(tracker.SentCount).IsEqualTo(0);
    await Assert.That(tracker.AcknowledgedCount).IsEqualTo(0);

    // Mark 2 as sent
    var pending = tracker.GetPending();
    tracker.MarkAsSent(new[] { pending[2], pending[3] }, DateTimeOffset.UtcNow);
    await Assert.That(tracker.PendingCount).IsEqualTo(2);
    await Assert.That(tracker.SentCount).IsEqualTo(2);

    // Mark 1 as acknowledged
    tracker.MarkAsAcknowledged(1);
    await Assert.That(tracker.SentCount).IsEqualTo(1);
    await Assert.That(tracker.AcknowledgedCount).IsEqualTo(1);

    // Clear acknowledged
    tracker.ClearAcknowledged();
    await Assert.That(tracker.PendingCount).IsEqualTo(2);
    await Assert.That(tracker.SentCount).IsEqualTo(1);
    await Assert.That(tracker.AcknowledgedCount).IsEqualTo(0);
  }
}

/// <summary>
/// Simple test completion type for CompletionTracker tests.
/// </summary>
internal sealed class TestCompletion {
  public required Guid Id { get; init; }
  public required string Data { get; init; }
}
