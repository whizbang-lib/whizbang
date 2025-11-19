using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Test event for OutboxPublisher tests. Must be at namespace level for JSON source generation.
/// </summary>
public record OutboxPublisherTestEvent(string Value) : IEvent;

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
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    var outbox = new InMemoryOutbox(adapter);
    var transport = new FakeTransport();
    var publisher = new OutboxPublisher(outbox, transport);

    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<OutboxPublisherTestEvent> {
      MessageId = messageId,
      Payload = new OutboxPublisherTestEvent("test-value"),
      Hops = []
    };
    envelope.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxPublisherTests" });
    await outbox.StoreAsync(envelope, "test-topic");

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
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    var outbox = new InMemoryOutbox(adapter);
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
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    var outbox = new InMemoryOutbox(adapter);
    var transport = new FakeTransport();
    var publisher = new OutboxPublisher(outbox, transport);

    // Store 5 messages
    for (int i = 0; i < 5; i++) {
      var envelope = new MessageEnvelope<OutboxPublisherTestEvent>(MessageId.New(), new OutboxPublisherTestEvent($"value-{i}"), []);
      envelope.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxPublisherTests" });
      await outbox.StoreAsync(envelope, $"topic-{i}");
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
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    var outbox = new InMemoryOutbox(adapter);
    var transport = new FakeTransport { ShouldFail = true };
    var publisher = new OutboxPublisher(outbox, transport);

    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<OutboxPublisherTestEvent> {
      MessageId = messageId,
      Payload = new OutboxPublisherTestEvent("test-value"),
      Hops = []
    };
    envelope.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxPublisherTests" });
    await outbox.StoreAsync(envelope, "test-topic");

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
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    var outbox = new InMemoryOutbox(adapter);
    var transport = new FakeTransport();
    var publisher = new OutboxPublisher(outbox, transport);

    var id1 = MessageId.New();
    var id2 = MessageId.New();
    var id3 = MessageId.New();

    var envelope1 = new MessageEnvelope<OutboxPublisherTestEvent>(id1, new OutboxPublisherTestEvent("value-1"), []);
    envelope1.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxPublisherTests" });
    var envelope2 = new MessageEnvelope<OutboxPublisherTestEvent>(id2, new OutboxPublisherTestEvent("value-2"), []);
    envelope2.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxPublisherTests" });
    var envelope3 = new MessageEnvelope<OutboxPublisherTestEvent>(id3, new OutboxPublisherTestEvent("value-3"), []);
    envelope3.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxPublisherTests" });

    await outbox.StoreAsync(envelope1, "topic-1");
    await outbox.StoreAsync(envelope2, "topic-2");
    await outbox.StoreAsync(envelope3, "topic-3");

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
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    var outbox = new InMemoryOutbox(adapter);
    var transport = new FakeTransport();
    var publisher = new OutboxPublisher(outbox, transport);

    var id1 = MessageId.New();
    var id2 = MessageId.New();
    var id3 = MessageId.New();

    var envelope1 = new MessageEnvelope<OutboxPublisherTestEvent>(id1, new OutboxPublisherTestEvent("value-1"), []);
    envelope1.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxPublisherTests" });
    var envelope2 = new MessageEnvelope<OutboxPublisherTestEvent>(id2, new OutboxPublisherTestEvent("value-2"), []);
    envelope2.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxPublisherTests" });
    var envelope3 = new MessageEnvelope<OutboxPublisherTestEvent>(id3, new OutboxPublisherTestEvent("value-3"), []);
    envelope3.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxPublisherTests" });

    await outbox.StoreAsync(envelope1, "topic-1");
    await outbox.StoreAsync(envelope2, "fail-topic"); // This will fail
    await outbox.StoreAsync(envelope3, "topic-3");

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
