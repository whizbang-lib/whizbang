using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:Serialize_WithMessageEnvelope_ReturnsNonEmptyBytesAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:Deserialize_WithSerializedEnvelope_ReturnsEnvelopeAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesMessageIdAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesPayloadAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesAllMessageHopsAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesHopTypeAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesCorrelationIdAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesCausationIdAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesMetadataAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesServiceNameAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesTopicStreamKeyPartitionAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesSequenceNumberAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesTimestampAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_WithNullValues_HandlesGracefullyAsync</tests>
/// Defines serialization/deserialization for message envelopes.
/// Implementations must preserve ALL envelope metadata including:
/// - MessageId, CorrelationId, CausationId
/// - All MessageHops (with Type, metadata, routing info, timestamps)
/// - Policy decision trails
/// - Security contexts
/// - Caller information
/// </summary>
/// <docs>components/transports</docs>
public interface IMessageSerializer {
  /// <summary>
  /// Serializes a message envelope to bytes for network transport.
  /// Must preserve all envelope metadata and message hops.
  /// </summary>
  /// <param name="envelope">The message envelope to serialize</param>
  /// <returns>Byte array representation of the envelope</returns>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:Serialize_WithMessageEnvelope_ReturnsNonEmptyBytesAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesMessageIdAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesPayloadAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesAllMessageHopsAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesHopTypeAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesCorrelationIdAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesCausationIdAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesMetadataAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesServiceNameAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesTopicStreamKeyPartitionAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesSequenceNumberAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesTimestampAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_WithNullValues_HandlesGracefullyAsync</tests>
  Task<byte[]> SerializeAsync(IMessageEnvelope envelope);

  /// <summary>
  /// Deserializes bytes back into a message envelope.
  /// Must restore all envelope metadata and message hops.
  /// </summary>
  /// <typeparam name="TMessage">The expected payload type</typeparam>
  /// <param name="bytes">Serialized envelope bytes</param>
  /// <returns>Deserialized message envelope with typed payload</returns>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:Deserialize_WithSerializedEnvelope_ReturnsEnvelopeAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesMessageIdAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesPayloadAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesAllMessageHopsAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesHopTypeAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesCorrelationIdAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesCausationIdAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesMetadataAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesServiceNameAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesTopicStreamKeyPartitionAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesSequenceNumberAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_PreservesTimestampAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/IMessageSerializerTests.cs:RoundTrip_WithNullValues_HandlesGracefullyAsync</tests>
  Task<IMessageEnvelope> DeserializeAsync<TMessage>(byte[] bytes) where TMessage : notnull;
}
