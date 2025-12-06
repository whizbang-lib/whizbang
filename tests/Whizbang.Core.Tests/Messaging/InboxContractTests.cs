using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Test message type for inbox contract tests.
/// </summary>
public record TestMessage : IEvent {
  public int Value { get; init; }
}

/// <summary>
/// Contract tests for IInbox interface.
/// All implementations of IInbox must pass these tests.
/// Tests the new inbox pattern that mirrors IOutbox (staging + processing).
/// </summary>
[Category("Messaging")]
public abstract class InboxContractTests {
  /// <summary>
  /// Derived test classes must provide a factory method to create an IInbox instance.
  /// </summary>
  protected abstract Task<IInbox> CreateInboxAsync();

  [Test]
  public async Task StoreAsync_WithValidEnvelope_StoresMessageAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var envelope = CreateTestEnvelope();
    var handlerName = "TestHandler";

    // Act
    await inbox.StoreAsync(envelope, handlerName);

    // Assert - Should be in pending state
    var pending = await inbox.GetPendingAsync(10);
    await Assert.That(pending).HasCount().EqualTo(1);
    await Assert.That(pending[0].MessageId).IsEqualTo(envelope.MessageId);
    await Assert.That(pending[0].HandlerName).IsEqualTo(handlerName);
  }

  [Test]
  public async Task StoreAsync_WithNullEnvelope_ShouldThrowAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();

    // Act & Assert
    await Assert.That(() => inbox.StoreAsync<TestMessage>(null!, "TestHandler"))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task StoreAsync_WithNullHandlerName_ShouldThrowAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var envelope = CreateTestEnvelope();

    // Act & Assert
    await Assert.That(() => inbox.StoreAsync(envelope, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task GetPendingAsync_WithNoMessages_ReturnsEmptyListAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();

    // Act
    var pending = await inbox.GetPendingAsync(10);

    // Assert
    await Assert.That(pending).IsEmpty();
  }

  [Test]
  public async Task GetPendingAsync_WithPendingMessages_ReturnsMessagesAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var envelope1 = CreateTestEnvelope();
    var envelope2 = CreateTestEnvelope();
    await inbox.StoreAsync(envelope1, "Handler1");
    await inbox.StoreAsync(envelope2, "Handler2");

    // Act
    var pending = await inbox.GetPendingAsync(10);

    // Assert
    await Assert.That(pending).HasCount().EqualTo(2);
  }

  [Test]
  public async Task GetPendingAsync_RespectsBatchSizeAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    await inbox.StoreAsync(CreateTestEnvelope(), "Handler1");
    await inbox.StoreAsync(CreateTestEnvelope(), "Handler2");
    await inbox.StoreAsync(CreateTestEnvelope(), "Handler3");

    // Act
    var pending = await inbox.GetPendingAsync(2);

    // Assert
    await Assert.That(pending).HasCount().EqualTo(2);
  }

  [Test]
  public async Task GetPendingAsync_ExcludesProcessedMessagesAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var envelope1 = CreateTestEnvelope();
    var envelope2 = CreateTestEnvelope();
    await inbox.StoreAsync(envelope1, "Handler1");
    await inbox.StoreAsync(envelope2, "Handler2");
    await inbox.MarkProcessedAsync(envelope1.MessageId);

    // Act
    var pending = await inbox.GetPendingAsync(10);

    // Assert
    await Assert.That(pending).HasCount().EqualTo(1);
    await Assert.That(pending[0].MessageId).IsEqualTo(envelope2.MessageId);
  }

  [Test]
  public async Task MarkProcessedAsync_UpdatesMessageStateAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var envelope = CreateTestEnvelope();
    await inbox.StoreAsync(envelope, "TestHandler");

    // Act
    await inbox.MarkProcessedAsync(envelope.MessageId);

    // Assert - Should not be in pending anymore
    var pending = await inbox.GetPendingAsync(10);
    await Assert.That(pending).IsEmpty();

    // Assert - Should be marked as processed
    await Assert.That(await inbox.HasProcessedAsync(envelope.MessageId)).IsTrue();
  }

  [Test]
  public async Task MarkProcessedAsync_WithNonExistentMessage_ShouldNotThrowAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var nonExistentId = MessageId.New();

    // Act & Assert - Should not throw
    await inbox.MarkProcessedAsync(nonExistentId);
  }

  [Test]
  public async Task HasProcessedAsync_ReturnsFalse_WhenMessageNotProcessedAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var envelope = CreateTestEnvelope();
    await inbox.StoreAsync(envelope, "TestHandler");

    // Act
    var hasProcessed = await inbox.HasProcessedAsync(envelope.MessageId);

    // Assert
    await Assert.That(hasProcessed).IsFalse();
  }

  [Test]
  public async Task HasProcessedAsync_ReturnsTrue_AfterMarkingProcessedAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var envelope = CreateTestEnvelope();
    await inbox.StoreAsync(envelope, "TestHandler");
    await inbox.MarkProcessedAsync(envelope.MessageId);

    // Act
    var hasProcessed = await inbox.HasProcessedAsync(envelope.MessageId);

    // Assert
    await Assert.That(hasProcessed).IsTrue();
  }

  [Test]
  public async Task HasProcessedAsync_ReturnsFalse_ForNonExistentMessageAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var nonExistentId = MessageId.New();

    // Act
    var hasProcessed = await inbox.HasProcessedAsync(nonExistentId);

    // Assert
    await Assert.That(hasProcessed).IsFalse();
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
    var envelope = CreateTestEnvelope();
    await inbox.StoreAsync(envelope, "TestHandler");
    await inbox.MarkProcessedAsync(envelope.MessageId);

    // Act - Cleanup with long retention (won't delete recent messages)
    await inbox.CleanupExpiredAsync(TimeSpan.FromDays(365));

    // Assert - Message should still be marked as processed
    await Assert.That(await inbox.HasProcessedAsync(envelope.MessageId)).IsTrue();
  }

  [Test]
  public async Task StoreAsync_DuplicateMessageId_ShouldBeIdempotentAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var envelope = CreateTestEnvelope();

    // Act - Store same envelope twice
    await inbox.StoreAsync(envelope, "Handler1");
    await inbox.StoreAsync(envelope, "Handler2");

    // Assert - Should only have one pending message
    var pending = await inbox.GetPendingAsync(10);
    await Assert.That(pending).HasCount().EqualTo(1);
  }

  [Test]
  public async Task MarkProcessedAsync_ConcurrentCalls_ShouldBeThreadSafeAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var envelope = CreateTestEnvelope();
    await inbox.StoreAsync(envelope, "TestHandler");

    // Act - Concurrent marking (simulating race condition)
    var tasks = Enumerable.Range(0, 10)
      .Select(_ => Task.Run(async () => await inbox.MarkProcessedAsync(envelope.MessageId)));
    await Task.WhenAll(tasks);

    // Assert - Should be marked as processed
    await Assert.That(await inbox.HasProcessedAsync(envelope.MessageId)).IsTrue();

    // Assert - Should not be in pending
    var pending = await inbox.GetPendingAsync(10);
    await Assert.That(pending).IsEmpty();
  }

  [Test]
  public async Task HasProcessedAsync_MultipleConcurrentChecks_ShouldBeConsistentAsync() {
    // Arrange
    var inbox = await CreateInboxAsync();
    var envelope = CreateTestEnvelope();
    await inbox.StoreAsync(envelope, "TestHandler");
    await inbox.MarkProcessedAsync(envelope.MessageId);

    // Act - Concurrent reads
    var tasks = Enumerable.Range(0, 10)
      .Select(_ => Task.Run(async () => await inbox.HasProcessedAsync(envelope.MessageId)));
    var results = await Task.WhenAll(tasks);

    // Assert - All reads should return true
    await Assert.That(results.All(r => r)).IsTrue();
  }

  // Helper method to create test envelope
  private static MessageEnvelope<TestMessage> CreateTestEnvelope() {
    var message = new TestMessage { Value = 42 };
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
    };
  }
}
