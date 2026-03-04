using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="IScopedEventTracker"/> and <see cref="ScopedEventTracker"/>.
/// </summary>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
public class ScopedEventTrackerTests {
  // ==========================================================================
  // TrackedEvent record tests
  // ==========================================================================

  [Test]
  public async Task TrackedEvent_StoresAllPropertiesAsync() {
    var streamId = Guid.NewGuid();
    var eventType = typeof(string);
    var eventId = Guid.NewGuid();

    var tracked = new TrackedEvent(streamId, eventType, eventId);

    await Assert.That(tracked.StreamId).IsEqualTo(streamId);
    await Assert.That(tracked.EventType).IsEqualTo(eventType);
    await Assert.That(tracked.EventId).IsEqualTo(eventId);
  }

  [Test]
  public async Task TrackedEvent_IsValueTypeAsync() {
    var tracked = new TrackedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());

    await Assert.That(tracked.GetType().IsValueType).IsTrue();
  }

  // ==========================================================================
  // ScopedEventTracker - TrackEmittedEvent tests
  // ==========================================================================

  [Test]
  public async Task ScopedEventTracker_TrackEmittedEvent_AddsToTrackedEventsAsync() {
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    var eventType = typeof(string);
    var eventId = Guid.NewGuid();

    tracker.TrackEmittedEvent(streamId, eventType, eventId);

    var events = tracker.GetEmittedEvents();
    await Assert.That(events.Count).IsEqualTo(1);
    await Assert.That(events[0].StreamId).IsEqualTo(streamId);
    await Assert.That(events[0].EventType).IsEqualTo(eventType);
    await Assert.That(events[0].EventId).IsEqualTo(eventId);
  }

  [Test]
  public async Task ScopedEventTracker_TrackMultipleEvents_AddsAllAsync() {
    var tracker = new ScopedEventTracker();

    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(int), Guid.NewGuid());
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(double), Guid.NewGuid());

    var events = tracker.GetEmittedEvents();
    await Assert.That(events.Count).IsEqualTo(3);
  }

  // ==========================================================================
  // ScopedEventTracker - GetEmittedEvents with filter tests
  // ==========================================================================

  [Test]
  public async Task ScopedEventTracker_GetEmittedEvents_WithStreamFilter_ReturnsMatchingAsync() {
    var tracker = new ScopedEventTracker();
    var targetStreamId = Guid.NewGuid();
    var otherStreamId = Guid.NewGuid();

    tracker.TrackEmittedEvent(targetStreamId, typeof(string), Guid.NewGuid());
    tracker.TrackEmittedEvent(otherStreamId, typeof(string), Guid.NewGuid());
    tracker.TrackEmittedEvent(targetStreamId, typeof(int), Guid.NewGuid());

    var filter = new StreamFilter(targetStreamId);
    var events = tracker.GetEmittedEvents(filter);

    await Assert.That(events.Count).IsEqualTo(2);
    await Assert.That(events.All(e => e.StreamId == targetStreamId)).IsTrue();
  }

  [Test]
  public async Task ScopedEventTracker_GetEmittedEvents_WithEventTypeFilter_ReturnsMatchingAsync() {
    var tracker = new ScopedEventTracker();

    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(int), Guid.NewGuid());
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());

    var filter = new EventTypeFilter([typeof(string)]);
    var events = tracker.GetEmittedEvents(filter);

    await Assert.That(events.Count).IsEqualTo(2);
    await Assert.That(events.All(e => e.EventType == typeof(string))).IsTrue();
  }

  [Test]
  public async Task ScopedEventTracker_GetEmittedEvents_WithCurrentScopeFilter_ReturnsAllAsync() {
    var tracker = new ScopedEventTracker();

    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(int), Guid.NewGuid());

    var filter = new CurrentScopeFilter();
    var events = tracker.GetEmittedEvents(filter);

    await Assert.That(events.Count).IsEqualTo(2);
  }

  [Test]
  public async Task ScopedEventTracker_GetEmittedEvents_WithAllPendingFilter_ReturnsAllAsync() {
    var tracker = new ScopedEventTracker();

    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(int), Guid.NewGuid());

    var filter = new AllPendingFilter();
    var events = tracker.GetEmittedEvents(filter);

    await Assert.That(events.Count).IsEqualTo(2);
  }

  [Test]
  public async Task ScopedEventTracker_GetEmittedEvents_WithAndFilter_ReturnsIntersectionAsync() {
    var tracker = new ScopedEventTracker();
    var targetStreamId = Guid.NewGuid();

    tracker.TrackEmittedEvent(targetStreamId, typeof(string), Guid.NewGuid());
    tracker.TrackEmittedEvent(targetStreamId, typeof(int), Guid.NewGuid());
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());

    var filter = new AndFilter(
        new StreamFilter(targetStreamId),
        new EventTypeFilter([typeof(string)]));
    var events = tracker.GetEmittedEvents(filter);

    await Assert.That(events.Count).IsEqualTo(1);
    await Assert.That(events[0].StreamId).IsEqualTo(targetStreamId);
    await Assert.That(events[0].EventType).IsEqualTo(typeof(string));
  }

  [Test]
  public async Task ScopedEventTracker_GetEmittedEvents_WithOrFilter_ReturnsUnionAsync() {
    var tracker = new ScopedEventTracker();
    var streamId1 = Guid.NewGuid();
    var streamId2 = Guid.NewGuid();

    tracker.TrackEmittedEvent(streamId1, typeof(string), Guid.NewGuid());
    tracker.TrackEmittedEvent(streamId2, typeof(int), Guid.NewGuid());
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(double), Guid.NewGuid());

    var filter = new OrFilter(
        new StreamFilter(streamId1),
        new StreamFilter(streamId2));
    var events = tracker.GetEmittedEvents(filter);

    await Assert.That(events.Count).IsEqualTo(2);
  }

  // ==========================================================================
  // ScopedEventTracker - AreAllProcessed tests
  // ==========================================================================

  [Test]
  public async Task ScopedEventTracker_AreAllProcessed_WithEmptyProcessed_ReturnsFalseAsync() {
    var tracker = new ScopedEventTracker();
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());

    var filter = new AllPendingFilter();
    var processedIds = new HashSet<Guid>();

    var result = tracker.AreAllProcessed(filter, processedIds);

    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ScopedEventTracker_AreAllProcessed_WithAllProcessed_ReturnsTrueAsync() {
    var tracker = new ScopedEventTracker();
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();

    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), eventId1);
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(int), eventId2);

    var filter = new AllPendingFilter();
    var processedIds = new HashSet<Guid> { eventId1, eventId2 };

    var result = tracker.AreAllProcessed(filter, processedIds);

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ScopedEventTracker_AreAllProcessed_WithPartiallyProcessed_ReturnsFalseAsync() {
    var tracker = new ScopedEventTracker();
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();

    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), eventId1);
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(int), eventId2);

    var filter = new AllPendingFilter();
    var processedIds = new HashSet<Guid> { eventId1 }; // Only one processed

    var result = tracker.AreAllProcessed(filter, processedIds);

    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ScopedEventTracker_AreAllProcessed_WithFilteredSubset_ChecksOnlyMatchingAsync() {
    var tracker = new ScopedEventTracker();
    var targetStreamId = Guid.NewGuid();
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();

    tracker.TrackEmittedEvent(targetStreamId, typeof(string), eventId1);
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(int), eventId2); // Different stream

    var filter = new StreamFilter(targetStreamId);
    var processedIds = new HashSet<Guid> { eventId1 }; // Only stream-matching event processed

    var result = tracker.AreAllProcessed(filter, processedIds);

    await Assert.That(result).IsTrue(); // Should be true because only matching events are checked
  }

  [Test]
  public async Task ScopedEventTracker_AreAllProcessed_WithNoMatchingEvents_ReturnsTrueAsync() {
    var tracker = new ScopedEventTracker();
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());

    var nonMatchingStreamId = Guid.NewGuid();
    var filter = new StreamFilter(nonMatchingStreamId);
    var processedIds = new HashSet<Guid>();

    var result = tracker.AreAllProcessed(filter, processedIds);

    await Assert.That(result).IsTrue(); // No events to process
  }

  // ==========================================================================
  // Thread safety tests
  // ==========================================================================

  [Test]
  public async Task ScopedEventTracker_ConcurrentTracking_DoesNotLoseEventsAsync() {
    var tracker = new ScopedEventTracker();
    const int eventCount = 1000;
    var tasks = new List<Task>();

    for (int i = 0; i < eventCount; i++) {
      tasks.Add(Task.Run(() =>
          tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid())));
    }

    await Task.WhenAll(tasks);

    var events = tracker.GetEmittedEvents();
    await Assert.That(events.Count).IsEqualTo(eventCount);
  }
}
