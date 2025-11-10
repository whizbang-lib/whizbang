using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for OutboxPublisher - background service that publishes pending outbox messages.
/// Polls the outbox, publishes messages via transport, and marks them as published.
/// </summary>
[Category("Messaging")]
[Category("Outbox")]
public class OutboxPublisherTests {
  [Test]
  public async Task PublishPendingAsync_WithPendingMessages_PublishesAndMarksCompletedAsync() {
    // Arrange
    var outbox = new InMemoryOutbox();
    var transport = new FakeTransport();
    var publisher = new OutboxPublisher(outbox, transport);

    var messageId = MessageId.New();
    await outbox.StoreAsync(messageId, "test-topic", new byte[] { 1, 2, 3 });

    // Act
    await publisher.PublishPendingAsync(batchSize: 10);

    // Assert - Message was published
    await Assert.That(transport.PublishedMessages).HasCount().EqualTo(1);
    await Assert.That(transport.PublishedMessages[0].Destination).IsEqualTo("test-topic");

    // Assert - Message marked as published in outbox
    var pending = await outbox.GetPendingAsync(10);
    await Assert.That(pending).HasCount().EqualTo(0);
  }

  [Test]
  public async Task PublishPendingAsync_WithNoPendingMessages_DoesNothingAsync() {
    // Arrange
    var outbox = new InMemoryOutbox();
    var transport = new FakeTransport();
    var publisher = new OutboxPublisher(outbox, transport);

    // Act
    await publisher.PublishPendingAsync(batchSize: 10);

    // Assert
    await Assert.That(transport.PublishedMessages).HasCount().EqualTo(0);
  }

  [Test]
  public async Task PublishPendingAsync_RespectsBatchSizeAsync() {
    // Arrange
    var outbox = new InMemoryOutbox();
    var transport = new FakeTransport();
    var publisher = new OutboxPublisher(outbox, transport);

    // Store 5 messages
    for (int i = 0; i < 5; i++) {
      await outbox.StoreAsync(MessageId.New(), $"topic-{i}", new byte[] { (byte)i });
    }

    // Act - Request only 3
    await publisher.PublishPendingAsync(batchSize: 3);

    // Assert - Only 3 published
    await Assert.That(transport.PublishedMessages.Count).IsLessThanOrEqualTo(3);

    // Assert - Remaining messages still pending
    var pending = await outbox.GetPendingAsync(10);
    await Assert.That(pending.Count).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  public async Task PublishPendingAsync_WhenTransportFails_DoesNotMarkAsPublishedAsync() {
    // Arrange
    var outbox = new InMemoryOutbox();
    var transport = new FakeTransport { ShouldFail = true };
    var publisher = new OutboxPublisher(outbox, transport);

    var messageId = MessageId.New();
    await outbox.StoreAsync(messageId, "test-topic", new byte[] { 1, 2, 3 });

    // Act - Should not throw, but log/handle error
    await publisher.PublishPendingAsync(batchSize: 10);

    // Assert - Message NOT marked as published
    var pending = await outbox.GetPendingAsync(10);
    await Assert.That(pending).HasCount().EqualTo(1);
    await Assert.That(pending[0].MessageId).IsEqualTo(messageId);
  }

  [Test]
  public async Task PublishPendingAsync_WithMultipleMessages_PublishesAllAsync() {
    // Arrange
    var outbox = new InMemoryOutbox();
    var transport = new FakeTransport();
    var publisher = new OutboxPublisher(outbox, transport);

    var id1 = MessageId.New();
    var id2 = MessageId.New();
    var id3 = MessageId.New();

    await outbox.StoreAsync(id1, "topic-1", new byte[] { 1 });
    await outbox.StoreAsync(id2, "topic-2", new byte[] { 2 });
    await outbox.StoreAsync(id3, "topic-3", new byte[] { 3 });

    // Act
    await publisher.PublishPendingAsync(batchSize: 10);

    // Assert - All messages published (order not guaranteed with ConcurrentDictionary)
    await Assert.That(transport.PublishedMessages).HasCount().EqualTo(3);
    var destinations = transport.PublishedMessages.Select(m => m.Destination).ToList();
    await Assert.That(destinations).Contains("topic-1");
    await Assert.That(destinations).Contains("topic-2");
    await Assert.That(destinations).Contains("topic-3");
  }

  [Test]
  public async Task PublishPendingAsync_WithPartialFailure_OnlyMarksSuccessfulAsync() {
    // Arrange
    var outbox = new InMemoryOutbox();
    var transport = new FakeTransport();
    var publisher = new OutboxPublisher(outbox, transport);

    var id1 = MessageId.New();
    var id2 = MessageId.New();
    var id3 = MessageId.New();

    await outbox.StoreAsync(id1, "topic-1", new byte[] { 1 });
    await outbox.StoreAsync(id2, "fail-topic", new byte[] { 2 }); // This will fail
    await outbox.StoreAsync(id3, "topic-3", new byte[] { 3 });

    // Configure transport to fail on specific destination
    transport.FailOnDestination = "fail-topic";

    // Act
    await publisher.PublishPendingAsync(batchSize: 10);

    // Assert - Only failed message remains pending
    var pending = await outbox.GetPendingAsync(10);
    await Assert.That(pending).HasCount().EqualTo(1);
    await Assert.That(pending[0].MessageId).IsEqualTo(id2);
  }

  /// <summary>
  /// Fake transport for testing outbox publisher.
  /// </summary>
  private class FakeTransport : ITransport {
    public List<PublishedMessage> PublishedMessages { get; } = new();
    public bool ShouldFail { get; set; }
    public string? FailOnDestination { get; set; }

    public TransportCapabilities Capabilities => new();

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, CancellationToken cancellationToken = default) {
      if (ShouldFail || destination.Address == FailOnDestination) {
        throw new InvalidOperationException("Transport failed");
      }

      PublishedMessages.Add(new PublishedMessage(envelope, destination.Address));
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull
      where TResponse : notnull {
      throw new NotImplementedException();
    }
  }

  private record PublishedMessage(IMessageEnvelope Envelope, string Destination);
}
