using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

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
    var serializer = CreateTestSerializer();
    var envelope = CreateTestEnvelope();

    // Act
    var bytes = await serializer.SerializeAsync(envelope);

    // Assert
    await Assert.That(bytes).IsNotNull();
    await Assert.That(bytes.Length).IsGreaterThan(0);
  }

  [Test]
  public async Task Deserialize_WithSerializedEnvelope_ReturnsEnvelopeAsync() {
    // Arrange
    var serializer = CreateTestSerializer();
    var original = CreateTestEnvelope();
    var bytes = await serializer.SerializeAsync(original);

    // Act
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized).IsNotNull();
  }

  [Test]
  public async Task RoundTrip_PreservesMessageIdAsync() {
    // Arrange
    var serializer = CreateTestSerializer();
    var original = CreateTestEnvelope();
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
    var serializer = CreateTestSerializer();
    var message = new TestMessage { Content = "Test Content", Value = 42 };
    var original = CreateTestEnvelope(message);

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
    var serializer = CreateTestSerializer();
    var original = CreateEnvelopeWithMultipleHops();
    var originalHopCount = original.Hops.Count;

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.Hops).HasCount().EqualTo(originalHopCount);
    await Assert.That(deserialized.Hops.Count).IsEqualTo(3); // Original + 2 causation hops
  }

  [Test]
  public async Task RoundTrip_PreservesHopTypeAsync() {
    // Arrange
    var serializer = CreateTestSerializer();
    var original = CreateEnvelopeWithMultipleHops();

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
    var serializer = CreateTestSerializer();
    var correlationId = CorrelationId.New();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "TestService",
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = correlationId
        }
      }
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
    var serializer = CreateTestSerializer();
    var causationId = MessageId.New();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "TestService",
          Timestamp = DateTimeOffset.UtcNow,
          CausationId = causationId
        }
      }
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.GetCausationId()).IsEqualTo(causationId);
  }

  [Test]
  public async Task RoundTrip_PreservesMetadataAsync() {
    // Arrange
    var serializer = CreateTestSerializer();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "TestService",
          Timestamp = DateTimeOffset.UtcNow,
          Metadata = new Dictionary<string, object> {
            ["priority"] = "high",
            ["retry-count"] = 3,
            ["timeout-ms"] = 5000
          }
        }
      }
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.GetMetadata("priority")).IsEqualTo("high");
    await Assert.That(deserialized.GetMetadata("retry-count")).IsEqualTo(3);
    await Assert.That(deserialized.GetMetadata("timeout-ms")).IsEqualTo(5000);
  }

  [Test]
  public async Task RoundTrip_PreservesServiceNameAsync() {
    // Arrange
    var serializer = CreateTestSerializer();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "OrderService",
          Timestamp = DateTimeOffset.UtcNow
        }
      }
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.Hops[0].ServiceName).IsEqualTo("OrderService");
  }

  [Test]
  public async Task RoundTrip_PreservesTopicStreamKeyPartitionAsync() {
    // Arrange
    var serializer = CreateTestSerializer();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "TestService",
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "orders.events",
          StreamKey = "order-123",
          PartitionIndex = 5
        }
      }
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
    var serializer = CreateTestSerializer();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "TestService",
          Timestamp = DateTimeOffset.UtcNow,
          SequenceNumber = 42
        }
      }
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
    var serializer = CreateTestSerializer();
    var timestamp = DateTimeOffset.UtcNow;
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "TestService",
          Timestamp = timestamp
        }
      }
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
    var serializer = CreateTestSerializer();
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "TestService",
          Timestamp = DateTimeOffset.UtcNow,
          Topic = null,
          StreamKey = null,
          Metadata = null
        }
      }
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert - Should not throw, nulls preserved
    var typed = (MessageEnvelope<TestMessage>)deserialized;
    await Assert.That(typed.GetCurrentTopic()).IsNull();
    await Assert.That(typed.GetCurrentStreamKey()).IsNull();
  }

  // Helper methods
  private IMessageSerializer CreateTestSerializer() {
    // This will use JsonMessageSerializer once implemented
    // For now, this will fail compilation - that's expected in RED phase
    return new JsonMessageSerializer();
  }

  private IMessageEnvelope CreateTestEnvelope(TestMessage? message = null) {
    var msg = message ?? new TestMessage { Content = "Test", Value = 1 };
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = msg,
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "TestService",
          Timestamp = DateTimeOffset.UtcNow
        }
      }
    };
  }

  private IMessageEnvelope CreateEnvelopeWithMultipleHops() {
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "Test", Value = 1 },
      Hops = new List<MessageHop> {
        new MessageHop {
          Type = HopType.Current,
          ServiceName = "CurrentService",
          Timestamp = DateTimeOffset.UtcNow
        },
        new MessageHop {
          Type = HopType.Causation,
          ServiceName = "ParentService",
          Timestamp = DateTimeOffset.UtcNow.AddSeconds(-1)
        },
        new MessageHop {
          Type = HopType.Causation,
          ServiceName = "GrandparentService",
          Timestamp = DateTimeOffset.UtcNow.AddSeconds(-2)
        }
      }
    };
    return envelope;
  }

  // Test message type
  private record TestMessage {
    public string Content { get; init; } = string.Empty;
    public int Value { get; init; }
  }
}
