using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for InMemorySerializer.
/// Note: InMemorySerializer uses GCHandle for in-process testing only.
/// It does NOT perform actual serialization.
/// </summary>
public class InMemorySerializerTests {
  [Test]
  public async Task SerializeAsync_ShouldReturnByteArrayAsync() {
    // Arrange
    var serializer = new InMemorySerializer();
    var envelope = _createTestEnvelope();

    // Act
    var bytes = await serializer.SerializeAsync(envelope);

    // Assert
    await Assert.That(bytes).IsNotNull();
    await Assert.That(bytes.Length).IsEqualTo(sizeof(long)); // IntPtr size
  }

  [Test]
  public async Task DeserializeAsync_ShouldRetrieveOriginalEnvelopeAsync() {
    // Arrange
    var serializer = new InMemorySerializer();
    var originalEnvelope = _createTestEnvelope();

    // Act - Serialize and then deserialize
    var bytes = await serializer.SerializeAsync(originalEnvelope);
    var deserializedEnvelope = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserializedEnvelope).IsSameReferenceAs(originalEnvelope);
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task SerializeDeserialize_RoundTrip_PreservesAllDataAsync() {
    // Arrange
    var serializer = new InMemorySerializer();
    var message = new TestMessage { Content = "test content", Value = 42 };
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Metadata = new Dictionary<string, JsonElement> {
            ["key1"] = JsonSerializer.SerializeToElement("value1"),
            ["key2"] = JsonSerializer.SerializeToElement(123)
          }
        }
      ]
    };

    // Act - Round trip
    var bytes = await serializer.SerializeAsync(envelope);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized).IsSameReferenceAs(envelope);
    var deserializedTyped = deserialized as MessageEnvelope<TestMessage>;
    await Assert.That(deserializedTyped).IsNotNull();
    await Assert.That(deserializedTyped!.Payload.Content).IsEqualTo("test content");
    await Assert.That(deserializedTyped.Payload.Value).IsEqualTo(42);
    await Assert.That(deserializedTyped.Hops).Count().IsEqualTo(1);
  }

  [Test]
  public async Task MultipleSerialization_ShouldProduceDifferentByteArraysAsync() {
    // Arrange
    var serializer = new InMemorySerializer();
    var envelope1 = _createTestEnvelope();
    var envelope2 = _createTestEnvelope();

    // Act
    var bytes1 = await serializer.SerializeAsync(envelope1);
    var bytes2 = await serializer.SerializeAsync(envelope2);

    // Assert - Different envelopes should have different pointer values
    await Assert.That(bytes1.SequenceEqual(bytes2)).IsFalse();
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope() {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
    };
  }
}
