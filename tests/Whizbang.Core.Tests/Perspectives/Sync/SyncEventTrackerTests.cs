using System.Collections.Concurrent;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="ISyncEventTracker"/> and <see cref="SyncEventTracker"/>.
/// Singleton tracker that bridges request scopes within the same microservice instance.
/// </summary>
/// <docs>core-concepts/perspectives/perspective-sync#event-tracking</docs>
public class SyncEventTrackerTests {
  // Sample event types for testing
  private sealed record TestEventA;
  private sealed record TestEventB;
  private sealed record TestEventC;

  // ==========================================================================
  // TrackedSyncEvent record tests
  // ==========================================================================

  [Test]
  public async Task TrackedSyncEvent_StoresAllPropertiesAsync() {
    var eventType = typeof(TestEventA);
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    const string perspectiveName = "TestPerspective";
    var trackedAt = DateTime.UtcNow;

    var tracked = new TrackedSyncEvent(eventType, eventId, streamId, perspectiveName, trackedAt);

    await Assert.That(tracked.EventType).IsEqualTo(eventType);
    await Assert.That(tracked.EventId).IsEqualTo(eventId);
    await Assert.That(tracked.StreamId).IsEqualTo(streamId);
    await Assert.That(tracked.PerspectiveName).IsEqualTo(perspectiveName);
    await Assert.That(tracked.TrackedAt).IsEqualTo(trackedAt);
  }

  // ==========================================================================
  // TrackEvent tests
  // ==========================================================================

  [Test]
  public async Task TrackEvent_AddsToTrackedListAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");

