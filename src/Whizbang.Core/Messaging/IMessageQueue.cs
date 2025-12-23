using System;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Message queue for distributed inbox pattern with atomic enqueue-and-lease.
/// Provides exactly-once processing across multiple service instances.
/// ServiceBus consumers enqueue-and-lease messages, process them, then delete.
/// If processing fails, lease expires and another instance can retry.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IMessageQueueTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Queue is the correct semantic name for this message queue interface")]
public interface IMessageQueue {
  /// <summary>
  /// Atomically enqueues and leases a message for this instance.
  /// Returns true if message was newly enqueued and leased (not already processed).
  /// Returns false if message was already processed (idempotency check passed).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IMessageQueueTests.cs:EnqueueAndLeaseAsync_NewMessage_ReturnsTrue_AndLeasesMessageAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IMessageQueueTests.cs:EnqueueAndLeaseAsync_DuplicateMessage_ReturnsFalse_IdempotencyCheckAsync</tests>
  Task<bool> EnqueueAndLeaseAsync(
    QueuedMessage message,
    string instanceId,
    TimeSpan leaseDuration,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Atomically marks message as processed and removes from queue.
  /// Called after successful processing.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IMessageQueueTests.cs:CompleteAsync_WithLeasedMessage_RemovesFromQueueAsync</tests>
  Task CompleteAsync(
    Guid messageId,
    string instanceId,
    string handlerName,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Fallback: Leases orphaned messages (no lease or expired lease) for crash recovery.
  /// Used by InboxProcessorWorker to handle messages from crashed instances.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IMessageQueueTests.cs:LeaseOrphanedMessagesAsync_WithExpiredLeases_ClaimsMessagesAsync</tests>
  Task<System.Collections.Generic.IReadOnlyList<QueuedMessage>> LeaseOrphanedMessagesAsync(
    string instanceId,
    int maxCount,
    TimeSpan leaseDuration,
    CancellationToken cancellationToken = default);
}

/// <summary>
/// A message queued for processing.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/QueuedMessageTests.cs</tests>
public record QueuedMessage {
  public required Guid MessageId { get; init; }
  public required string EventType { get; init; }
  public required string EventData { get; init; }  // JSON-serialized event
  public string? Metadata { get; init; }  // JSON-serialized envelope metadata
}
