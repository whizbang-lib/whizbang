using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for <see cref="SyncTrackingEventStoreDecorator"/>.
/// </summary>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
public class SyncTrackingEventStoreDecoratorTests {
  // Test event type
  private sealed record TestEvent(string Value) : IEvent;

  // ==========================================================================
  // Constructor tests
  // ==========================================================================

  [Test]
  public async Task Constructor_WithNullInner_ThrowsArgumentNullExceptionAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = new SyncTrackingEventStoreDecorator(null!);
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task Constructor_WithNullTracker_DoesNotThrowAsync() {
    var inner = new InMemoryEventStore();

    var decorator = new SyncTrackingEventStoreDecorator(inner, tracker: null);

    await Assert.That(decorator).IsNotNull();
  }

  // ==========================================================================
  // AppendAsync with envelope tests
  // ==========================================================================

  [Test]
  public async Task AppendAsync_WithEnvelope_TracksEmittedEventAsync() {
    var inner = new InMemoryEventStore();
    var tracker = new ScopedEventTracker();
    var decorator = new SyncTrackingEventStoreDecorator(inner, tracker);

    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = messageId,
      Payload = new TestEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    };

    await decorator.AppendAsync(streamId, envelope);

    var trackedEvents = tracker.GetEmittedEvents();
    await Assert.That(trackedEvents.Count).IsEqualTo(1);
    await Assert.That(trackedEvents[0].StreamId).IsEqualTo(streamId);
    await Assert.That(trackedEvents[0].EventType).IsEqualTo(typeof(TestEvent));
    await Assert.That(trackedEvents[0].EventId).IsEqualTo(messageId.Value);
  }

  [Test]
  public async Task AppendAsync_WithEnvelope_DelegatesToInnerStoreAsync() {
    var inner = new InMemoryEventStore();
    var tracker = new ScopedEventTracker();
    var decorator = new SyncTrackingEventStoreDecorator(inner, tracker);

    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    };

    await decorator.AppendAsync(streamId, envelope);

    // Verify event was stored in inner store
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var e in inner.ReadAsync<TestEvent>(streamId, 0)) {
      events.Add(e);
    }

    await Assert.That(events.Count).IsEqualTo(1);
    await Assert.That(events[0].Payload.Value).IsEqualTo("test");
  }

  [Test]
  public async Task AppendAsync_WithEnvelope_NoTracker_DoesNotThrowAsync() {
    var inner = new InMemoryEventStore();
    var decorator = new SyncTrackingEventStoreDecorator(inner, tracker: null);

    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    };

    // Should not throw
    await decorator.AppendAsync(streamId, envelope);

    // Verify event was still stored
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var e in inner.ReadAsync<TestEvent>(streamId, 0)) {
      events.Add(e);
    }

    await Assert.That(events.Count).IsEqualTo(1);
  }

  // ==========================================================================
  // AppendAsync with message tests
  // ==========================================================================

  [Test]
  public async Task AppendAsync_WithMessage_TracksEmittedEventAsync() {
    var inner = new InMemoryEventStore();
    var tracker = new ScopedEventTracker();
    var decorator = new SyncTrackingEventStoreDecorator(inner, tracker);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test");

    await decorator.AppendAsync(streamId, message);

    var trackedEvents = tracker.GetEmittedEvents();
    await Assert.That(trackedEvents.Count).IsEqualTo(1);
    await Assert.That(trackedEvents[0].StreamId).IsEqualTo(streamId);
    await Assert.That(trackedEvents[0].EventType).IsEqualTo(typeof(TestEvent));
    // EventId will be a new GUID since no envelope registry was provided
    await Assert.That(trackedEvents[0].EventId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task AppendAsync_WithMessage_AndEnvelopeRegistry_UsesRegisteredMessageIdAsync() {
    var envelopeRegistry = new EnvelopeRegistry();
    var inner = new InMemoryEventStore(envelopeRegistry);
    var tracker = new ScopedEventTracker();
    var decorator = new SyncTrackingEventStoreDecorator(inner, tracker, envelopeRegistry);

    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    var message = new TestEvent("test");

    // Register the envelope before appending
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = messageId,
      Payload = message,
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    };
    envelopeRegistry.Register(envelope);

    await decorator.AppendAsync(streamId, message);

    var trackedEvents = tracker.GetEmittedEvents();
    await Assert.That(trackedEvents.Count).IsEqualTo(1);
    await Assert.That(trackedEvents[0].EventId).IsEqualTo(messageId.Value);
  }

  // ==========================================================================
  // Multiple events tests
  // ==========================================================================

  [Test]
  public async Task AppendAsync_MultipleEvents_TracksAllAsync() {
    var inner = new InMemoryEventStore();
    var tracker = new ScopedEventTracker();
    var decorator = new SyncTrackingEventStoreDecorator(inner, tracker);

    var streamId = Guid.NewGuid();

    await decorator.AppendAsync(streamId, new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent("event1"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    });

    await decorator.AppendAsync(streamId, new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent("event2"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    });

    var trackedEvents = tracker.GetEmittedEvents();
    await Assert.That(trackedEvents.Count).IsEqualTo(2);
  }

  // ==========================================================================
  // Read delegation tests
  // ==========================================================================

  [Test]
  public async Task ReadAsync_DelegatesToInnerStoreAsync() {
    var inner = new InMemoryEventStore();
    var tracker = new ScopedEventTracker();
    var decorator = new SyncTrackingEventStoreDecorator(inner, tracker);

    var streamId = Guid.NewGuid();
    await decorator.AppendAsync(streamId, new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    });

    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var e in decorator.ReadAsync<TestEvent>(streamId, 0)) {
      events.Add(e);
    }

    await Assert.That(events.Count).IsEqualTo(1);
    await Assert.That(events[0].Payload.Value).IsEqualTo("test");
  }

  [Test]
  public async Task GetLastSequenceAsync_DelegatesToInnerStoreAsync() {
    var inner = new InMemoryEventStore();
    var tracker = new ScopedEventTracker();
    var decorator = new SyncTrackingEventStoreDecorator(inner, tracker);

    var streamId = Guid.NewGuid();
    await decorator.AppendAsync(streamId, new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    });

    var lastSequence = await decorator.GetLastSequenceAsync(streamId);

    await Assert.That(lastSequence).IsGreaterThanOrEqualTo(0);
  }

  // ==========================================================================
  // ISyncEventTracker integration tests (cross-scope sync)
  // ==========================================================================

  [Test]
  public async Task AppendAsync_WithEnvelope_AndSyncEventTracker_TracksInSingletonAsync() {
    var inner = new InMemoryEventStore();
    var scopedTracker = new ScopedEventTracker();
    var syncEventTracker = new SyncEventTracker();
    var typeRegistry = new TrackedEventTypeRegistry(new Dictionary<Type, string> {
      { typeof(TestEvent), "TestPerspective" }
    });

    var decorator = new SyncTrackingEventStoreDecorator(
        inner,
        scopedTracker,
        envelopeRegistry: null,
        syncEventTracker,
        typeRegistry);

    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = messageId,
      Payload = new TestEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    };

    await decorator.AppendAsync(streamId, envelope);

    // Verify tracked in scoped tracker
    var scopedEvents = scopedTracker.GetEmittedEvents();
    await Assert.That(scopedEvents.Count).IsEqualTo(1);

    // Verify tracked in singleton tracker
    var syncEvents = syncEventTracker.GetPendingEvents(streamId, "TestPerspective");
    await Assert.That(syncEvents.Count).IsEqualTo(1);
    await Assert.That(syncEvents[0].EventId).IsEqualTo(messageId.Value);
    await Assert.That(syncEvents[0].EventType).IsEqualTo(typeof(TestEvent));
    await Assert.That(syncEvents[0].PerspectiveName).IsEqualTo("TestPerspective");
  }

  [Test]
  public async Task AppendAsync_WithMessage_AndSyncEventTracker_TracksInSingletonAsync() {
    var inner = new InMemoryEventStore();
    var syncEventTracker = new SyncEventTracker();
    var typeRegistry = new TrackedEventTypeRegistry(new Dictionary<Type, string> {
      { typeof(TestEvent), "TestPerspective" }
    });

    var decorator = new SyncTrackingEventStoreDecorator(
        inner,
        tracker: null,
        envelopeRegistry: null,
        syncEventTracker,
        typeRegistry);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test");

    await decorator.AppendAsync(streamId, message);

    // Verify tracked in singleton tracker
    var syncEvents = syncEventTracker.GetPendingEvents(streamId, "TestPerspective");
    await Assert.That(syncEvents.Count).IsEqualTo(1);
    await Assert.That(syncEvents[0].EventType).IsEqualTo(typeof(TestEvent));
  }

  [Test]
  public async Task AppendAsync_UnregisteredEventType_DoesNotTrackInSingletonAsync() {
    var inner = new InMemoryEventStore();
    var syncEventTracker = new SyncEventTracker();
    var typeRegistry = new TrackedEventTypeRegistry(new Dictionary<Type, string> {
      // TestEvent is NOT registered
    });

    var decorator = new SyncTrackingEventStoreDecorator(
        inner,
        tracker: null,
        envelopeRegistry: null,
        syncEventTracker,
        typeRegistry);

    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    };

    await decorator.AppendAsync(streamId, envelope);

    // Verify NOT tracked in singleton tracker (type not registered)
    var allTrackedIds = syncEventTracker.GetAllTrackedEventIds();
    await Assert.That(allTrackedIds.Count).IsEqualTo(0);
  }

  [Test]
  public async Task AppendAsync_MultiplePerspectives_TracksForEachAsync() {
    var inner = new InMemoryEventStore();
    var syncEventTracker = new SyncEventTracker();
    var typeRegistry = new TrackedEventTypeRegistry(new Dictionary<Type, string[]> {
      { typeof(TestEvent), ["PerspectiveA", "PerspectiveB", "PerspectiveC"] }
    });

    var decorator = new SyncTrackingEventStoreDecorator(
        inner,
        tracker: null,
        envelopeRegistry: null,
        syncEventTracker,
        typeRegistry);

    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = messageId,
      Payload = new TestEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    };

    await decorator.AppendAsync(streamId, envelope);

    // Verify tracked for each perspective
    var eventsA = syncEventTracker.GetPendingEvents(streamId, "PerspectiveA");
    var eventsB = syncEventTracker.GetPendingEvents(streamId, "PerspectiveB");
    var eventsC = syncEventTracker.GetPendingEvents(streamId, "PerspectiveC");

    await Assert.That(eventsA.Count).IsEqualTo(1);
    await Assert.That(eventsB.Count).IsEqualTo(1);
    await Assert.That(eventsC.Count).IsEqualTo(1);

    // Verify all have the same EventId
    await Assert.That(eventsA[0].EventId).IsEqualTo(messageId.Value);
    await Assert.That(eventsB[0].EventId).IsEqualTo(messageId.Value);
    await Assert.That(eventsC[0].EventId).IsEqualTo(messageId.Value);
  }

  [Test]
  public async Task AppendAsync_NoSyncEventTracker_DoesNotThrowAsync() {
    var inner = new InMemoryEventStore();
    var typeRegistry = new TrackedEventTypeRegistry(new Dictionary<Type, string> {
      { typeof(TestEvent), "TestPerspective" }
    });

    // No syncEventTracker provided
    var decorator = new SyncTrackingEventStoreDecorator(
        inner,
        tracker: null,
        envelopeRegistry: null,
        syncEventTracker: null,
        typeRegistry);

    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    };

    // Should not throw
    await decorator.AppendAsync(streamId, envelope);
  }

  [Test]
  public async Task AppendAsync_NoTypeRegistry_DoesNotThrowAsync() {
    var inner = new InMemoryEventStore();
    var syncEventTracker = new SyncEventTracker();

    // No typeRegistry provided
    var decorator = new SyncTrackingEventStoreDecorator(
        inner,
        tracker: null,
        envelopeRegistry: null,
        syncEventTracker,
        typeRegistry: null);

    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    };

    // Should not throw
    await decorator.AppendAsync(streamId, envelope);

    // Verify NOT tracked (no registry)
    var allTrackedIds = syncEventTracker.GetAllTrackedEventIds();
    await Assert.That(allTrackedIds.Count).IsEqualTo(0);
  }
}
