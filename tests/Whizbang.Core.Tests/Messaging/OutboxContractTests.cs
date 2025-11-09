using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

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
    var payload = new byte[] { 1, 2, 3 };

    // Act
    await outbox.StoreAsync(messageId, destination, payload);
    var pending = await outbox.GetPendingAsync(batchSize: 10);

    // Assert
    await Assert.That(pending).HasCount().EqualTo(1);
    await Assert.That(pending[0].MessageId).IsEqualTo(messageId);
    await Assert.That(pending[0].Destination).IsEqualTo(destination);
    await Assert.That(pending[0].Payload).IsEqualTo(payload);
  }

  [Test]
  public async Task StoreAsync_WithNullDestination_ShouldThrowAsync() {
    // Arrange
    var outbox = await CreateOutboxAsync();
    var messageId = MessageId.New();
    var payload = new byte[] { 1, 2, 3 };

    // Act & Assert
    await Assert.That(() => outbox.StoreAsync(messageId, null!, payload))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task StoreAsync_WithNullPayload_ShouldThrowAsync() {
    // Arrange
    var outbox = await CreateOutboxAsync();
    var messageId = MessageId.New();

    // Act & Assert
    await Assert.That(() => outbox.StoreAsync(messageId, "test-topic", null!))
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
      await outbox.StoreAsync(MessageId.New(), $"topic-{i}", new byte[] { (byte)i });
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
    await outbox.StoreAsync(messageId, "test-topic", new byte[] { 1, 2, 3 });

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
    await outbox.StoreAsync(messageId1, "topic-1", new byte[] { 1 });
    await outbox.StoreAsync(messageId2, "topic-2", new byte[] { 2 });
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
    var tasks = messageIds.Select(id =>
      Task.Run(async () => await outbox.StoreAsync(id, "test-topic", new byte[] { 1, 2, 3 })));
    await Task.WhenAll(tasks);

    // Assert
    var pending = await outbox.GetPendingAsync(batchSize: 100);
    await Assert.That(pending).HasCount().EqualTo(10);
  }
}
