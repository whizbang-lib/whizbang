using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IDeferredOutboxChannel - the in-memory channel for deferred events.
/// Events published outside a transaction context are queued here for the next lifecycle loop.
/// </summary>
/// <docs>core-concepts/dispatcher#deferred-event-channel</docs>
public class DeferredOutboxChannelTests {

  [Test]
  public async Task Constructor_CreatesEmptyChannel_SuccessfullyAsync() {
    // Arrange & Act
    var channel = new DeferredOutboxChannel();

    // Assert
    await Assert.That(channel).IsNotNull();
    await Assert.That(channel.HasPending).IsFalse();
  }

  [Test]
  public async Task QueueAsync_AddsMessageToPending_SuccessfullyAsync() {
    // Arrange
    var channel = new DeferredOutboxChannel();
    var message = _createTestOutboxMessage();

    // Act
    await channel.QueueAsync(message);

    // Assert
    await Assert.That(channel.HasPending).IsTrue();
  }

  [Test]
  public async Task QueueAsync_MultipleMessages_AllPendingAsync() {
    // Arrange
    var channel = new DeferredOutboxChannel();
    var message1 = _createTestOutboxMessage();
    var message2 = _createTestOutboxMessage();
    var message3 = _createTestOutboxMessage();

    // Act
    await channel.QueueAsync(message1);
    await channel.QueueAsync(message2);
    await channel.QueueAsync(message3);

    // Assert
    await Assert.That(channel.HasPending).IsTrue();
    var drained = channel.DrainAll();
    await Assert.That(drained).Count().IsEqualTo(3);
  }

  [Test]
  public async Task DrainAll_ReturnsAllQueuedMessages_AndClearsChannelAsync() {
    // Arrange
    var channel = new DeferredOutboxChannel();
    var message1 = _createTestOutboxMessage();
    var message2 = _createTestOutboxMessage();

    await channel.QueueAsync(message1);
    await channel.QueueAsync(message2);

    // Act
    var drained = channel.DrainAll();

    // Assert
    await Assert.That(drained).Count().IsEqualTo(2);
    await Assert.That(drained).Contains(message1);
    await Assert.That(drained).Contains(message2);
    await Assert.That(channel.HasPending).IsFalse();
  }

  [Test]
  public async Task DrainAll_WhenEmpty_ReturnsEmptyListAsync() {
    // Arrange
    var channel = new DeferredOutboxChannel();

    // Act
    var drained = channel.DrainAll();

    // Assert
    await Assert.That(drained).Count().IsEqualTo(0);
  }

  [Test]
  public async Task DrainAll_MultipleCalls_OnlyReturnsMessagesOnceAsync() {
    // Arrange
    var channel = new DeferredOutboxChannel();
    var message = _createTestOutboxMessage();
    await channel.QueueAsync(message);

    // Act - first drain
    var firstDrain = channel.DrainAll();

    // Act - second drain (should be empty)
    var secondDrain = channel.DrainAll();

    // Assert
    await Assert.That(firstDrain).Count().IsEqualTo(1);
    await Assert.That(secondDrain).Count().IsEqualTo(0);
    await Assert.That(channel.HasPending).IsFalse();
  }

  [Test]
  public async Task QueueAsync_IsThreadSafe_MultipleConcurrentWritesAsync() {
    // Arrange
    var channel = new DeferredOutboxChannel();
    const int messageCount = 100;

    // Act - concurrent writes from multiple threads
    var tasks = Enumerable.Range(0, messageCount)
      .Select(_ => channel.QueueAsync(_createTestOutboxMessage()).AsTask());
    await Task.WhenAll(tasks);

    // Assert
    await Assert.That(channel.HasPending).IsTrue();
    var drained = channel.DrainAll();
    await Assert.That(drained).Count().IsEqualTo(messageCount);
  }

  [Test]
  public async Task DrainAll_IsThreadSafe_ConcurrentDrainAndWriteAsync() {
    // Arrange
    var channel = new DeferredOutboxChannel();
    const int writeCount = 50;

    // Pre-populate some messages
    for (int i = 0; i < writeCount; i++) {
      await channel.QueueAsync(_createTestOutboxMessage());
    }

    // Act - concurrent drain and write operations
    var drainTask = Task.Run(() => channel.DrainAll());
    var writeTask = Task.Run(async () => {
      for (int i = 0; i < writeCount; i++) {
        await channel.QueueAsync(_createTestOutboxMessage());
      }
    });

    await Task.WhenAll(drainTask, writeTask);

    // Assert - total messages should equal write count (initial drained + written again)
    // This tests that no messages are lost during concurrent operations
    var firstDrainCount = drainTask.Result.Count;
    var remainingMessages = channel.DrainAll();

    // First drain should have gotten some messages, remaining should have others
    // Total should be writeCount * 2 (initial + concurrent writes)
    await Assert.That(firstDrainCount + remainingMessages.Count).IsEqualTo(writeCount * 2);
  }

  [Test]
  public async Task QueueAsync_PreservesMessageOrder_FIFOAsync() {
    // Arrange
    var channel = new DeferredOutboxChannel();
    var messages = new List<OutboxMessage>();

    for (int i = 0; i < 10; i++) {
      var msg = _createTestOutboxMessage(Guid.NewGuid()); // Use distinct message IDs
      messages.Add(msg);
      await channel.QueueAsync(msg);
    }

    // Act
    var drained = channel.DrainAll();

    // Assert - messages should be in FIFO order
    await Assert.That(drained).Count().IsEqualTo(10);
    for (int i = 0; i < 10; i++) {
      await Assert.That(drained[i].MessageId).IsEqualTo(messages[i].MessageId);
    }
  }

  /// <summary>
  /// Creates a test OutboxMessage with required properties populated.
  /// </summary>
  private static OutboxMessage _createTestOutboxMessage(Guid? messageId = null) {
    var id = messageId ?? Guid.NewGuid();
    return new OutboxMessage {
      MessageId = id,
      Destination = "test-topic",
      Envelope = _createTestEnvelope(id),
      Metadata = new EnvelopeMetadata { MessageId = new MessageId(id), Hops = [] },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestEvent]], Whizbang.Core",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      MessageType = "TestEvent"
    };
  }

  /// <summary>
  /// Creates a test envelope for the OutboxMessage.
  /// </summary>
  private static MessageEnvelope<System.Text.Json.JsonElement> _createTestEnvelope(Guid messageId) {
    var json = System.Text.Json.JsonDocument.Parse("{}").RootElement;
    return new MessageEnvelope<System.Text.Json.JsonElement> {
      MessageId = new MessageId(messageId),
      Payload = json,
      Hops = []
    };
  }
}
