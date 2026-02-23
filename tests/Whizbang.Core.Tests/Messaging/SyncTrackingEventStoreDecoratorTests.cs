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
}
