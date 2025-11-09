using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

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
    var streamKey = "test-stream";
    var envelope = CreateTestEnvelope("event-1");

    // Act
    await eventStore.AppendAsync(streamKey, envelope);

    // Assert - Read back the event
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync(streamKey, fromSequence: 0)) {
      events.Add(evt);
    }
    await Assert.That(events).HasCount().EqualTo(1);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope.MessageId);
  }

  [Test]
  public async Task AppendAsync_WithNullStreamKey_ShouldThrowAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var envelope = CreateTestEnvelope("event-1");

    // Act & Assert
    await Assert.That(() => eventStore.AppendAsync(null!, envelope))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task AppendAsync_WithNullEnvelope_ShouldThrowAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();

    // Act & Assert
    await Assert.That(() => eventStore.AppendAsync("test-stream", null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamKey = "empty-stream";

    // Act
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync(streamKey, fromSequence: 0)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).HasCount().EqualTo(0);
  }

  [Test]
  public async Task ReadAsync_ShouldReturnEventsInOrderAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamKey = "ordered-stream";
    var envelope1 = CreateTestEnvelope("event-1");
    var envelope2 = CreateTestEnvelope("event-2");
    var envelope3 = CreateTestEnvelope("event-3");

    await eventStore.AppendAsync(streamKey, envelope1);
    await eventStore.AppendAsync(streamKey, envelope2);
    await eventStore.AppendAsync(streamKey, envelope3);

    // Act
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync(streamKey, fromSequence: 0)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).HasCount().EqualTo(3);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope1.MessageId);
    await Assert.That(events[1].MessageId).IsEqualTo(envelope2.MessageId);
    await Assert.That(events[2].MessageId).IsEqualTo(envelope3.MessageId);
  }

  [Test]
  public async Task ReadAsync_FromMiddle_ShouldReturnSubsetAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamKey = "subset-stream";
    await eventStore.AppendAsync(streamKey, CreateTestEnvelope("event-1"));
    await eventStore.AppendAsync(streamKey, CreateTestEnvelope("event-2"));
    var envelope3 = CreateTestEnvelope("event-3");
    await eventStore.AppendAsync(streamKey, envelope3);

    // Act - Read from sequence 2 (third event, 0-indexed)
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync(streamKey, fromSequence: 2)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).HasCount().EqualTo(1);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope3.MessageId);
  }

  [Test]
  public async Task GetLastSequenceAsync_EmptyStream_ShouldReturnMinusOneAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamKey = "new-stream";

    // Act
    var lastSequence = await eventStore.GetLastSequenceAsync(streamKey);

    // Assert
    await Assert.That(lastSequence).IsEqualTo(-1);
  }

  [Test]
  public async Task GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamKey = "sequence-stream";
    await eventStore.AppendAsync(streamKey, CreateTestEnvelope("event-1"));
    await eventStore.AppendAsync(streamKey, CreateTestEnvelope("event-2"));
    await eventStore.AppendAsync(streamKey, CreateTestEnvelope("event-3"));

    // Act
    var lastSequence = await eventStore.GetLastSequenceAsync(streamKey);

    // Assert - Last sequence should be 2 (0-indexed, so 0, 1, 2)
    await Assert.That(lastSequence).IsEqualTo(2);
  }

  [Test]
  public async Task AppendAsync_DifferentStreams_ShouldBeIndependentAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamKey1 = "stream-1";
    var streamKey2 = "stream-2";
    var envelope1 = CreateTestEnvelope("stream-1-event");
    var envelope2 = CreateTestEnvelope("stream-2-event");

    await eventStore.AppendAsync(streamKey1, envelope1);
    await eventStore.AppendAsync(streamKey2, envelope2);

    // Act
    var stream1Events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync(streamKey1, fromSequence: 0)) {
      stream1Events.Add(evt);
    }

    var stream2Events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync(streamKey2, fromSequence: 0)) {
      stream2Events.Add(evt);
    }

    // Assert
    await Assert.That(stream1Events).HasCount().EqualTo(1);
    await Assert.That(stream2Events).HasCount().EqualTo(1);
    await Assert.That(stream1Events[0].MessageId).IsEqualTo(envelope1.MessageId);
    await Assert.That(stream2Events[0].MessageId).IsEqualTo(envelope2.MessageId);
  }

  [Test]
  public async Task AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();
    var streamKey = "concurrent-stream";
    var envelopes = Enumerable.Range(0, 10).Select(i => CreateTestEnvelope($"event-{i}")).ToList();

    // Act - Concurrent appends
    var tasks = envelopes.Select(env =>
      Task.Run(async () => await eventStore.AppendAsync(streamKey, env)));
    await Task.WhenAll(tasks);

    // Assert
    var events = new List<IMessageEnvelope>();
    await foreach (var evt in eventStore.ReadAsync(streamKey, fromSequence: 0)) {
      events.Add(evt);
    }
    await Assert.That(events).HasCount().EqualTo(10);
  }

  [Test]
  public async Task ReadAsync_WithNullStreamKey_ShouldThrowAsync() {
    // Arrange
    var eventStore = await CreateEventStoreAsync();

    // Act & Assert
    await Assert.That(async () => {
      await foreach (var _ in eventStore.ReadAsync(null!, fromSequence: 0)) {
        // Should not reach here
      }
    }).ThrowsExactly<ArgumentNullException>();
  }

  /// <summary>
  /// Helper method to create a test message envelope.
  /// </summary>
  private static IMessageEnvelope CreateTestEnvelope(string payload) {
    return new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = new List<MessageHop>()
    };
  }
}
