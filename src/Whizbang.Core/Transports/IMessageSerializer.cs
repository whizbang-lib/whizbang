using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// Defines serialization/deserialization for message envelopes.
/// Implementations must preserve ALL envelope metadata including:
/// - MessageId, CorrelationId, CausationId
/// - All MessageHops (with Type, metadata, routing info, timestamps)
/// - Policy decision trails
/// - Security contexts
/// - Caller information
/// </summary>
public interface IMessageSerializer {
  /// <summary>
  /// Serializes a message envelope to bytes for network transport.
  /// Must preserve all envelope metadata and message hops.
  /// </summary>
  /// <param name="envelope">The message envelope to serialize</param>
  /// <returns>Byte array representation of the envelope</returns>
  Task<byte[]> SerializeAsync(IMessageEnvelope envelope);

  /// <summary>
  /// Deserializes bytes back into a message envelope.
  /// Must restore all envelope metadata and message hops.
  /// </summary>
  /// <typeparam name="TMessage">The expected payload type</typeparam>
  /// <param name="bytes">Serialized envelope bytes</param>
  /// <returns>Deserialized message envelope with typed payload</returns>
  Task<IMessageEnvelope> DeserializeAsync<TMessage>(byte[] bytes) where TMessage : notnull;
}
