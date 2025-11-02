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
  /// Identifies the logical workflow this message belongs to.
  /// </summary>
  CorrelationId CorrelationId { get; }

  /// <summary>
  /// Identifies the message that caused this message to be created.
  /// </summary>
  CausationId CausationId { get; }

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
}
