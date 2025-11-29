using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Inbox for staging incoming messages from remote services.
/// Mirrors the outbox pattern - remote service's outbox feeds into local service's inbox.
/// Stores messages until they are processed, then marks them as complete.
/// Enables exactly-once processing semantics for transports that don't natively support it.
/// </summary>
public interface IInbox {
  /// <summary>
  /// Stores an incoming message envelope in the inbox for later processing.
  /// Uses the same 3-column JSONB pattern as outbox and event store (event_data, metadata, scope).
  /// Generic for AOT compatibility - allows compile-time type information for JSON serialization.
  /// </summary>
  /// <typeparam name="TMessage">The message payload type (must be registered in JsonSerializerContext)</typeparam>
  /// <param name="envelope">The message envelope to store</param>
  /// <param name="handlerName">The name of the handler that will process this message</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the message is stored</returns>
  Task StoreAsync<TMessage>(MessageEnvelope<TMessage> envelope, string handlerName, CancellationToken cancellationToken = default);

  /// <summary>
  /// Gets pending messages that have not yet been processed.
  /// </summary>
  /// <param name="batchSize">Maximum number of messages to retrieve</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of pending messages to process</returns>
  Task<IReadOnlyList<InboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default);

  /// <summary>
  /// Marks a message as successfully processed.
  /// </summary>
  /// <param name="messageId">The message ID to mark as processed</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the message is marked as processed</returns>
  Task MarkProcessedAsync(MessageId messageId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Checks if a message has already been processed (for deduplication).
  /// </summary>
  /// <param name="messageId">The message ID to check</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>True if the message has been processed, false otherwise</returns>
  Task<bool> HasProcessedAsync(MessageId messageId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Cleans up old processed message records to prevent unbounded growth.
  /// </summary>
  /// <param name="retention">How long to retain processed message records</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when cleanup is finished</returns>
  Task CleanupExpiredAsync(TimeSpan retention, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a message envelope stored in the inbox waiting to be processed.
/// Mirrors OutboxMessage - uses 3-column JSONB storage pattern (event_data, metadata, scope).
/// </summary>
/// <param name="MessageId">The unique message identifier</param>
/// <param name="HandlerName">The name of the handler that will process this message</param>
/// <param name="EventType">The fully-qualified type name of the event payload</param>
/// <param name="EventData">The event payload as JSON string</param>
/// <param name="Metadata">The envelope metadata (correlation, causation, hops) as JSON string</param>
/// <param name="Scope">The security scope (tenant, user) as JSON string (nullable)</param>
/// <param name="ReceivedAt">When the message was received into the inbox</param>
public record InboxMessage(
  MessageId MessageId,
  string HandlerName,
  string EventType,
  string EventData,
  string Metadata,
  string? Scope,
  DateTimeOffset ReceivedAt
);
