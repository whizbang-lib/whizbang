using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Non-generic interface for message envelopes.
/// Provides access to identity and metadata without requiring knowledge of the payload type.
/// </summary>
public interface IMessageEnvelope {
  /// <summary>
  /// Unique identifier for this specific message.
  /// </summary>
  MessageId MessageId { get; }

  /// <summary>
  /// Hops this message has taken through the system.
  /// </summary>
  List<MessageHop> Hops { get; }

  /// <summary>
  /// Adds a hop to the message's journey.
  /// </summary>
  void AddHop(MessageHop hop);

  /// <summary>
  /// Gets the message timestamp (first hop's timestamp).
  /// </summary>
  DateTimeOffset GetMessageTimestamp();

  /// <summary>
  /// Gets the correlation ID from the first hop.
  /// </summary>
  CorrelationId? GetCorrelationId();

  /// <summary>
  /// Gets the causation ID from the first hop.
  /// </summary>
  MessageId? GetCausationId();
}
