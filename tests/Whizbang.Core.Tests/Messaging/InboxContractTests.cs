using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Contract tests for IInbox interface.
/// All implementations of IInbox must pass these tests.
/// </summary>
[Category("Messaging")]
public abstract class InboxContractTests {
  /// <summary>
  /// Derived test classes must provide a factory method to create an IInbox instance.
  /// </summary>
  protected abstract Task<IInbox> CreateInboxAsync();

  [Test]
  public async Task HasProcessedAsync_ReturnsFalse_WhenMessageNotProcessedAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var messageId = MessageId.New();

    // Act
    var hasProcessed = await inbox.HasProcessedAsync(messageId);

    // Assert
    await Assert.That(hasProcessed).IsFalse();
  }

  [Test]
  public async Task HasProcessedAsync_ReturnsTrue_AfterMarkingProcessedAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var messageId = MessageId.New();
    await inbox.MarkProcessedAsync(messageId, "TestHandler");

    // Act
    var hasProcessed = await inbox.HasProcessedAsync(messageId);

    // Assert
    await Assert.That(hasProcessed).IsTrue();
  }

  [Test]
  public async Task MarkProcessedAsync_WithNullHandlerName_ShouldThrowAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var messageId = MessageId.New();

    // Act & Assert
    await Assert.That(() => inbox.MarkProcessedAsync(messageId, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task MarkProcessedAsync_SameMessageTwice_ShouldNotThrowAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var messageId = MessageId.New();

    // Act - Mark same message twice (idempotent)
    await inbox.MarkProcessedAsync(messageId, "TestHandler");
    await inbox.MarkProcessedAsync(messageId, "TestHandler");

    // Assert - Should still be marked as processed
    await Assert.That(await inbox.HasProcessedAsync(messageId)).IsTrue();
  }

  [Test]
  public async Task HasProcessedAsync_DifferentMessages_ShouldBeIndependentAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var messageId1 = MessageId.New();
    var messageId2 = MessageId.New();
    await inbox.MarkProcessedAsync(messageId1, "TestHandler");

    // Act
    var hasProcessed1 = await inbox.HasProcessedAsync(messageId1);
    var hasProcessed2 = await inbox.HasProcessedAsync(messageId2);

    // Assert
    await Assert.That(hasProcessed1).IsTrue();
    await Assert.That(hasProcessed2).IsFalse();
  }

  [Test]
  public async Task CleanupExpiredAsync_ShouldNotThrowAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var retention = TimeSpan.FromDays(7);

    // Act & Assert - Should not throw
    await inbox.CleanupExpiredAsync(retention);
  }

  [Test]
  public async Task CleanupExpiredAsync_ShouldRetainRecentMessagesAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var messageId = MessageId.New();
    await inbox.MarkProcessedAsync(messageId, "TestHandler");

    // Act - Cleanup with long retention (won't delete recent messages)
    await inbox.CleanupExpiredAsync(TimeSpan.FromDays(365));

    // Assert - Message should still be marked as processed
    await Assert.That(await inbox.HasProcessedAsync(messageId)).IsTrue();
  }

  [Test]
  public async Task MarkProcessedAsync_ConcurrentCalls_ShouldBeThreadSafeAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var messageId = MessageId.New();

    // Act - Concurrent marking (simulating race condition)
    var tasks = Enumerable.Range(0, 10)
      .Select(_ => Task.Run(async () => await inbox.MarkProcessedAsync(messageId, "TestHandler")));
    await Task.WhenAll(tasks);

    // Assert - Should be marked exactly once
    await Assert.That(await inbox.HasProcessedAsync(messageId)).IsTrue();
  }

  [Test]
  public async Task HasProcessedAsync_MultipleConcurrentChecks_ShouldBeConsistentAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var messageId = MessageId.New();
    await inbox.MarkProcessedAsync(messageId, "TestHandler");

    // Act - Concurrent reads
    var tasks = Enumerable.Range(0, 10)
      .Select(_ => Task.Run(async () => await inbox.HasProcessedAsync(messageId)));
    var results = await Task.WhenAll(tasks);

    // Assert - All reads should return true
    await Assert.That(results.All(r => r)).IsTrue();
  }
}
