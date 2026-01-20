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
}
