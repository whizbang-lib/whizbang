using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Transactional outbox for reliable message publishing.
/// Stores messages to be published and tracks publication status.
/// Enables exactly-once delivery semantics for transports that don't natively support it.
/// </summary>
public interface IOutbox {
  /// <summary>
  /// Stores a message envelope in the outbox for later publication.
  /// Uses the same 3-column JSONB pattern as the event store (event_data, metadata, scope).
  /// Generic for AOT compatibility - allows compile-time type information for JSON serialization.
  /// </summary>
  /// <typeparam name="TMessage">The message payload type (must be registered in JsonSerializerContext)</typeparam>
  /// <param name="envelope">The message envelope to store</param>
  /// <param name="destination">The destination to publish to (topic, queue, etc.)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the message is stored</returns>
  Task StoreAsync<TMessage>(MessageEnvelope<TMessage> envelope, string destination, CancellationToken cancellationToken = default);

  /// <summary>
  /// Stores a message envelope in the outbox for later publication (non-generic overload).
  /// Uses the same 3-column JSONB pattern as the event store (event_data, metadata, scope).
  /// This overload supports dynamic routing scenarios (e.g., BFF.API with 0 local receptors)
  /// where the message type is not known at compile time. AOT-compatible - no reflection.
  /// </summary>
  /// <param name="envelope">The message envelope to store (runtime type information available)</param>
  /// <param name="destination">The destination to publish to (topic, queue, etc.)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the message is stored</returns>
  Task StoreAsync(IMessageEnvelope envelope, string destination, CancellationToken cancellationToken = default);

  /// <summary>
  /// Gets pending messages that have not yet been published.
  /// </summary>
  /// <param name="batchSize">Maximum number of messages to retrieve</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of pending messages to publish</returns>
  Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default);

  /// <summary>
  /// Marks a message as successfully published.
  /// </summary>
  /// <param name="messageId">The message ID to mark as published</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the message is marked as published</returns>
  Task MarkPublishedAsync(MessageId messageId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a message envelope stored in the outbox waiting to be published.
/// Uses 3-column JSONB storage pattern (event_data, metadata, scope) like event store.
/// </summary>
/// <param name="MessageId">The unique message identifier</param>
/// <param name="Destination">The destination to publish to (topic, queue, etc.)</param>
/// <param name="EventType">The fully-qualified type name of the event payload</param>
/// <param name="EventData">The event payload as JSON string</param>
/// <param name="Metadata">The envelope metadata (correlation, causation, hops) as JSON string</param>
/// <param name="Scope">The security scope (tenant, user) as JSON string (nullable)</param>
/// <param name="CreatedAt">When the message was stored in the outbox</param>
public record OutboxMessage(
  MessageId MessageId,
  string Destination,
  string EventType,
  string EventData,
  string Metadata,
  string? Scope,
  DateTimeOffset CreatedAt
);
