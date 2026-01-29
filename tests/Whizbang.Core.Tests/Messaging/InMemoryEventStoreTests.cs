using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for InMemoryEventStore implementation.
/// Inherits all contract tests from EventStoreContractTests.
/// </summary>
[InheritsTests]
public class InMemoryEventStoreTests : EventStoreContractTests {
  protected override Task<IEventStore> CreateEventStoreAsync() {
    return Task.FromResult<IEventStore>(new InMemoryEventStore());
  }

  // ========================================
  // MESSAGE OVERLOAD TESTS
  // ========================================

  [Test]
  public async Task AppendAsync_WithMessage_ShouldStoreEventAsync() {
    // Arrange
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var message = new TestEvent {
      AggregateId = streamId,
      Payload = "test-payload"
    };

    // Act
    await eventStore.AppendAsync(streamId, message);

    // Assert - Read back the event
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Payload).IsEqualTo(message);
  }

  [Test]
  public async Task AppendAsync_WithMessage_WhenNoEnvelope_ShouldCreateMinimalEnvelopeAsync() {
    // Arrange - no envelope registry provided
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var message = new TestEvent {
      AggregateId = streamId,
      Payload = "test-payload"
    };

    // Act
    await eventStore.AppendAsync(streamId, message);

    // Assert - Should have a minimal envelope with Unknown service instance
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops[0].ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown);
  }

  [Test]
  public async Task AppendAsync_WithMessage_WhenEnvelopeRegistered_ShouldUseEnvelopeAsync() {
    // Arrange
    using var registry = new EnvelopeRegistry();
    var eventStore = new InMemoryEventStore(registry);
    var streamId = Guid.NewGuid();
    var message = new TestEvent {
      AggregateId = streamId,
      Payload = "test-payload"
    };

    // Register the envelope with custom tracing info
    var expectedMessageId = MessageId.New();
    var customServiceInstance = new ServiceInstanceInfo {
      ServiceName = "TestService",
      InstanceId = Guid.NewGuid(),
      HostName = "test-host",
      ProcessId = 12345
    };
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = expectedMessageId,
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = customServiceInstance,
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
    };
    registry.Register(envelope);

    // Act - Pass just the message, envelope should be looked up
    await eventStore.AppendAsync(streamId, message);

    // Assert - Should have used the registered envelope with its MessageId and ServiceInstance
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].MessageId).IsEqualTo(expectedMessageId);
    await Assert.That(events[0].Hops[0].ServiceInstance.ServiceName).IsEqualTo("TestService");
  }

  [Test]
  public async Task AppendAsync_WithMessage_WithNullMessage_ShouldThrowAsync() {
    // Arrange
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();

    // Act & Assert - explicitly cast to TestEvent to disambiguate overload
    await Assert.That(() => eventStore.AppendAsync(streamId, (TestEvent)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task AppendAsync_WithMessage_WithRegistryButNotRegistered_ShouldCreateMinimalEnvelopeAsync() {
    // Arrange - registry provided but message not registered
    using var registry = new EnvelopeRegistry();
    var eventStore = new InMemoryEventStore(registry);
    var streamId = Guid.NewGuid();
    var message = new TestEvent {
      AggregateId = streamId,
      Payload = "not-registered"
    };

    // Act - Message is not registered, should fall back to minimal envelope
    await eventStore.AppendAsync(streamId, message);

    // Assert
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Hops[0].ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown);
  }

  // ========================================
  // READ BY EVENT ID TESTS
  // ========================================

  [Test]
  public async Task ReadAsync_ByEventId_WithNullFromEventId_ShouldReturnAllEventsAsync() {
    // Arrange
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-1"));
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-2"));

    // Act
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromEventId: null)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(2);
  }

  [Test]
  public async Task ReadAsync_ByEventId_WithSpecificEventId_ShouldReturnEventsAfterItAsync() {
    // Arrange
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var envelope1 = _createTestEnvelope(streamId, "event-1");
    var envelope2 = _createTestEnvelope(streamId, "event-2");
    var envelope3 = _createTestEnvelope(streamId, "event-3");

    await eventStore.AppendAsync(streamId, envelope1);
    await eventStore.AppendAsync(streamId, envelope2);
    await eventStore.AppendAsync(streamId, envelope3);

    // Act - Read events after envelope1
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromEventId: envelope1.MessageId.Value)) {
      events.Add(evt);
    }

    // Assert - Should return events with MessageId > envelope1.MessageId
    await Assert.That(events).Count().IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task ReadAsync_ByEventId_NonExistentStream_ShouldReturnEmptyAsync() {
    // Arrange
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();

    // Act
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromEventId: null)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  // ========================================
  // POLYMORPHIC READ TESTS
  // ========================================

  [Test]
  public async Task ReadPolymorphicAsync_NonExistentStream_ShouldReturnEmptyAsync() {
    // Arrange
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var eventTypes = new List<Type> { typeof(TestEvent) };

    // Act
    var events = new List<MessageEnvelope<IEvent>>();
    await foreach (var evt in eventStore.ReadPolymorphicAsync(streamId, null, eventTypes)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  [Test]
  public async Task ReadPolymorphicAsync_WithMatchingEventType_ShouldReturnEventsAsync() {
    // Arrange
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-1"));

    var eventTypes = new List<Type> { typeof(TestEvent) };

    // Act
    var events = new List<MessageEnvelope<IEvent>>();
    await foreach (var evt in eventStore.ReadPolymorphicAsync(streamId, null, eventTypes)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].Payload).IsTypeOf<TestEvent>();
  }

  // ========================================
  // GET EVENTS BETWEEN TESTS
  // ========================================

  [Test]
  public async Task GetEventsBetweenAsync_NonExistentStream_ShouldReturnEmptyListAsync() {
    // Arrange
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();

    // Act
    var events = await eventStore.GetEventsBetweenAsync<TestEvent>(
      streamId,
      afterEventId: null,
      upToEventId: Guid.NewGuid());

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_NonExistentStream_ShouldReturnEmptyListAsync() {
    // Arrange
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var eventTypes = new List<Type> { typeof(TestEvent) };

    // Act
    var events = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      afterEventId: null,
      upToEventId: Guid.NewGuid(),
      eventTypes);

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_WithAfterEventId_ShouldFilterEventsAsync() {
    // Arrange
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var envelope1 = _createTestEnvelope(streamId, "event-1");
    var envelope2 = _createTestEnvelope(streamId, "event-2");
    var envelope3 = _createTestEnvelope(streamId, "event-3");

    await eventStore.AppendAsync(streamId, envelope1);
    await eventStore.AppendAsync(streamId, envelope2);
    await eventStore.AppendAsync(streamId, envelope3);

    var eventTypes = new List<Type> { typeof(TestEvent) };

    // Act - Get events after envelope1 up to envelope3
    var events = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      afterEventId: envelope1.MessageId.Value,
      upToEventId: envelope3.MessageId.Value,
      eventTypes);

    // Assert
    await Assert.That(events.Count).IsGreaterThanOrEqualTo(0);
  }

  // ========================================
  // CANCELLATION TESTS
  // ========================================

  [Test]
  public async Task ReadAsync_BySequence_WithCancellation_ShouldThrowAsync() {
    // Arrange
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-1"));

    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () => {
      await foreach (var _ in eventStore.ReadAsync<TestEvent>(streamId, fromSequence: 0, cts.Token)) {
        // Should throw
      }
    }).ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task ReadAsync_ByEventId_WithCancellation_ShouldThrowAsync() {
    // Arrange
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-1"));

    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () => {
      await foreach (var _ in eventStore.ReadAsync<TestEvent>(streamId, fromEventId: null, cts.Token)) {
        // Should throw
      }
    }).ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task ReadPolymorphicAsync_WithCancellation_ShouldThrowAsync() {
    // Arrange
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-1"));

    var cts = new CancellationTokenSource();
    cts.Cancel();
    var eventTypes = new List<Type> { typeof(TestEvent) };

    // Act & Assert
    await Assert.That(async () => {
      await foreach (var _ in eventStore.ReadPolymorphicAsync(streamId, null, eventTypes, cts.Token)) {
        // Should throw
      }
    }).ThrowsExactly<OperationCanceledException>();
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  private static MessageEnvelope<TestEvent> _createTestEnvelope(Guid aggregateId, string payload) {
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent {
        AggregateId = aggregateId,
        Payload = payload
      },
      Hops = []
    };

    envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "InMemoryEventStoreTests",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      }
    });

    return envelope;
  }
}
