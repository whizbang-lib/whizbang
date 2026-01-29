using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Test event with AggregateId for stream ID inference.
/// </summary>
public record TestEvent : IEvent {
  [StreamKey]
  [AggregateId]
  public required Guid AggregateId { get; init; }
  public required string Payload { get; init; }
}

/// <summary>
/// Contract tests for IEventStore interface.
/// All implementations of IEventStore must pass these tests.
/// </summary>
[Category("Messaging")]
public abstract class EventStoreContractTests {

  /// <summary>
  /// Derived test classes must provide a factory method to create an IEventStore instance.
  /// </summary>
  protected abstract Task<IEventStore> CreateEventStoreAsync();

  [Test]
  public async Task AppendAsync_ShouldStoreEventAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();
    var envelope = _createTestEnvelope(streamId, "event-1");

    // Act
    await eventStore.AppendAsync(streamId, envelope);

    // Assert - Read back the event
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope.MessageId);
  }

  [Test]
  public async Task AppendAsync_WithNullEnvelope_ShouldThrowAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();

    // Act & Assert - explicitly cast to MessageEnvelope<TestEvent> to disambiguate overload
    await Assert.That(() => eventStore.AppendAsync(streamId, (MessageEnvelope<TestEvent>)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();

    // Act
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  [Test]
  public async Task ReadAsync_ShouldReturnEventsInOrderAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();
    var envelope1 = _createTestEnvelope(streamId, "event-1");
    var envelope2 = _createTestEnvelope(streamId, "event-2");
    var envelope3 = _createTestEnvelope(streamId, "event-3");

    await eventStore.AppendAsync(streamId, envelope1);
    await eventStore.AppendAsync(streamId, envelope2);
    await eventStore.AppendAsync(streamId, envelope3);

    // Act
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(3);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope1.MessageId);
    await Assert.That(events[1].MessageId).IsEqualTo(envelope2.MessageId);
    await Assert.That(events[2].MessageId).IsEqualTo(envelope3.MessageId);
  }

  [Test]
  public async Task ReadAsync_FromMiddle_ShouldReturnSubsetAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-1"));
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-2"));
    var envelope3 = _createTestEnvelope(streamId, "event-3");
    await eventStore.AppendAsync(streamId, envelope3);

    // Act - Read from sequence 2 (third event, 0-indexed)
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromSequence: 2)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope3.MessageId);
  }

  [Test]
  public async Task GetLastSequenceAsync_EmptyStream_ShouldReturnMinusOneAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();

    // Act
    var lastSequence = await eventStore.GetLastSequenceAsync(streamId);

    // Assert
    await Assert.That(lastSequence).IsEqualTo(-1);
  }

  [Test]
  public async Task GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-1"));
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-2"));
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-3"));

    // Act
    var lastSequence = await eventStore.GetLastSequenceAsync(streamId);

    // Assert - Last sequence should be 2 (0-indexed, so 0, 1, 2)
    await Assert.That(lastSequence).IsEqualTo(2);
  }

  [Test]
  public async Task AppendAsync_DifferentStreams_ShouldBeIndependentAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId1 = Guid.NewGuid();
    var streamId2 = Guid.NewGuid();
    var envelope1 = _createTestEnvelope(streamId1, "stream-1-event");
    var envelope2 = _createTestEnvelope(streamId2, "stream-2-event");

    await eventStore.AppendAsync(streamId1, envelope1);
    await eventStore.AppendAsync(streamId2, envelope2);

    // Act
    var stream1Events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId1, fromSequence: 0)) {
      stream1Events.Add(evt);
    }

    var stream2Events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId2, fromSequence: 0)) {
      stream2Events.Add(evt);
    }

    // Assert
    await Assert.That(stream1Events).Count().IsEqualTo(1);
    await Assert.That(stream2Events).Count().IsEqualTo(1);
    await Assert.That(stream1Events[0].MessageId).IsEqualTo(envelope1.MessageId);
    await Assert.That(stream2Events[0].MessageId).IsEqualTo(envelope2.MessageId);
  }

  [Test]
  public async Task AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();
    var envelopes = Enumerable.Range(0, 10).Select(i => _createTestEnvelope(streamId, $"event-{i}")).ToList();

    // Act - Concurrent appends
    var tasks = envelopes.Select(env =>
      Task.Run(async () => await eventStore.AppendAsync(streamId, env)));
    await Task.WhenAll(tasks);

    // Assert
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }
    await Assert.That(events).Count().IsEqualTo(10);
  }

  // ========================================
  // EVENT ID BASED READ TESTS
  // ========================================

  [Test]
  public async Task ReadAsync_ByEventId_FromNull_ShouldReturnAllEventsAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-1"));
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-2"));
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-3"));

    // Act - Read with null fromEventId
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromEventId: null)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(3);
  }

  [Test]
  public async Task ReadAsync_ByEventId_FromSpecificEvent_ShouldReturnEventsAfterItAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();
    var envelope1 = _createTestEnvelope(streamId, "event-1");
    var envelope2 = _createTestEnvelope(streamId, "event-2");
    var envelope3 = _createTestEnvelope(streamId, "event-3");

    await eventStore.AppendAsync(streamId, envelope1);
    await eventStore.AppendAsync(streamId, envelope2);
    await eventStore.AppendAsync(streamId, envelope3);

    // Act - Read from after envelope1
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync<TestEvent>(streamId, fromEventId: envelope1.MessageId.Value)) {
      events.Add(evt);
    }

    // Assert - Should return events after envelope1
    await Assert.That(events).Count().IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task ReadAsync_ByEventId_EmptyStream_ShouldReturnEmptyAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();

    // Act
    var events = new List<IMessageEnvelope>();
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
  public virtual async Task ReadPolymorphicAsync_ShouldReturnIEventEnvelopesAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();

    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-1"));
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-2"));

    // Act
    var eventTypes = new List<Type> { typeof(TestEvent) };
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadPolymorphicAsync(streamId, fromEventId: null, eventTypes)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(2);
  }

  [Test]
  public virtual async Task ReadPolymorphicAsync_EmptyStream_ShouldReturnEmptyAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();

    // Act
    var eventTypes = new List<Type> { typeof(TestEvent) };
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadPolymorphicAsync(streamId, fromEventId: null, eventTypes)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  // ========================================
  // GET EVENTS BETWEEN TESTS
  // ========================================

  [Test]
  public virtual async Task GetEventsBetweenAsync_ShouldReturnEventsInRangeAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();
    var envelope1 = _createTestEnvelope(streamId, "event-1");
    var envelope2 = _createTestEnvelope(streamId, "event-2");
    var envelope3 = _createTestEnvelope(streamId, "event-3");

    await eventStore.AppendAsync(streamId, envelope1);
    await eventStore.AppendAsync(streamId, envelope2);
    await eventStore.AppendAsync(streamId, envelope3);

    // Act - Get events up to envelope2
    var events = await eventStore.GetEventsBetweenAsync<TestEvent>(
      streamId,
      afterEventId: null,
      upToEventId: envelope2.MessageId.Value);

    // Assert - Should return events up to and including envelope2
    await Assert.That(events).Count().IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public virtual async Task GetEventsBetweenAsync_EmptyStream_ShouldReturnEmptyListAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
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
  public virtual async Task GetEventsBetweenPolymorphicAsync_ShouldReturnIEventEnvelopesAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();
    var envelope1 = _createTestEnvelope(streamId, "event-1");
    var envelope2 = _createTestEnvelope(streamId, "event-2");

    await eventStore.AppendAsync(streamId, envelope1);
    await eventStore.AppendAsync(streamId, envelope2);

    // Act
    var eventTypes = new List<Type> { typeof(TestEvent) };
    var events = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      afterEventId: null,
      upToEventId: envelope2.MessageId.Value,
      eventTypes);

    // Assert
    await Assert.That(events).Count().IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public virtual async Task GetEventsBetweenPolymorphicAsync_EmptyStream_ShouldReturnEmptyListAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();

    // Act
    var eventTypes = new List<Type> { typeof(TestEvent) };
    var events = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      afterEventId: null,
      upToEventId: Guid.NewGuid(),
      eventTypes);

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  [Test]
  public virtual async Task GetEventsBetweenPolymorphicAsync_WithNullEventTypes_ShouldThrowAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();

    // Act & Assert
    await Assert.That(async () => await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      afterEventId: null,
      upToEventId: Guid.NewGuid(),
      eventTypes: null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  /// <summary>
  /// Helper method to create a test message envelope with TestEvent payload.
  /// </summary>
  private static MessageEnvelope<TestEvent> _createTestEnvelope(Guid aggregateId, string payload) {
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent {
        AggregateId = aggregateId,
        Payload = payload
      },
      Hops = []
    };

    // Add the first hop (dispatch hop)
    envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "EventStoreContractTests",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      }
    });

    return envelope;
  }
}
