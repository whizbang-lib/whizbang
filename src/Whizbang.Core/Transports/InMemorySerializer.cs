using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// <tests>tests/Whizbang.Transports.Tests/InMemorySerializerTests.cs:SerializeAsync_ShouldReturnByteArrayAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InMemorySerializerTests.cs:DeserializeAsync_ShouldRetrieveOriginalEnvelopeAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InMemorySerializerTests.cs:SerializeDeserialize_RoundTrip_PreservesAllDataAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/InMemorySerializerTests.cs:MultipleSerialization_ShouldProduceDifferentByteArraysAsync</tests>
/// In-memory serializer for testing purposes.
/// Does NOT perform actual serialization - stores envelope references directly.
/// Should only be used for in-process testing, NOT for network transport.
/// </summary>
public class InMemorySerializer : IMessageSerializer {
  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/InMemorySerializerTests.cs:SerializeAsync_ShouldReturnByteArrayAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InMemorySerializerTests.cs:SerializeDeserialize_RoundTrip_PreservesAllDataAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InMemorySerializerTests.cs:MultipleSerialization_ShouldProduceDifferentByteArraysAsync</tests>
  public Task<byte[]> SerializeAsync(IMessageEnvelope envelope) {
    // Store the envelope reference as a "byte array" (not real serialization)
    // This is only safe for in-process testing
    var handle = GCHandle.Alloc(envelope);
    var ptr = GCHandle.ToIntPtr(handle);
    return Task.FromResult(BitConverter.GetBytes(ptr.ToInt64()));
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/InMemorySerializerTests.cs:DeserializeAsync_ShouldRetrieveOriginalEnvelopeAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/InMemorySerializerTests.cs:SerializeDeserialize_RoundTrip_PreservesAllDataAsync</tests>
  public Task<IMessageEnvelope> DeserializeAsync<TMessage>(byte[] bytes) where TMessage : notnull {
    // Retrieve the envelope reference from the "byte array"
    var ptr = new IntPtr(BitConverter.ToInt64(bytes, 0));
    var handle = GCHandle.FromIntPtr(ptr);
    var envelope = (IMessageEnvelope)handle.Target!;
    handle.Free();
    return Task.FromResult(envelope);
  }
}
