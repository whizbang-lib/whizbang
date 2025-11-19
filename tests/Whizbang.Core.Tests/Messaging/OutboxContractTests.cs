using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Test event type for outbox contract tests. Must be at namespace level for JSON source generation.
/// </summary>
public record OutboxTestEvent(string Value) : IEvent;

/// <summary>
/// Contract tests for IOutbox interface.
/// All implementations of IOutbox must pass these tests.
/// </summary>
[Category("Messaging")]
public abstract class OutboxContractTests {
  /// <summary>
  /// Derived test classes must provide a factory method to create an IOutbox instance.
  /// </summary>
  protected abstract Task<IOutbox> CreateOutboxAsync();

  [Test]
  public async Task StoreAsync_ShouldStorePendingMessageAsync() {
    // Arrange
    var outbox = await CreateOutboxAsync();
    var messageId = MessageId.New();
    var destination = "test-topic";
    var testEvent = new OutboxTestEvent("test-value");
    var envelope = new MessageEnvelope<OutboxTestEvent>(messageId, testEvent, []);
    envelope.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxContractTests" });

    // Act
    await outbox.StoreAsync(envelope, destination);
    var pending = await outbox.GetPendingAsync(batchSize: 10);

    // Assert
    await Assert.That(pending).HasCount().EqualTo(1);
    await Assert.That(pending[0].MessageId).IsEqualTo(messageId);
    await Assert.That(pending[0].Destination).IsEqualTo(destination);
    await Assert.That(pending[0].EventType).IsEqualTo(typeof(OutboxTestEvent).FullName);
  }

  [Test]
  public async Task StoreAsync_WithNullDestination_ShouldThrowAsync() {
    // Arrange
    var outbox = await CreateOutboxAsync();
    var testEvent = new OutboxTestEvent("test-value");
    var envelope = new MessageEnvelope<OutboxTestEvent>(MessageId.New(), testEvent, []);
    envelope.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxContractTests" });

    // Act & Assert
    await Assert.That(() => outbox.StoreAsync(envelope, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task StoreAsync_WithNullEnvelope_ShouldThrowAsync() {
    // Arrange
    var outbox = await CreateOutboxAsync();

    // Act & Assert
    await Assert.That(() => outbox.StoreAsync(null!, "test-topic"))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task GetPendingAsync_WhenEmpty_ShouldReturnEmptyListAsync() {
    // Arrange
    var outbox = await CreateOutboxAsync();

    // Act
    var pending = await outbox.GetPendingAsync(batchSize: 10);

    // Assert
    await Assert.That(pending).HasCount().EqualTo(0);
  }

  [Test]
  public async Task GetPendingAsync_ShouldRespectBatchSizeAsync() {
    // Arrange
    var outbox = await CreateOutboxAsync();
    for (int i = 0; i < 5; i++) {
      var envelope = new MessageEnvelope<OutboxTestEvent>(MessageId.New(), new OutboxTestEvent($"value-{i}"), []);
      envelope.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxContractTests" });
      await outbox.StoreAsync(envelope, $"topic-{i}");
    }

    // Act
    var pending = await outbox.GetPendingAsync(batchSize: 3);

    // Assert
    await Assert.That(pending.Count).IsLessThanOrEqualTo(3);
  }

  [Test]
  public async Task MarkPublishedAsync_ShouldRemoveFromPendingAsync() {
    // Arrange
    var outbox = await CreateOutboxAsync();
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<OutboxTestEvent>(messageId, new OutboxTestEvent("test-value"), []);
    envelope.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxContractTests" });
    await outbox.StoreAsync(envelope, "test-topic");

    // Act
    await outbox.MarkPublishedAsync(messageId);
    var pending = await outbox.GetPendingAsync(batchSize: 10);

    // Assert
    await Assert.That(pending).HasCount().EqualTo(0);
  }

  [Test]
  public async Task MarkPublishedAsync_NonExistentMessage_ShouldNotThrowAsync() {
    // Arrange
    var outbox = await CreateOutboxAsync();
    var messageId = MessageId.New();

    // Act & Assert - Should be idempotent
    await outbox.MarkPublishedAsync(messageId);
  }

  [Test]
  public async Task GetPendingAsync_ShouldNotReturnPublishedMessagesAsync() {
    // Arrange
    var outbox = await CreateOutboxAsync();
    var messageId1 = MessageId.New();
    var messageId2 = MessageId.New();
    var envelope1 = new MessageEnvelope<OutboxTestEvent>(messageId1, new OutboxTestEvent("value-1"), []);
    envelope1.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxContractTests" });
    var envelope2 = new MessageEnvelope<OutboxTestEvent>(messageId2, new OutboxTestEvent("value-2"), []);
    envelope2.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxContractTests" });
    await outbox.StoreAsync(envelope1, "topic-1");
    await outbox.StoreAsync(envelope2, "topic-2");
    await outbox.MarkPublishedAsync(messageId1);

    // Act
    var pending = await outbox.GetPendingAsync(batchSize: 10);

    // Assert
    await Assert.That(pending).HasCount().EqualTo(1);
    await Assert.That(pending[0].MessageId).IsEqualTo(messageId2);
  }

  [Test]
  public async Task StoreAsync_ConcurrentCalls_ShouldBeThreadSafeAsync() {
    // Arrange
    var outbox = await CreateOutboxAsync();
    var messageIds = Enumerable.Range(0, 10).Select(_ => MessageId.New()).ToList();

    // Act - Concurrent stores
    var tasks = messageIds.Select(id => {
      var envelope = new MessageEnvelope<OutboxTestEvent>(id, new OutboxTestEvent("test-value"), []);
      envelope.AddHop(new MessageHop { Type = HopType.Current, ServiceName = "OutboxContractTests" });
      return Task.Run(async () => await outbox.StoreAsync(envelope, "test-topic"));
    });
    await Task.WhenAll(tasks);

    // Assert
    var pending = await outbox.GetPendingAsync(batchSize: 100);
    await Assert.That(pending).HasCount().EqualTo(10);
  }
}
