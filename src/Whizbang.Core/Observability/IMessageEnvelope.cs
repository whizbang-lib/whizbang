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

  /// <summary>
  /// Gets a metadata value by key from the most recent Current hop.
  /// Searches backwards through hops to find the first HopType.Current hop
  /// that contains the specified key.
  /// </summary>
  /// <param name="key">The metadata key to retrieve</param>
  /// <returns>The metadata value if found, otherwise null</returns>
  object? GetMetadata(string key);

  /// <summary>
  /// Gets the message payload as an object.
  /// </summary>
  /// <returns>The message payload</returns>
  object GetPayload();
}
