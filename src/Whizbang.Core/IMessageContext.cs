using Whizbang.Core.ValueObjects;

namespace Whizbang.Core;

/// <summary>
/// Provides context and metadata for a message flowing through the system.
/// </summary>
/// <docs>core-concepts/message-context</docs>
public interface IMessageContext {
  /// <summary>
  /// Unique identifier for this specific message.
  /// </summary>
  MessageId MessageId { get; }

  /// <summary>
  /// Identifies the logical workflow this message belongs to.
  /// All messages in a workflow share the same CorrelationId.
  /// </summary>
  CorrelationId CorrelationId { get; }

  /// <summary>
  /// Identifies the message that caused this message to be created.
  /// Forms a causal chain for event sourcing and distributed tracing.
  /// </summary>
  MessageId CausationId { get; }

  /// <summary>
  /// When this message was created.
  /// </summary>
  DateTimeOffset Timestamp { get; }

  /// <summary>
  /// Optional user identifier for authorization and auditing.
  /// </summary>
  string? UserId { get; }

  /// <summary>
  /// Additional metadata for cross-cutting concerns.
  /// </summary>
  IReadOnlyDictionary<string, object> Metadata { get; }
}
