using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for InMemoryInbox implementation.
/// Inherits all contract tests from InboxContractTests.
/// </summary>
[InheritsTests]
public class InMemoryInboxTests : InboxContractTests {
  protected override Task<IInbox> CreateInboxAsync() {
    return Task.FromResult<IInbox>(new InMemoryInbox());
  }

  [Test]
  public async Task CleanupExpiredAsync_WithExpiredRecords_ShouldRemoveThemAsync() {
    // Arrange
    var inbox = new InMemoryInbox();
    var messageId1 = MessageId.New();
    var messageId2 = MessageId.New();

    // Mark two messages as processed
    await inbox.MarkProcessedAsync(messageId1, "TestHandler");
    await inbox.MarkProcessedAsync(messageId2, "TestHandler");

    // Wait for them to age
    await Task.Delay(100);

    // Act - cleanup with very short retention (1ms) so both are expired
    await inbox.CleanupExpiredAsync(TimeSpan.FromMilliseconds(1));

    // Assert - both messages should no longer be marked as processed
    var hasProcessed1 = await inbox.HasProcessedAsync(messageId1);
    var hasProcessed2 = await inbox.HasProcessedAsync(messageId2);

    await Assert.That(hasProcessed1).IsFalse();
    await Assert.That(hasProcessed2).IsFalse();
  }

  [Test]
  public async Task CleanupExpiredAsync_WithMixedRecords_ShouldOnlyRemoveExpiredAsync() {
    // Arrange
    var inbox = new InMemoryInbox();
    var expiredMessageId = MessageId.New();
    var recentMessageId = MessageId.New();

    // Mark first message as processed
    await inbox.MarkProcessedAsync(expiredMessageId, "TestHandler");

    // Wait for it to age
    await Task.Delay(100);

    // Mark second message as processed (this one is recent)
    await inbox.MarkProcessedAsync(recentMessageId, "TestHandler");

    // Act - cleanup with 50ms retention (first message expired, second not)
    await inbox.CleanupExpiredAsync(TimeSpan.FromMilliseconds(50));

    // Assert - expired message should be removed, recent should remain
    var expiredHasProcessed = await inbox.HasProcessedAsync(expiredMessageId);
    var recentHasProcessed = await inbox.HasProcessedAsync(recentMessageId);

    await Assert.That(expiredHasProcessed).IsFalse();
    await Assert.That(recentHasProcessed).IsTrue();
  }

  [Test]
  public async Task CleanupExpiredAsync_WithNoExpiredRecords_ShouldNotRemoveAnythingAsync() {
    // Arrange
    var inbox = new InMemoryInbox();
    var messageId = MessageId.New();

    // Mark message as processed
    await inbox.MarkProcessedAsync(messageId, "TestHandler");

    // Act - cleanup immediately with 1 hour retention (nothing should expire)
    await inbox.CleanupExpiredAsync(TimeSpan.FromHours(1));

    // Assert - message should still be marked as processed
    var hasProcessed = await inbox.HasProcessedAsync(messageId);
    await Assert.That(hasProcessed).IsTrue();
  }
}
