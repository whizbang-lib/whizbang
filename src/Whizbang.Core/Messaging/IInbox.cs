using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Deduplicates incoming messages to ensure exactly-once processing.
/// Stores processed message IDs to prevent duplicate handling.
/// Used by transports that don't natively support exactly-once semantics (RabbitMQ, Event Hubs).
/// </summary>
public interface IInbox {
  /// <summary>
  /// Checks if a message has already been processed.
  /// </summary>
  /// <param name="messageId">The message ID to check</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>True if the message has been processed, false otherwise</returns>
  Task<bool> HasProcessedAsync(MessageId messageId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Marks a message as processed to prevent future duplicate processing.
  /// </summary>
  /// <param name="messageId">The message ID to mark as processed</param>
  /// <param name="handlerName">The name of the handler that processed the message</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the message is marked as processed</returns>
  Task MarkProcessedAsync(MessageId messageId, string handlerName, CancellationToken cancellationToken = default);

  /// <summary>
  /// Cleans up expired/old processed message records to prevent unbounded growth.
  /// </summary>
  /// <param name="retention">How long to retain processed message records</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when cleanup is finished</returns>
  Task CleanupExpiredAsync(TimeSpan retention, CancellationToken cancellationToken = default);
}
