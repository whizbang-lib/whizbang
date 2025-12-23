using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Transports.Tests.Generated;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for IMessageSerializer interface.
/// Ensures message serialization preserves ALL envelope metadata including:
/// - MessageId, CorrelationId, CausationId
/// - All MessageHops (causation and current)
/// - Metadata stitching across hops
/// - Policy decision trails
/// - Security contexts
/// - Caller information
/// Following TDD: These tests are written BEFORE the interface implementation.
/// </summary>
public class IMessageSerializerTests {
  [Test]
  public async Task Serialize_WithMessageEnvelope_ReturnsNonEmptyBytesAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var envelope = _createTestEnvelope();

    // Act
    var bytes = await serializer.SerializeAsync(envelope);

    // Assert
    await Assert.That(bytes).IsNotNull();
    await Assert.That(bytes.Length).IsGreaterThan(0);
  }

  [Test]
  public async Task Deserialize_WithSerializedEnvelope_ReturnsEnvelopeAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var original = _createTestEnvelope();
    var bytes = await serializer.SerializeAsync(original);

    // Act
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized).IsNotNull();
  }

  [Test]
  public async Task RoundTrip_PreservesMessageIdAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var original = _createTestEnvelope();
    var originalMessageId = original.MessageId;

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.MessageId).IsEqualTo(originalMessageId);
  }

  [Test]
  public async Task RoundTrip_PreservesPayloadAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var message = new TestMessage { Content = "Test Content", Value = 42 };
    var original = _createTestEnvelope(message);

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    var payload = ((MessageEnvelope<TestMessage>)deserialized).Payload;
    await Assert.That(payload.Content).IsEqualTo("Test Content");
    await Assert.That(payload.Value).IsEqualTo(42);
  }

  [Test]
  public async Task RoundTrip_PreservesAllMessageHopsAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var original = _createEnvelopeWithMultipleHops();
    var originalHopCount = original.Hops.Count;

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.Hops).Count().IsEqualTo(originalHopCount);
    await Assert.That(deserialized.Hops.Count).IsEqualTo(3); // Original + 2 causation hops
  }

  [Test]
  public async Task RoundTrip_PreservesHopTypeAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var original = _createEnvelopeWithMultipleHops();

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert - First hop is Current, others are Causation
    await Assert.That(deserialized.Hops[0].Type).IsEqualTo(HopType.Current);
    await Assert.That(deserialized.Hops[1].Type).IsEqualTo(HopType.Causation);
    await Assert.That(deserialized.Hops[2].Type).IsEqualTo(HopType.Causation);
  }

  [Test]
  public async Task RoundTrip_PreservesCorrelationIdAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var correlationId = CorrelationId.New();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = correlationId
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.GetCorrelationId()).IsEqualTo(correlationId);
  }

  [Test]
  public async Task RoundTrip_PreservesCausationIdAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var causationId = MessageId.New();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          CausationId = causationId
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.GetCausationId()).IsEqualTo(causationId);
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task RoundTrip_PreservesMetadataAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          Metadata = new Dictionary<string, JsonElement> {
            ["priority"] = JsonSerializer.SerializeToElement("high"),
            ["retry-count"] = JsonSerializer.SerializeToElement(3),
            ["timeout-ms"] = JsonSerializer.SerializeToElement(5000)
          }
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.GetMetadata("priority")!.Value.GetString()).IsEqualTo("high");
    await Assert.That(deserialized.GetMetadata("retry-count")!.Value.GetInt32()).IsEqualTo(3);
    await Assert.That(deserialized.GetMetadata("timeout-ms")!.Value.GetInt32()).IsEqualTo(5000);
  }

  [Test]
  public async Task RoundTrip_PreservesServiceNameAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "OrderService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.Hops[0].ServiceInstance.ServiceName).IsEqualTo("OrderService");
  }

  [Test]
  public async Task RoundTrip_PreservesTopicStreamKeyPartitionAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "orders.events",
          StreamKey = "order-123",
          PartitionIndex = 5
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    var typed = (MessageEnvelope<TestMessage>)deserialized;
    await Assert.That(typed.GetCurrentTopic()).IsEqualTo("orders.events");
    await Assert.That(typed.GetCurrentStreamKey()).IsEqualTo("order-123");
    await Assert.That(typed.GetCurrentPartitionIndex()).IsEqualTo(5);
  }

  [Test]
  public async Task RoundTrip_PreservesSequenceNumberAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          SequenceNumber = 42
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    var typed = (MessageEnvelope<TestMessage>)deserialized;
    await Assert.That(typed.GetCurrentSequenceNumber()).IsEqualTo(42);
  }

  [Test]
  public async Task RoundTrip_PreservesTimestampAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var timestamp = DateTimeOffset.UtcNow;
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = timestamp
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.GetMessageTimestamp()).IsEqualTo(timestamp);
  }

  [Test]
  public async Task RoundTrip_WithNullValues_HandlesGracefullyAsync() {
    // Arrange
    var serializer = _createTestSerializer();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          Topic = string.Empty,
          StreamKey = string.Empty,
          Metadata = null
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert - Should not throw, empty strings and nulls preserved
    var typed = (MessageEnvelope<TestMessage>)deserialized;
    await Assert.That(typed.GetCurrentTopic()).IsEqualTo(string.Empty);
    await Assert.That(typed.GetCurrentStreamKey()).IsEqualTo(string.Empty);
  }

  // Helper methods
  private static JsonMessageSerializer _createTestSerializer() {
    // AOT-compatible serializer using WhizbangJsonContext
    // Use CreateOptions to properly configure options with all contexts
    var options = WhizbangJsonContext.CreateOptions();
    return new JsonMessageSerializer(options);
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope(TestMessage? message = null) {
    var msg = message ?? new TestMessage { Content = "Test", Value = 1 };
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = msg,
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
    };
  }

  private static MessageEnvelope<TestMessage> _createEnvelopeWithMultipleHops() {
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "CurrentService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow
        },
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "ParentService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Causation,
          Timestamp = DateTimeOffset.UtcNow.AddSeconds(-1)
        },
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "GrandparentService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Causation,
          Timestamp = DateTimeOffset.UtcNow.AddSeconds(-2)
        }
      ]
    };
    return envelope;
  }
}
