using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for InMemoryInbox implementation.
/// Inherits all contract tests from InboxContractTests.
/// </summary>
[InheritsTests]
public class InMemoryInboxTests : InboxContractTests {
  protected override Task<IInbox> CreateInboxAsync() {
    // Use Core.Tests JSON context which includes TestMessage
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    return Task.FromResult<IInbox>(new InMemoryInbox(jsonOptions));
  }

  [Test]
  public async Task CleanupExpiredAsync_WithExpiredRecords_ShouldRemoveThemAsync() {
    // Arrange
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var inbox = new InMemoryInbox(jsonOptions);
    var envelope1 = CreateTestEnvelope();
    var envelope2 = CreateTestEnvelope();

    // Store and mark two messages as processed
    await inbox.StoreAsync(envelope1, "TestHandler");
    await inbox.StoreAsync(envelope2, "TestHandler");
    await inbox.MarkProcessedAsync(envelope1.MessageId);
    await inbox.MarkProcessedAsync(envelope2.MessageId);

    // Wait for them to age
    await Task.Delay(100);

    // Act - cleanup with very short retention (1ms) so both are expired
    await inbox.CleanupExpiredAsync(TimeSpan.FromMilliseconds(1));

    // Assert - both messages should no longer be marked as processed
    var hasProcessed1 = await inbox.HasProcessedAsync(envelope1.MessageId);
    var hasProcessed2 = await inbox.HasProcessedAsync(envelope2.MessageId);

    await Assert.That(hasProcessed1).IsFalse();
    await Assert.That(hasProcessed2).IsFalse();
  }

  [Test]
  public async Task CleanupExpiredAsync_WithMixedRecords_ShouldOnlyRemoveExpiredAsync() {
    // Arrange
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var inbox = new InMemoryInbox(jsonOptions);
    var expiredEnvelope = CreateTestEnvelope();
    var recentEnvelope = CreateTestEnvelope();

    // Store and mark first message as processed
    await inbox.StoreAsync(expiredEnvelope, "TestHandler");
    await inbox.MarkProcessedAsync(expiredEnvelope.MessageId);

    // Wait for it to age
    await Task.Delay(100);

    // Store and mark second message as processed (this one is recent)
    await inbox.StoreAsync(recentEnvelope, "TestHandler");
    await inbox.MarkProcessedAsync(recentEnvelope.MessageId);

    // Act - cleanup with 50ms retention (first message expired, second not)
    await inbox.CleanupExpiredAsync(TimeSpan.FromMilliseconds(50));

    // Assert - expired message should be removed, recent should remain
    var expiredHasProcessed = await inbox.HasProcessedAsync(expiredEnvelope.MessageId);
    var recentHasProcessed = await inbox.HasProcessedAsync(recentEnvelope.MessageId);

    await Assert.That(expiredHasProcessed).IsFalse();
    await Assert.That(recentHasProcessed).IsTrue();
  }

  [Test]
  public async Task CleanupExpiredAsync_WithNoExpiredRecords_ShouldNotRemoveAnythingAsync() {
    // Arrange
    var jsonOptions = WhizbangJsonContext.CreateOptions();
    var inbox = new InMemoryInbox(jsonOptions);
    var envelope = CreateTestEnvelope();

    // Store and mark message as processed
    await inbox.StoreAsync(envelope, "TestHandler");
    await inbox.MarkProcessedAsync(envelope.MessageId);

    // Act - cleanup immediately with 1 hour retention (nothing should expire)
    await inbox.CleanupExpiredAsync(TimeSpan.FromHours(1));

    // Assert - message should still be marked as processed
    var hasProcessed = await inbox.HasProcessedAsync(envelope.MessageId);
    await Assert.That(hasProcessed).IsTrue();
  }

  // Helper method to create test envelope
  private static MessageEnvelope<TestMessage> CreateTestEnvelope() {
    var message = new TestMessage { Value = 42 };
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceName = "TestService",
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
    };
  }
}
