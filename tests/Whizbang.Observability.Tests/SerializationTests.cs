using System.Text.Json;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for JSON serialization/deserialization of message envelope and related types using AOT-compatible WhizbangJsonContext.
/// Ensures proper cross-network communication with minimal payload size.
/// </summary>
public class SerializationTests {
  public record TestMessage(string Value, int Count) : ICommand;

  private readonly JsonSerializerOptions _jsonOptions = WhizbangJsonContext.CreateOptions();

  #region MessageEnvelope Serialization Tests

  [Test]
  public async Task MessageEnvelope_SerializesAndDeserializes_WithSimplePayloadAsync() {
    // Arrange
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test", 42),
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "Origin",
          Topic = "test-topic",
          Timestamp = DateTimeOffset.UtcNow
        }
      }
    };

    // Act
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<TestMessage>));
    var json = JsonSerializer.Serialize(original, typeInfo);
    var deserialized = JsonSerializer.Deserialize(json, typeInfo) as MessageEnvelope<TestMessage>;

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.MessageId).IsEqualTo(original.MessageId);
    await Assert.That(deserialized!.GetCorrelationId()).IsEqualTo(original.GetCorrelationId());
    await Assert.That(deserialized!.Payload.Value).IsEqualTo("test");
    await Assert.That(deserialized!.Payload.Count).IsEqualTo(42);
  }

  [Test]
  public async Task MessageEnvelope_SerializesAndDeserializes_WithMultipleHopsAsync() {
    // Arrange
    var hop1 = new MessageHop {
      ServiceName = "Service1",
      Topic = "orders",
      Timestamp = DateTimeOffset.UtcNow
    };
    var hop2 = new MessageHop {
      ServiceName = "Service2",
      StreamKey = "order-123",
      Timestamp = DateTimeOffset.UtcNow.AddSeconds(1)
    };

    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test", 1),
      Hops = new List<MessageHop> { hop1, hop2 }
    };

    // Act
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<TestMessage>));
    var json = JsonSerializer.Serialize(original, typeInfo);
    var deserialized = JsonSerializer.Deserialize(json, typeInfo) as MessageEnvelope<TestMessage>;

    // Assert
    await Assert.That(deserialized!.Hops).HasCount().EqualTo(2);
    await Assert.That(deserialized!.Hops[0].ServiceName).IsEqualTo("Service1");
    await Assert.That(deserialized!.Hops[1].ServiceName).IsEqualTo("Service2");
  }

  [Test]
  public async Task MessageEnvelope_WithCausationHops_SerializesCorrectlyAsync() {
    // Arrange
    var causationHop = new MessageHop {
      ServiceName = "ParentService",
      Type = HopType.Causation,
      CausationId = MessageId.New(),
      CausationType = "OrderCreated",
      Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    var currentHop = new MessageHop {
      ServiceName = "CurrentService",
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow
    };

    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test", 1),
      Hops = new List<MessageHop> { causationHop, currentHop }
    };

    // Act
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<TestMessage>));
    var json = JsonSerializer.Serialize(original, typeInfo);
    var deserialized = JsonSerializer.Deserialize(json, typeInfo) as MessageEnvelope<TestMessage>;

    // Assert
    var deserializedCausationHops = deserialized!.GetCausationHops();
    await Assert.That(deserializedCausationHops).HasCount().EqualTo(1);
    await Assert.That(deserializedCausationHops[0].ServiceName).IsEqualTo("ParentService");
  }

  #endregion

  #region Payload Size Tests

  [Test]
  public async Task MessageEnvelope_MinimalHop_ProducesSmallPayloadAsync() {
    // Arrange
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test", 1),
      Hops = new List<MessageHop> {
        new MessageHop { ServiceName = "Test" }
      }
    };

    // Act
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<TestMessage>));
    var json = JsonSerializer.Serialize(original, typeInfo);

    // Assert - Should be relatively small (less than 700 bytes for this simple case)
    await Assert.That(json.Length).IsLessThan(700);

    // Should not contain null properties
    await Assert.That(json).DoesNotContain("null");
  }

  [Test]
  public async Task MessageEnvelope_SerializedJson_IsCompactAsync() {
    // Arrange
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test", 1),
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "Test",
          Topic = "orders",
          StreamKey = "order-1"
        }
      }
    };

    // Act
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<TestMessage>));
    var json = JsonSerializer.Serialize(original, typeInfo);

    // Assert - No whitespace (WriteIndented = false)
    await Assert.That(json).DoesNotContain("  ");  // No double spaces
    await Assert.That(json).DoesNotContain("\n");  // No newlines
  }

  #endregion

  #region Roundtrip Tests

  [Test]
  public async Task ComplexEnvelope_Roundtrips_WithoutDataLossAsync() {
    // Arrange - Create a fully populated envelope
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("StreamSelection", "Order.* → order-{id}", true, "order-123", "Matched pattern");

    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("complex test", 999),
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "Dispatcher",
          MachineName = "machine-1",
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "orders",
          StreamKey = "order-123",
          PartitionIndex = 5,
          SequenceNumber = 100,
          ExecutionStrategy = "SerialExecutor",
          SecurityContext = new SecurityContext { UserId = "user-1", TenantId = "tenant-a" },
          Metadata = new Dictionary<string, object> {
            ["priority"] = "high",
            ["retryCount"] = 0
          },
          Trail = trail,
          CallerMemberName = "ProcessAsync",
          CallerFilePath = "/src/Service.cs",
          CallerLineNumber = 42,
          Duration = TimeSpan.FromMilliseconds(150)
        }
      }
    };

    // Act
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<TestMessage>));
    var json = JsonSerializer.Serialize(original, typeInfo);
    var deserialized = JsonSerializer.Deserialize(json, typeInfo) as MessageEnvelope<TestMessage>;

    // Assert - All data preserved
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.MessageId).IsEqualTo(original.MessageId);
    await Assert.That(deserialized!.GetCorrelationId()).IsEqualTo(original.GetCorrelationId());
    await Assert.That(deserialized!.MessageId).IsEqualTo(original.MessageId);
    await Assert.That(deserialized!.Payload.Value).IsEqualTo("complex test");
    await Assert.That(deserialized!.Payload.Count).IsEqualTo(999);

    var hop = deserialized!.Hops[0];
    await Assert.That(hop.ServiceName).IsEqualTo("Dispatcher");
    await Assert.That(hop.Topic).IsEqualTo("orders");
    await Assert.That(hop.PartitionIndex).IsEqualTo(5);
    await Assert.That(hop.SecurityContext!.UserId).IsEqualTo("user-1");
    await Assert.That(hop.Trail!.Decisions).HasCount().EqualTo(1);
  }

  #endregion
}
