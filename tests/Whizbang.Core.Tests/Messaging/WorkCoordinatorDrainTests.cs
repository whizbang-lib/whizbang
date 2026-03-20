using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for work coordinator draining deferred channel during FlushAsync.
/// </summary>
/// <docs>core-concepts/dispatcher#deferred-event-channel</docs>
public class WorkCoordinatorDrainTests {

  [Test]
  public async Task FlushAsync_DrainsDeferredChannel_IncludesInBatchAsync() {
    // Arrange: Deferred channel has pending messages
    var deferredChannel = new DeferredOutboxChannel();
    var message1 = _createTestOutboxMessage(Guid.NewGuid());
    var message2 = _createTestOutboxMessage(Guid.NewGuid());
    await deferredChannel.QueueAsync(message1);
    await deferredChannel.QueueAsync(message2);

    var workCoordinator = new StubWorkCoordinator();
    var strategy = _createStrategy(workCoordinator, deferredChannel);

    // Act
    _ = await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert: Deferred messages included in batch request
    await Assert.That(workCoordinator.LastRequest).IsNotNull();
    await Assert.That(workCoordinator.LastRequest!.NewOutboxMessages).Count().IsEqualTo(2);
    await Assert.That(workCoordinator.LastRequest!.NewOutboxMessages[0].MessageId).IsEqualTo(message1.MessageId);
    await Assert.That(workCoordinator.LastRequest!.NewOutboxMessages[1].MessageId).IsEqualTo(message2.MessageId);
    // Channel should be empty after drain
    await Assert.That(deferredChannel.HasPending).IsFalse();
  }

  [Test]
  public async Task FlushAsync_NoDeferredChannel_StillWorksAsync() {
    // Arrange: No deferred channel registered (backward compatibility)
    var workCoordinator = new StubWorkCoordinator();
    var strategy = _createStrategy(workCoordinator, deferredChannel: null);

    // Queue a message directly through the strategy
    strategy.QueueOutboxMessage(_createTestOutboxMessage(Guid.NewGuid()));

    // Act
    _ = await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert: Still works without deferred channel
    await Assert.That(workCoordinator.LastRequest).IsNotNull();
    await Assert.That(workCoordinator.LastRequest!.NewOutboxMessages).Count().IsEqualTo(1);
  }

  [Test]
  public async Task FlushAsync_DeferredChannel_EmptyChannel_NoMessagesAddedAsync() {
    // Arrange: Deferred channel is empty
    var deferredChannel = new DeferredOutboxChannel();
    var workCoordinator = new StubWorkCoordinator();
    var strategy = _createStrategy(workCoordinator, deferredChannel);

    // Queue a message directly through the strategy
    var directMessage = _createTestOutboxMessage(Guid.NewGuid());
    strategy.QueueOutboxMessage(directMessage);

    // Act
    _ = await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert: Only the directly queued message
    await Assert.That(workCoordinator.LastRequest).IsNotNull();
    await Assert.That(workCoordinator.LastRequest!.NewOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(workCoordinator.LastRequest!.NewOutboxMessages[0].MessageId).IsEqualTo(directMessage.MessageId);
  }

  [Test]
  public async Task FlushAsync_DeferredAndDirectMessages_AllIncludedAsync() {
    // Arrange: Both deferred and direct messages
    var deferredChannel = new DeferredOutboxChannel();
    var deferredMessage = _createTestOutboxMessage(Guid.NewGuid());
    await deferredChannel.QueueAsync(deferredMessage);

    var workCoordinator = new StubWorkCoordinator();
    var strategy = _createStrategy(workCoordinator, deferredChannel);

    var directMessage = _createTestOutboxMessage(Guid.NewGuid());
    strategy.QueueOutboxMessage(directMessage);

    // Act
    var batch = await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert: Both messages included
    await Assert.That(workCoordinator.LastRequest).IsNotNull();
    await Assert.That(workCoordinator.LastRequest!.NewOutboxMessages).Count().IsEqualTo(2);
    // Deferred messages should come first (prepended)
    var messageIds = workCoordinator.LastRequest!.NewOutboxMessages.Select(m => m.MessageId).ToList();
    await Assert.That(messageIds).Contains(deferredMessage.MessageId);
    await Assert.That(messageIds).Contains(directMessage.MessageId);
  }

  [Test]
  public async Task FlushAsync_MultipleCalls_OnlyDrainsOncePerCallAsync() {
    // Arrange
    var deferredChannel = new DeferredOutboxChannel();
    await deferredChannel.QueueAsync(_createTestOutboxMessage(Guid.NewGuid()));

    var workCoordinator = new StubWorkCoordinator();
    var strategy = _createStrategy(workCoordinator, deferredChannel);

    // Act - first flush
    await strategy.FlushAsync(WorkBatchFlags.None);

    // Queue another deferred message
    await deferredChannel.QueueAsync(_createTestOutboxMessage(Guid.NewGuid()));

    // Act - second flush
    await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert: Second flush got the second message
    await Assert.That(workCoordinator.FlushCount).IsEqualTo(2);
    await Assert.That(workCoordinator.LastRequest!.NewOutboxMessages).Count().IsEqualTo(1);
  }

  // ========================================
  // STUB IMPLEMENTATIONS
  // ========================================

  private sealed class StubWorkCoordinator : IWorkCoordinator {
    public ProcessWorkBatchRequest? LastRequest { get; private set; }
    public int FlushCount { get; private set; }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) {
      LastRequest = request;
      FlushCount++;
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) {
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  private static ImmediateWorkCoordinatorStrategy _createStrategy(
    IWorkCoordinator workCoordinator,
    IDeferredOutboxChannel? deferredChannel) {
    return new ImmediateWorkCoordinatorStrategy(
      workCoordinator,
      new ServiceInstanceProvider(configuration: null),
      new WorkCoordinatorOptions(),
      logger: null,
      scopeFactory: null,
      lifecycleMessageDeserializer: null,
      tracingOptions: null,
      deferredChannel: deferredChannel
    );
  }

  private static OutboxMessage _createTestOutboxMessage(Guid messageId) {
    return new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = _createTestEnvelope(messageId),
      Metadata = new EnvelopeMetadata { MessageId = new MessageId(messageId), Hops = [] },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestEvent]], Whizbang.Core",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      MessageType = "TestEvent"
    };
  }

  private static MessageEnvelope<System.Text.Json.JsonElement> _createTestEnvelope(Guid messageId) {
    var json = System.Text.Json.JsonDocument.Parse("{}").RootElement;
    return new MessageEnvelope<System.Text.Json.JsonElement> {
      MessageId = new MessageId(messageId),
      Payload = json,
      Hops = []
    };
  }
}
