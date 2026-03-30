namespace Whizbang.Core.Messaging;

/// <summary>
/// In-memory channel for events that need to be deferred to the next lifecycle loop.
/// Events queued here will be written to the outbox in the next transaction cycle.
/// </summary>
/// <remarks>
/// <para>This is used when <see cref="IDispatcher.PublishAsync"/> is called outside an active
/// transaction context (e.g., PostPerspective handlers). The channel is process-wide and
/// signals the work coordinator that pending work exists.</para>
/// <para>Thread-safe for concurrent writes from multiple threads.</para>
/// </remarks>
/// <docs>fundamentals/dispatcher/dispatcher#deferred-event-channel</docs>
/// <tests>Whizbang.Core.Tests/Messaging/DeferredOutboxChannelTests.cs</tests>
public interface IDeferredOutboxChannel {
  /// <summary>
  /// Queues a message for deferred outbox write in the next lifecycle loop.
  /// </summary>
  /// <param name="message">The outbox message to queue for deferred processing.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>A ValueTask that completes when the message has been queued.</returns>
  /// <tests>Whizbang.Core.Tests/Messaging/DeferredOutboxChannelTests.cs:QueueAsync_AddsMessageToPending_SuccessfullyAsync</tests>
  /// <tests>Whizbang.Core.Tests/Messaging/DeferredOutboxChannelTests.cs:QueueAsync_MultipleMessages_AllPendingAsync</tests>
  /// <tests>Whizbang.Core.Tests/Messaging/DeferredOutboxChannelTests.cs:QueueAsync_IsThreadSafe_MultipleConcurrentWritesAsync</tests>
  ValueTask QueueAsync(OutboxMessage message, CancellationToken ct = default);

  /// <summary>
  /// Drains all queued messages for processing in the current transaction.
  /// Called by the work coordinator at the start of each lifecycle loop.
  /// </summary>
  /// <returns>A list of all queued messages. The channel is cleared after draining.</returns>
  /// <tests>Whizbang.Core.Tests/Messaging/DeferredOutboxChannelTests.cs:DrainAll_ReturnsAllQueuedMessages_AndClearsChannelAsync</tests>
  /// <tests>Whizbang.Core.Tests/Messaging/DeferredOutboxChannelTests.cs:DrainAll_WhenEmpty_ReturnsEmptyList</tests>
  /// <tests>Whizbang.Core.Tests/Messaging/DeferredOutboxChannelTests.cs:DrainAll_MultipleCalls_OnlyReturnsMessagesOnceAsync</tests>
  IReadOnlyList<OutboxMessage> DrainAll();

  /// <summary>
  /// Gets whether there are pending messages in the channel.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Messaging/DeferredOutboxChannelTests.cs:Constructor_CreatesEmptyChannel_SuccessfullyAsync</tests>
  /// <tests>Whizbang.Core.Tests/Messaging/DeferredOutboxChannelTests.cs:QueueAsync_AddsMessageToPending_SuccessfullyAsync</tests>
  bool HasPending { get; }
}
