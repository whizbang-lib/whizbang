using System.Text.Json;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for JSON serialization/deserialization of message envelope and related types.
/// Ensures proper cross-network communication with minimal payload size.
/// </summary>
public class SerializationTests {
  private record TestMessage(string Value, int Count);

  private static readonly JsonSerializerOptions Options = new() {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false  // Minimize size
  };

  #region Value Object Serialization Tests

  [Test]
  public async Task MessageId_SerializesAndDeserializes_CorrectlyAsync() {
    // Arrange
    var original = MessageId.New();

    // Act
    var json = JsonSerializer.Serialize(original, Options);
    var deserialized = JsonSerializer.Deserialize<MessageId>(json, Options);

    // Assert
    await Assert.That(deserialized).IsEqualTo(original);
    await Assert.That(json).Contains(original.Value.ToString());
  }

  [Test]
  public async Task CorrelationId_SerializesAndDeserializes_CorrectlyAsync() {
    // Arrange
    var original = CorrelationId.New();

    // Act
    var json = JsonSerializer.Serialize(original, Options);
    var deserialized = JsonSerializer.Deserialize<CorrelationId>(json, Options);

    // Assert
    await Assert.That(deserialized).IsEqualTo(original);
  }

  [Test]
  public async Task CausationId_SerializesAndDeserializes_CorrectlyAsync() {
    // Arrange
    var original = CausationId.New();

    // Act
    var json = JsonSerializer.Serialize(original, Options);
    var deserialized = JsonSerializer.Deserialize<CausationId>(json, Options);

    // Assert
    await Assert.That(deserialized).IsEqualTo(original);
  }

  #endregion

  #region SecurityContext Serialization Tests

  [Test]
  public async Task SecurityContext_SerializesAndDeserializes_WithAllPropertiesAsync() {
    // Arrange
    var original = new SecurityContext {
      UserId = "user-123",
      TenantId = "tenant-abc"
    };

    // Act
    var json = JsonSerializer.Serialize(original, Options);
    var deserialized = JsonSerializer.Deserialize<SecurityContext>(json, Options);

    // Assert
    await Assert.That(deserialized).IsEqualTo(original);
    await Assert.That(deserialized!.UserId).IsEqualTo("user-123");
    await Assert.That(deserialized!.TenantId).IsEqualTo("tenant-abc");
  }

  [Test]
  public async Task SecurityContext_OmitsNullProperties_InJsonAsync() {
    // Arrange
    var original = new SecurityContext {
      UserId = "user-123",
      TenantId = null
    };

    // Act
    var json = JsonSerializer.Serialize(original, Options);

    // Assert
    await Assert.That(json).Contains("user-123");
    await Assert.That(json).DoesNotContain("tenantId");  // Null should be omitted
  }

  #endregion

  #region MessageHop Serialization Tests

  [Test]
  public async Task MessageHop_SerializesAndDeserializes_WithAllPropertiesAsync() {
    // Arrange
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("TestPolicy", "TestRule", true, "config", "reason");

    var original = new MessageHop {
      ServiceName = "TestService",
      MachineName = "test-machine",
      Timestamp = DateTimeOffset.UtcNow,
      Topic = "test-topic",
      StreamKey = "test-stream",
      PartitionIndex = 5,
      SequenceNumber = 100,
      ExecutionStrategy = "SerialExecutor",
      SecurityContext = new SecurityContext { UserId = "user-1", TenantId = "tenant-a" },
      Metadata = new Dictionary<string, object> { ["key"] = "value", ["count"] = 42 },
      Trail = trail,
      CallerMemberName = "TestMethod",
      CallerFilePath = "/test/file.cs",
      CallerLineNumber = 123,
      Duration = TimeSpan.FromMilliseconds(150)
    };

    // Act
    var json = JsonSerializer.Serialize(original, Options);
    var deserialized = JsonSerializer.Deserialize<MessageHop>(json, Options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.ServiceName).IsEqualTo("TestService");
    await Assert.That(deserialized!.Topic).IsEqualTo("test-topic");
    await Assert.That(deserialized!.PartitionIndex).IsEqualTo(5);
    await Assert.That(deserialized!.SequenceNumber).IsEqualTo(100);
  }

