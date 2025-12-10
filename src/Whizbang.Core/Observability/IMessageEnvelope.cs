using System.Text.Json;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Non-generic base interface for message envelopes.
/// Provides access to identity, payload (as object), hops, and metadata without requiring knowledge of the payload type.
/// Use this for heterogeneous collections of envelopes with different payload types.
/// Use <see cref="IMessageEnvelope{TMessage}"/> when you need strongly-typed access to the payload.
/// </summary>
public interface IMessageEnvelope {
  /// <summary>
  /// Unique identifier for this specific message.
  /// </summary>
  MessageId MessageId { get; }

  /// <summary>
  /// The message payload as an object.
  /// For strongly-typed access, use <see cref="IMessageEnvelope{TMessage}.Payload"/>.
  /// </summary>
  object Payload { get; }

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
  /// <returns>The JsonElement metadata value if found, otherwise null</returns>
  JsonElement? GetMetadata(string key);
}

/// <summary>
/// Generic interface for message envelopes with strong typing.
/// Extends <see cref="IMessageEnvelope"/> to add strongly-typed access to the payload.
/// The 'out' modifier enables covariance for the payload type.
/// </summary>
/// <typeparam name="TMessage">The type of the message payload (covariant)</typeparam>
public interface IMessageEnvelope<out TMessage> : IMessageEnvelope {
  /// <summary>
  /// The message payload with strong type information.
  /// Hides the base interface's object Payload property to provide strong typing.
  /// </summary>
  new TMessage Payload { get; }
}
