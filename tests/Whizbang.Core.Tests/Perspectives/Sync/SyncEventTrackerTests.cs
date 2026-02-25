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
    var perspectiveName = "TestPerspective";
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
    var perspectiveName = "TestPerspective";

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
    var perspectiveName = "TestPerspective";

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
    var perspectiveName = "TestPerspective";

    tracker.TrackEvent(typeof(TestEventA), Guid.NewGuid(), streamId, perspectiveName);
    tracker.TrackEvent(typeof(TestEventB), Guid.NewGuid(), streamId, perspectiveName);

    var pending = tracker.GetPendingEvents(streamId, perspectiveName, eventTypes: null);

    await Assert.That(pending.Count).IsEqualTo(2);
  }

  [Test]
  public async Task GetPendingEvents_EmptyEventTypes_ReturnsAllForStreamAsync() {
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var perspectiveName = "TestPerspective";

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
    var perspectiveName = "TestPerspective";

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
    var perspectiveName = "TestPerspective";

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
}