  [Test]
  public async Task MessageHop_CausationHop_SerializesCorrectlyAsync() {
    // Arrange
    var original = new MessageHop {
      ServiceName = "TestService",
      Type = HopType.Causation,
      CausationMessageId = MessageId.New(),
      CausationMessageType = "OrderCreated"
    };

    // Act
    var json = JsonSerializer.Serialize(original, Options);
    var deserialized = JsonSerializer.Deserialize<MessageHop>(json, Options);

    // Assert
    await Assert.That(deserialized!.Type).IsEqualTo(HopType.Causation);
    await Assert.That(deserialized!.CausationMessageId).IsEqualTo(original.CausationMessageId);
    await Assert.That(deserialized!.CausationMessageType).IsEqualTo("OrderCreated");
  }

  [Test]
  public async Task MessageHop_OmitsNullAndDefaultValues_InJsonAsync() {
    // Arrange
    var original = new MessageHop {
      ServiceName = "TestService",
      Topic = "test-topic"
      // All other properties default/null
    };

    // Act
    var json = JsonSerializer.Serialize(original, Options);

    // Assert
    await Assert.That(json).Contains("serviceName");
    await Assert.That(json).Contains("topic");
    await Assert.That(json).DoesNotContain("metadata");  // Null should be omitted
    await Assert.That(json).DoesNotContain("securityContext");  // Null should be omitted
  }

  #endregion

  #region PolicyDecisionTrail Serialization Tests

  [Test]
  public async Task PolicyDecisionTrail_SerializesAndDeserializes_WithDecisionsAsync() {
    // Arrange
    var original = new PolicyDecisionTrail();
    original.RecordDecision("Policy1", "Rule1", true, "config1", "reason1");
    original.RecordDecision("Policy2", "Rule2", false, null, "reason2");

    // Act
    var json = JsonSerializer.Serialize(original, Options);
    var deserialized = JsonSerializer.Deserialize<PolicyDecisionTrail>(json, Options);

    // Assert
    await Assert.That(deserialized!.Decisions).HasCount().EqualTo(2);
    await Assert.That(deserialized!.Decisions[0].PolicyName).IsEqualTo("Policy1");
    await Assert.That(deserialized!.Decisions[0].Matched).IsTrue();
    await Assert.That(deserialized!.Decisions[1].PolicyName).IsEqualTo("Policy2");
    await Assert.That(deserialized!.Decisions[1].Matched).IsFalse();
  }

  #endregion

  #region MessageEnvelope Serialization Tests

  [Test]
  public async Task MessageEnvelope_SerializesAndDeserializes_WithSimplePayloadAsync() {
    // Arrange
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = CausationId.New(),
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
    var json = JsonSerializer.Serialize(original, Options);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<TestMessage>>(json, Options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.MessageId).IsEqualTo(original.MessageId);
    await Assert.That(deserialized!.CorrelationId).IsEqualTo(original.CorrelationId);
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
      CorrelationId = CorrelationId.New(),
      CausationId = CausationId.New(),
      Payload = new TestMessage("test", 1),
      Hops = new List<MessageHop> { hop1, hop2 }
    };

    // Act
    var json = JsonSerializer.Serialize(original, Options);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<TestMessage>>(json, Options);

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
      CausationMessageId = MessageId.New(),
      CausationMessageType = "OrderCreated",
      Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    var currentHop = new MessageHop {
      ServiceName = "CurrentService",
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow
    };

    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = CausationId.New(),
      Payload = new TestMessage("test", 1),
      Hops = new List<MessageHop> { causationHop, currentHop }
    };

    // Act
    var json = JsonSerializer.Serialize(original, Options);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<TestMessage>>(json, Options);

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
      CorrelationId = CorrelationId.New(),
      CausationId = CausationId.New(),
      Payload = new TestMessage("test", 1),
      Hops = new List<MessageHop> {
        new MessageHop { ServiceName = "Test" }
      }
    };

    // Act
    var json = JsonSerializer.Serialize(original, Options);

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
      CorrelationId = CorrelationId.New(),
      CausationId = CausationId.New(),
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
    var json = JsonSerializer.Serialize(original, Options);

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
      CorrelationId = CorrelationId.New(),
      CausationId = CausationId.New(),
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
    var json = JsonSerializer.Serialize(original, Options);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<TestMessage>>(json, Options);

    // Assert - All data preserved
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.MessageId).IsEqualTo(original.MessageId);
    await Assert.That(deserialized!.CorrelationId).IsEqualTo(original.CorrelationId);
    await Assert.That(deserialized!.CausationId).IsEqualTo(original.CausationId);
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