    var allIds = tracker.GetAllTrackedEventIds();
    await Assert.That(allIds.Count).IsEqualTo(1);
    await Assert.That(allIds[0]).IsEqualTo(eventId);
  }

  [Test]
  public async Task TrackEvent_SameEventId_DoesNotDuplicateAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective"); // Duplicate

    var allIds = tracker.GetAllTrackedEventIds();
    await Assert.That(allIds.Count).IsEqualTo(1);
  }

  [Test]
  public async Task TrackEvent_MultipleEvents_TracksAllAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();

    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();
    var eventId3 = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId1, streamId, "TestPerspective");
    tracker.TrackEvent(typeof(TestEventB), eventId2, streamId, "TestPerspective");
    tracker.TrackEvent(typeof(TestEventC), eventId3, streamId, "TestPerspective");

    var allIds = tracker.GetAllTrackedEventIds();
    await Assert.That(allIds.Count).IsEqualTo(3);
  }

  // ==========================================================================
  // GetPendingEvents filter tests
  // ==========================================================================

  [Test]
  public async Task GetPendingEvents_FiltersByStreamIdAsync() {
    var tracker = new SyncEventTracker();
    var targetStreamId = Guid.NewGuid();
    var otherStreamId = Guid.NewGuid();
    const string perspectiveName = "TestPerspective";

    tracker.TrackEvent(typeof(TestEventA), Guid.NewGuid(), targetStreamId, perspectiveName);
    tracker.TrackEvent(typeof(TestEventB), Guid.NewGuid(), otherStreamId, perspectiveName);
    tracker.TrackEvent(typeof(TestEventC), Guid.NewGuid(), targetStreamId, perspectiveName);

    var pending = tracker.GetPendingEvents(targetStreamId, perspectiveName);

    await Assert.That(pending.Count).IsEqualTo(2);
    await Assert.That(pending.All(e => e.StreamId == targetStreamId)).IsTrue();
  }

  [Test]
  public async Task GetPendingEvents_FiltersByPerspectiveNameAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), Guid.NewGuid(), streamId, "PerspectiveA");
    tracker.TrackEvent(typeof(TestEventB), Guid.NewGuid(), streamId, "PerspectiveB");
    tracker.TrackEvent(typeof(TestEventC), Guid.NewGuid(), streamId, "PerspectiveA");

    var pending = tracker.GetPendingEvents(streamId, "PerspectiveA");

    await Assert.That(pending.Count).IsEqualTo(2);
    await Assert.That(pending.All(e => e.PerspectiveName == "PerspectiveA")).IsTrue();
  }

  [Test]
  public async Task GetPendingEvents_FiltersByEventTypesAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    const string perspectiveName = "TestPerspective";

    tracker.TrackEvent(typeof(TestEventA), Guid.NewGuid(), streamId, perspectiveName);
    tracker.TrackEvent(typeof(TestEventB), Guid.NewGuid(), streamId, perspectiveName);
    tracker.TrackEvent(typeof(TestEventC), Guid.NewGuid(), streamId, perspectiveName);

    var pending = tracker.GetPendingEvents(streamId, perspectiveName, [typeof(TestEventA), typeof(TestEventC)]);

    await Assert.That(pending.Count).IsEqualTo(2);
    await Assert.That(pending.Any(e => e.EventType == typeof(TestEventA))).IsTrue();
    await Assert.That(pending.Any(e => e.EventType == typeof(TestEventC))).IsTrue();
    await Assert.That(pending.Any(e => e.EventType == typeof(TestEventB))).IsFalse();
  }

  [Test]
  public async Task GetPendingEvents_NoEventTypes_ReturnsAllForStreamAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    const string perspectiveName = "TestPerspective";

    tracker.TrackEvent(typeof(TestEventA), Guid.NewGuid(), streamId, perspectiveName);
    tracker.TrackEvent(typeof(TestEventB), Guid.NewGuid(), streamId, perspectiveName);

    var pending = tracker.GetPendingEvents(streamId, perspectiveName, eventTypes: null);

    await Assert.That(pending.Count).IsEqualTo(2);
  }

  [Test]
  public async Task GetPendingEvents_EmptyEventTypes_ReturnsAllForStreamAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    const string perspectiveName = "TestPerspective";

    tracker.TrackEvent(typeof(TestEventA), Guid.NewGuid(), streamId, perspectiveName);
    tracker.TrackEvent(typeof(TestEventB), Guid.NewGuid(), streamId, perspectiveName);

    var pending = tracker.GetPendingEvents(streamId, perspectiveName, eventTypes: []);

    await Assert.That(pending.Count).IsEqualTo(2);
  }

  [Test]
  public async Task GetPendingEvents_NoMatches_ReturnsEmptyAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), Guid.NewGuid(), Guid.NewGuid(), "OtherPerspective");

    var pending = tracker.GetPendingEvents(streamId, "TestPerspective");

    await Assert.That(pending.Count).IsEqualTo(0);
  }

  // ==========================================================================
  // MarkProcessed tests
  // ==========================================================================

  [Test]
  public async Task MarkProcessed_RemovesFromTrackedListAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId1, streamId, "TestPerspective");
    tracker.TrackEvent(typeof(TestEventB), eventId2, streamId, "TestPerspective");

    tracker.MarkProcessed([eventId1]);

    var allIds = tracker.GetAllTrackedEventIds();
    await Assert.That(allIds.Count).IsEqualTo(1);
    await Assert.That(allIds[0]).IsEqualTo(eventId2);
  }

  [Test]
  public async Task MarkProcessed_MultipleIds_RemovesAllAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();
    var eventId3 = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId1, streamId, "TestPerspective");
    tracker.TrackEvent(typeof(TestEventB), eventId2, streamId, "TestPerspective");
    tracker.TrackEvent(typeof(TestEventC), eventId3, streamId, "TestPerspective");

    tracker.MarkProcessed([eventId1, eventId2]);

    var allIds = tracker.GetAllTrackedEventIds();
    await Assert.That(allIds.Count).IsEqualTo(1);
    await Assert.That(allIds[0]).IsEqualTo(eventId3);
  }

  [Test]
  public async Task MarkProcessed_NonExistentId_NoOpAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");

    // Mark a non-existent ID - should not throw
    tracker.MarkProcessed([Guid.NewGuid()]);

    var allIds = tracker.GetAllTrackedEventIds();
    await Assert.That(allIds.Count).IsEqualTo(1);
    await Assert.That(allIds[0]).IsEqualTo(eventId);
  }

  [Test]
  public async Task MarkProcessed_EmptyEnumerable_NoOpAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");

    tracker.MarkProcessed([]);

    var allIds = tracker.GetAllTrackedEventIds();
    await Assert.That(allIds.Count).IsEqualTo(1);
  }

  // ==========================================================================
  // GetAllTrackedEventIds tests
  // ==========================================================================

  [Test]
  public async Task GetAllTrackedEventIds_ReturnsAllIdsAsync() {
    var tracker = new SyncEventTracker();
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId1, Guid.NewGuid(), "TestPerspective");
    tracker.TrackEvent(typeof(TestEventB), eventId2, Guid.NewGuid(), "TestPerspective");

    var allIds = tracker.GetAllTrackedEventIds();

    await Assert.That(allIds.Count).IsEqualTo(2);
    await Assert.That(allIds.Contains(eventId1)).IsTrue();
    await Assert.That(allIds.Contains(eventId2)).IsTrue();
  }

  [Test]
  public async Task GetAllTrackedEventIds_EmptyTracker_ReturnsEmptyAsync() {
    var tracker = new SyncEventTracker();

    var allIds = tracker.GetAllTrackedEventIds();

    await Assert.That(allIds.Count).IsEqualTo(0);
  }

  // ==========================================================================
  // Thread safety tests
  // ==========================================================================

  [Test]
  public async Task ThreadSafety_ConcurrentTrackAndRemoveAsync() {
    var tracker = new SyncEventTracker();
    var eventIds = new ConcurrentBag<Guid>();
    var streamId = Guid.NewGuid();
    const int operationsPerThread = 100;
    const int threadCount = 10;

    // Track events concurrently
    var trackTasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() => {
      for (int i = 0; i < operationsPerThread; i++) {
        var eventId = Guid.NewGuid();
        eventIds.Add(eventId);
        tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");
      }
    }));

    await Task.WhenAll(trackTasks);

    var allIds = tracker.GetAllTrackedEventIds();
    await Assert.That(allIds.Count).IsEqualTo(threadCount * operationsPerThread);

    // Remove events concurrently
    var idsToRemove = eventIds.Take(threadCount * operationsPerThread / 2).ToArray();
    var removeTasks = Enumerable.Range(0, threadCount).Select(threadIdx => Task.Run(() => {
      var startIdx = threadIdx * (idsToRemove.Length / threadCount);
      var endIdx = (threadIdx + 1) * (idsToRemove.Length / threadCount);
      tracker.MarkProcessed(idsToRemove.Skip(startIdx).Take(endIdx - startIdx));
    }));

    await Task.WhenAll(removeTasks);

    var remainingIds = tracker.GetAllTrackedEventIds();
    await Assert.That(remainingIds.Count).IsEqualTo(threadCount * operationsPerThread / 2);
  }

  [Test]
  public async Task ThreadSafety_ConcurrentGetPendingAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    const string perspectiveName = "TestPerspective";

    // Add some events first
    for (int i = 0; i < 100; i++) {
      tracker.TrackEvent(typeof(TestEventA), Guid.NewGuid(), streamId, perspectiveName);
    }

    // Concurrently read and write
    var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() => {
      if (i % 2 == 0) {
        // Read - verify no exception and valid result
        var pending = tracker.GetPendingEvents(streamId, perspectiveName);
        return pending is not null;
      } else {
        // Write
        tracker.TrackEvent(typeof(TestEventB), Guid.NewGuid(), streamId, perspectiveName);
        return true;
      }
    }));

    var results = await Task.WhenAll(tasks);
    await Assert.That(results.All(r => r)).IsTrue();
  }

  // ==========================================================================
  // Edge case tests
  // ==========================================================================

  [Test]
  public async Task TrackEvent_DifferentStreams_SameEventType_TrackedSeparatelyAsync() {
    var tracker = new SyncEventTracker();
    var stream1 = Guid.NewGuid();
    var stream2 = Guid.NewGuid();
    const string perspectiveName = "TestPerspective";

    tracker.TrackEvent(typeof(TestEventA), Guid.NewGuid(), stream1, perspectiveName);
    tracker.TrackEvent(typeof(TestEventA), Guid.NewGuid(), stream2, perspectiveName);

    var pendingStream1 = tracker.GetPendingEvents(stream1, perspectiveName);
    var pendingStream2 = tracker.GetPendingEvents(stream2, perspectiveName);

    await Assert.That(pendingStream1.Count).IsEqualTo(1);
    await Assert.That(pendingStream2.Count).IsEqualTo(1);
  }

  [Test]
  public async Task TrackEvent_SameStream_DifferentPerspectives_TrackedSeparatelyAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), Guid.NewGuid(), streamId, "PerspectiveA");
    tracker.TrackEvent(typeof(TestEventA), Guid.NewGuid(), streamId, "PerspectiveB");

    var pendingA = tracker.GetPendingEvents(streamId, "PerspectiveA");
    var pendingB = tracker.GetPendingEvents(streamId, "PerspectiveB");

    await Assert.That(pendingA.Count).IsEqualTo(1);
    await Assert.That(pendingB.Count).IsEqualTo(1);
    await Assert.That(pendingA[0].EventId).IsNotEqualTo(pendingB[0].EventId);
  }

  // ==========================================================================
  // MarkProcessedByPerspective tests
  // ==========================================================================

  [Test]
  public async Task MarkProcessedByPerspective_OnlyRemovesSpecificPerspective_LeavesOtherPerspectivesAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    // Same event tracked for TWO different perspectives
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "PerspectiveA");
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "PerspectiveB");

    // Mark processed for PerspectiveA only
    tracker.MarkProcessedByPerspective([eventId], "PerspectiveA");

    // Event should still be tracked for PerspectiveB
    var allIds = tracker.GetAllTrackedEventIds();
    await Assert.That(allIds.Count).IsEqualTo(1);
    await Assert.That(allIds[0]).IsEqualTo(eventId);

    // PerspectiveA should have no pending events
    var pendingA = tracker.GetPendingEvents(streamId, "PerspectiveA");
    await Assert.That(pendingA.Count).IsEqualTo(0);

    // PerspectiveB should still have the event
    var pendingB = tracker.GetPendingEvents(streamId, "PerspectiveB");
    await Assert.That(pendingB.Count).IsEqualTo(1);
  }

  [Test]
  public async Task MarkProcessedByPerspective_NoOpForUnknownPerspectiveAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "PerspectiveA");

    // Mark processed for a perspective that doesn't exist - should not throw
    tracker.MarkProcessedByPerspective([eventId], "NonExistentPerspective");

    // Event should still be tracked for PerspectiveA
    var allIds = tracker.GetAllTrackedEventIds();
    await Assert.That(allIds.Count).IsEqualTo(1);
  }

  [Test]
  public async Task MarkProcessedByPerspective_MultipleEventsProcessedCorrectlyAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId1, streamId, "PerspectiveA");
    tracker.TrackEvent(typeof(TestEventB), eventId2, streamId, "PerspectiveA");
    tracker.TrackEvent(typeof(TestEventA), eventId1, streamId, "PerspectiveB");

    // Mark both events processed for PerspectiveA
    tracker.MarkProcessedByPerspective([eventId1, eventId2], "PerspectiveA");

    // eventId2 should be completely removed (only tracked by PerspectiveA)
    // eventId1 should still be tracked (also tracked by PerspectiveB)
    var allIds = tracker.GetAllTrackedEventIds();
    await Assert.That(allIds.Count).IsEqualTo(1);
    await Assert.That(allIds[0]).IsEqualTo(eventId1);
  }

  // ==========================================================================
  // WaitForPerspectiveEventsAsync tests (waits for SPECIFIC perspective)
  // ==========================================================================

  [Test]
  public async Task WaitForPerspectiveEventsAsync_SignalsWhenSpecificPerspectiveProcessedAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    // Track event for TWO perspectives
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "PerspectiveA");
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "PerspectiveB");

    // Start waiting for PerspectiveA specifically
    var waitTask = tracker.WaitForPerspectiveEventsAsync([eventId], "PerspectiveA", TimeSpan.FromSeconds(5));

    // Mark processed for PerspectiveA only
    tracker.MarkProcessedByPerspective([eventId], "PerspectiveA");

    // Wait should complete even though PerspectiveB hasn't processed
    var result = await waitTask;
    await Assert.That(result).IsTrue();

    // PerspectiveB should still have pending event
    var pendingB = tracker.GetPendingEvents(streamId, "PerspectiveB");
    await Assert.That(pendingB.Count).IsEqualTo(1);
  }

  [Test]
  public async Task WaitForPerspectiveEventsAsync_DoesNotSignalWhenOtherPerspectiveProcessedAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    // Track event for TWO perspectives
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "PerspectiveA");
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "PerspectiveB");

    // Start waiting for PerspectiveA specifically with short timeout
    var waitTask = tracker.WaitForPerspectiveEventsAsync([eventId], "PerspectiveA", TimeSpan.FromMilliseconds(100));

    // Mark processed for PerspectiveB (NOT PerspectiveA)
    tracker.MarkProcessedByPerspective([eventId], "PerspectiveB");

    // Wait should timeout because PerspectiveA hasn't processed
    var result = await waitTask;
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task WaitForPerspectiveEventsAsync_ReturnsImmediatelyWhenNotTrackedAsync() {
    var tracker = new SyncEventTracker();

    // Event not tracked - should return immediately
    var result = await tracker.WaitForPerspectiveEventsAsync([Guid.NewGuid()], "PerspectiveA", TimeSpan.FromSeconds(5));

    await Assert.That(result).IsTrue();
  }

  // ==========================================================================
  // WaitForAllPerspectivesAsync tests (waits for ALL perspectives)
  // ==========================================================================

  [Test]
  public async Task WaitForAllPerspectivesAsync_SignalsOnlyWhenAllPerspectivesDoneAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    // Track event for TWO perspectives
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "PerspectiveA");
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "PerspectiveB");

    // Start waiting for ALL perspectives
    var waitTask = tracker.WaitForAllPerspectivesAsync([eventId], TimeSpan.FromSeconds(5));

    // Mark processed for PerspectiveA only - wait should NOT complete yet
    tracker.MarkProcessedByPerspective([eventId], "PerspectiveA");

    // Give it a moment to potentially (incorrectly) signal
    await Task.Delay(50);
    await Assert.That(waitTask.IsCompleted).IsFalse();

    // Mark processed for PerspectiveB - NOW wait should complete
    tracker.MarkProcessedByPerspective([eventId], "PerspectiveB");

    var result = await waitTask;
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForAllPerspectivesAsync_TimeoutsWhenNotAllPerspectivesProcessedAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    // Track event for TWO perspectives
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "PerspectiveA");
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "PerspectiveB");

    // Mark processed for only ONE perspective
    tracker.MarkProcessedByPerspective([eventId], "PerspectiveA");

    // Wait with short timeout - should timeout
    var result = await tracker.WaitForAllPerspectivesAsync([eventId], TimeSpan.FromMilliseconds(100));

    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task WaitForAllPerspectivesAsync_ReturnsImmediatelyWhenNotTrackedAsync() {
    var tracker = new SyncEventTracker();

    // Event not tracked - should return immediately
    var result = await tracker.WaitForAllPerspectivesAsync([Guid.NewGuid()], TimeSpan.FromSeconds(5));

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForAllPerspectivesAsync_HandlesMultipleEventsAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();

    // Track two events for different perspectives
    tracker.TrackEvent(typeof(TestEventA), eventId1, streamId, "PerspectiveA");
    tracker.TrackEvent(typeof(TestEventB), eventId2, streamId, "PerspectiveA");
    tracker.TrackEvent(typeof(TestEventA), eventId1, streamId, "PerspectiveB");

    // Start waiting for ALL perspectives on BOTH events
    var waitTask = tracker.WaitForAllPerspectivesAsync([eventId1, eventId2], TimeSpan.FromSeconds(5));

    // Mark eventId2 fully processed (only had PerspectiveA)
    tracker.MarkProcessedByPerspective([eventId2], "PerspectiveA");

    // Wait should NOT complete - eventId1 still has PerspectiveB pending
    await Task.Delay(50);
    await Assert.That(waitTask.IsCompleted).IsFalse();

    // Mark eventId1 for PerspectiveA
    tracker.MarkProcessedByPerspective([eventId1], "PerspectiveA");

    // Wait should STILL NOT complete - eventId1 still has PerspectiveB pending
    await Task.Delay(50);
    await Assert.That(waitTask.IsCompleted).IsFalse();

    // Mark eventId1 for PerspectiveB - NOW should complete
    tracker.MarkProcessedByPerspective([eventId1], "PerspectiveB");

    var result = await waitTask;
    await Assert.That(result).IsTrue();
  }

  // ==========================================================================
  // WaitForEventsAsync edge case tests
  // ==========================================================================

  [Test]
  public async Task WaitForEventsAsync_WithNullEventIds_ReturnsTrueImmediatelyAsync() {
    var tracker = new SyncEventTracker();

    var result = await tracker.WaitForEventsAsync(null!, TimeSpan.FromSeconds(5));

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForEventsAsync_WithEmptyEventIds_ReturnsTrueImmediatelyAsync() {
    var tracker = new SyncEventTracker();

    var result = await tracker.WaitForEventsAsync([], TimeSpan.FromSeconds(5));

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForEventsAsync_WithAlreadyProcessedEvents_ReturnsTrueImmediatelyAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();

    // Event not tracked = already processed
    var result = await tracker.WaitForEventsAsync([eventId], TimeSpan.FromSeconds(5));

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForEventsAsync_TimesOutWhenEventsNeverProcessedAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");

    // Don't mark as processed - should timeout
    var result = await tracker.WaitForEventsAsync([eventId], TimeSpan.FromMilliseconds(100));

    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task WaitForEventsAsync_SignalsWhenEventProcessedDuringWaitAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");

    // Start waiting
    var waitTask = tracker.WaitForEventsAsync([eventId], TimeSpan.FromSeconds(5));

    // Mark processed
    tracker.MarkProcessed([eventId]);

    var result = await waitTask;
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForEventsAsync_CancelledDuringWaitAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");

    using var cts = new CancellationTokenSource();

    // Start waiting
    var waitTask = tracker.WaitForEventsAsync([eventId], TimeSpan.FromSeconds(5), cancellationToken: cts.Token);

    // Cancel
    cts.Cancel();

    var result = await waitTask;
    await Assert.That(result).IsFalse();
  }

  // ==========================================================================
  // WaitForPerspectiveEventsAsync edge case tests
  // ==========================================================================

  [Test]
  public async Task WaitForPerspectiveEventsAsync_WithNullEventIds_ReturnsTrueImmediatelyAsync() {
    var tracker = new SyncEventTracker();

    var result = await tracker.WaitForPerspectiveEventsAsync(null!, "TestPerspective", TimeSpan.FromSeconds(5));

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForPerspectiveEventsAsync_WithEmptyEventIds_ReturnsTrueImmediatelyAsync() {
    var tracker = new SyncEventTracker();

    var result = await tracker.WaitForPerspectiveEventsAsync([], "TestPerspective", TimeSpan.FromSeconds(5));

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForPerspectiveEventsAsync_TimesOutWhenEventsNeverProcessedAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");

    // Don't mark as processed - should timeout
    var result = await tracker.WaitForPerspectiveEventsAsync([eventId], "TestPerspective", TimeSpan.FromMilliseconds(100));

    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task WaitForPerspectiveEventsAsync_CancelledDuringWaitAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");

    using var cts = new CancellationTokenSource();

    // Start waiting
    var waitTask = tracker.WaitForPerspectiveEventsAsync([eventId], "TestPerspective", TimeSpan.FromSeconds(5), cancellationToken: cts.Token);

    // Cancel
    cts.Cancel();

    var result = await waitTask;
    await Assert.That(result).IsFalse();
  }

  // ==========================================================================
  // WaitForAllPerspectivesAsync edge case tests
  // ==========================================================================

  [Test]
  public async Task WaitForAllPerspectivesAsync_WithNullEventIds_ReturnsTrueImmediatelyAsync() {
    var tracker = new SyncEventTracker();

    var result = await tracker.WaitForAllPerspectivesAsync(null!, TimeSpan.FromSeconds(5));

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForAllPerspectivesAsync_WithEmptyEventIds_ReturnsTrueImmediatelyAsync() {
    var tracker = new SyncEventTracker();

    var result = await tracker.WaitForAllPerspectivesAsync([], TimeSpan.FromSeconds(5));

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForAllPerspectivesAsync_CancelledDuringWaitAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");

    using var cts = new CancellationTokenSource();

    // Start waiting
    var waitTask = tracker.WaitForAllPerspectivesAsync([eventId], TimeSpan.FromSeconds(5), cancellationToken: cts.Token);

    // Cancel
    cts.Cancel();

    var result = await waitTask;
    await Assert.That(result).IsFalse();
  }

  // ==========================================================================
  // Race condition coverage tests (double-check after registration)
  // ==========================================================================

  [Test]
  public async Task WaitForEventsAsync_RaceConditionFix_SignalsWhenProcessedBeforeRegistrationCompleteAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");

    // Immediately mark processed (simulates race condition)
    var markProcessedTask = Task.Run(async () => {
      await Task.Yield();
      tracker.MarkProcessed([eventId]);
    });

    // Wait should handle the race correctly
    var waitTask = tracker.WaitForEventsAsync([eventId], TimeSpan.FromSeconds(5));

    await markProcessedTask;
    var result = await waitTask;

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForPerspectiveEventsAsync_RaceConditionFix_SignalsWhenProcessedBeforeRegistrationCompleteAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");

    // Immediately mark processed (simulates race condition)
    var markProcessedTask = Task.Run(async () => {
      await Task.Yield();
      tracker.MarkProcessedByPerspective([eventId], "TestPerspective");
    });

    // Wait should handle the race correctly
    var waitTask = tracker.WaitForPerspectiveEventsAsync([eventId], "TestPerspective", TimeSpan.FromSeconds(5));

    await markProcessedTask;
    var result = await waitTask;

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForAllPerspectivesAsync_RaceConditionFix_SignalsWhenProcessedBeforeRegistrationCompleteAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "TestPerspective");

    // Immediately mark processed (simulates race condition)
    var markProcessedTask = Task.Run(async () => {
      await Task.Yield();
      tracker.MarkProcessedByPerspective([eventId], "TestPerspective");
    });

    // Wait should handle the race correctly
    var waitTask = tracker.WaitForAllPerspectivesAsync([eventId], TimeSpan.FromSeconds(5));

    await markProcessedTask;
    var result = await waitTask;

    await Assert.That(result).IsTrue();
  }

  // ==========================================================================
  // TrackEvent with same event for multiple perspectives
  // ==========================================================================

  [Test]
  public async Task TrackEvent_SameEventIdForMultiplePerspectives_TracksEachSeparatelyAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    // Track same event for two perspectives
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "PerspectiveA");
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "PerspectiveB");

    // GetAllTrackedEventIds should return the eventId once (distinct)
    var allIds = tracker.GetAllTrackedEventIds();
    await Assert.That(allIds.Count).IsEqualTo(1);
    await Assert.That(allIds[0]).IsEqualTo(eventId);

    // But GetPendingEvents should show it for each perspective
    var pendingA = tracker.GetPendingEvents(streamId, "PerspectiveA");
    var pendingB = tracker.GetPendingEvents(streamId, "PerspectiveB");

    await Assert.That(pendingA.Count).IsEqualTo(1);
    await Assert.That(pendingB.Count).IsEqualTo(1);
  }

  // ==========================================================================
  // UnregisterAwaiter tests
  // ==========================================================================

  [Test]
  public async Task UnregisterAwaiter_RemovesAllTcsForAwaiterAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "P1");

    var awaiterId = Guid.NewGuid();
    var task = tracker.WaitForPerspectiveEventsAsync(
        [eventId], "P1", TimeSpan.FromSeconds(5), awaiterId);

    // Unregister should cancel the TCS
    tracker.UnregisterAwaiter(awaiterId);

    var result = await task;
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task UnregisterAwaiter_CancelsTcsEntriesAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "P1");

    var awaiterId = Guid.NewGuid();
    var task = tracker.WaitForPerspectiveEventsAsync(
        [eventId], "P1", TimeSpan.FromSeconds(5), awaiterId);

    tracker.UnregisterAwaiter(awaiterId);

    // Task should complete with false (cancellation handled internally)
    var result = await task;
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task UnregisterAwaiter_LeavesOtherAwaitersIntactAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "P1");

    var awaiter1 = Guid.NewGuid();
    var awaiter2 = Guid.NewGuid();

    var task1 = tracker.WaitForPerspectiveEventsAsync(
        [eventId], "P1", TimeSpan.FromSeconds(5), awaiter1);
    var task2 = tracker.WaitForPerspectiveEventsAsync(
        [eventId], "P1", TimeSpan.FromSeconds(5), awaiter2);

    // Only unregister awaiter1
    tracker.UnregisterAwaiter(awaiter1);

    // Signal event
    tracker.MarkProcessedByPerspective([eventId], "P1");

    var result1 = await task1;
    var result2 = await task2;

    await Assert.That(result1).IsFalse().Because("Awaiter 1 was unregistered");
    await Assert.That(result2).IsTrue().Because("Awaiter 2 should complete normally");
  }

  [Test]
  public async Task WaitForPerspectiveEventsAsync_WithAwaiterId_RegistersCorrectlyAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "P1");

    var awaiterId = Guid.NewGuid();
    var task = tracker.WaitForPerspectiveEventsAsync(
        [eventId], "P1", TimeSpan.FromSeconds(5), awaiterId);

    // Complete the event
    tracker.MarkProcessedByPerspective([eventId], "P1");

    var result = await task;
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForPerspectiveEventsAsync_WithNullAwaiterId_AutoGeneratesIdAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "P1");

    // null awaiterId should still work
    var task = tracker.WaitForPerspectiveEventsAsync(
        [eventId], "P1", TimeSpan.FromSeconds(5), awaiterId: null);

    tracker.MarkProcessedByPerspective([eventId], "P1");

    var result = await task;
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task WaitForEventsAsync_WithAwaiterId_CanBeUnregisteredAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "P1");

    var awaiterId = Guid.NewGuid();
    var task = tracker.WaitForEventsAsync([eventId], TimeSpan.FromSeconds(5), awaiterId);

    tracker.UnregisterAwaiter(awaiterId);

    var result = await task;
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task WaitForAllPerspectivesAsync_WithAwaiterId_CanBeUnregisteredAsync() {
    var tracker = new SyncEventTracker();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "P1");

    var awaiterId = Guid.NewGuid();
    var task = tracker.WaitForAllPerspectivesAsync([eventId], TimeSpan.FromSeconds(5), awaiterId);

    tracker.UnregisterAwaiter(awaiterId);

    var result = await task;
    await Assert.That(result).IsFalse();
  }

  // ==========================================================================
  // TTL Cleanup tests
  // ==========================================================================

  [Test]
  public async Task CleanupStaleEntries_RemovesEntriesOlderThanMaxAgeAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "P1");

    // Entry was just added — cleanup with 1-hour TTL should NOT remove it
    var removedCount = tracker.CleanupStaleEntries(TimeSpan.FromHours(1));
    await Assert.That(removedCount).IsEqualTo(0);

    var pending = tracker.GetPendingEvents(streamId, "P1");
    await Assert.That(pending).Count().IsEqualTo(1);
  }

  [Test]
  public async Task CleanupStaleEntries_WithZeroTTL_RemovesAllEntriesAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId1, streamId, "P1");
    tracker.TrackEvent(typeof(TestEventB), eventId2, streamId, "P1");

    // Zero TTL means everything is "stale"
    var removedCount = tracker.CleanupStaleEntries(TimeSpan.Zero);
    await Assert.That(removedCount).IsGreaterThanOrEqualTo(2);

    var allIds = tracker.GetAllTrackedEventIds();
    await Assert.That(allIds).Count().IsEqualTo(0);
  }

  [Test]
  public async Task CleanupStaleEntries_SignalsWaitersForRemovedEntriesAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "P1");

    // Start waiting on the event
    var waitTask = tracker.WaitForPerspectiveEventsAsync([eventId], "P1", TimeSpan.FromSeconds(30));

    // Cleanup with zero TTL — should remove the entry AND signal the waiter
    tracker.CleanupStaleEntries(TimeSpan.Zero);

    // The waiter should complete (signaled by cleanup) rather than waiting 30 seconds
    var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(5)));
    await Assert.That(waitTask.IsCompleted).IsTrue()
      .Because("Cleanup should signal waiters so they don't hang");
  }
}
