using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Observability.Tests.Generated;

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
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Origin",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Topic = "test-topic",
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
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
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service1",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Topic = "orders",
      Timestamp = DateTimeOffset.UtcNow
    };
    var hop2 = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service2",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      StreamKey = "order-123",
      Timestamp = DateTimeOffset.UtcNow.AddSeconds(1)
    };

    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test", 1),
      Hops = [hop1, hop2]
    };

    // Act
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<TestMessage>));
    var json = JsonSerializer.Serialize(original, typeInfo);
    var deserialized = JsonSerializer.Deserialize(json, typeInfo) as MessageEnvelope<TestMessage>;

    // Assert
    await Assert.That(deserialized!.Hops).HasCount().EqualTo(2);
    await Assert.That(deserialized!.Hops[0].ServiceInstance.ServiceName).IsEqualTo("Service1");
    await Assert.That(deserialized!.Hops[1].ServiceInstance.ServiceName).IsEqualTo("Service2");
  }

  [Test]
  public async Task MessageEnvelope_WithCausationHops_SerializesCorrectlyAsync() {
    // Arrange
    var causationHop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "ParentService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Type = HopType.Causation,
      CausationId = MessageId.New(),
      CausationType = "OrderCreated",
      Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    var currentHop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "CurrentService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow
    };

    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test", 1),
      Hops = [causationHop, currentHop]
    };

    // Act
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<TestMessage>));
    var json = JsonSerializer.Serialize(original, typeInfo);
    var deserialized = JsonSerializer.Deserialize(json, typeInfo) as MessageEnvelope<TestMessage>;

    // Assert
    var deserializedCausationHops = deserialized!.GetCausationHops();
    await Assert.That(deserializedCausationHops).HasCount().EqualTo(1);
    await Assert.That(deserializedCausationHops[0].ServiceInstance.ServiceName).IsEqualTo("ParentService");
  }

  #endregion

  #region Payload Size Tests

  [Test]
  public async Task MessageEnvelope_MinimalHop_ProducesSmallPayloadAsync() {
    // Arrange
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test", 1),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Test",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          }
        }
      ]
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
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Test",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Topic = "orders",
          StreamKey = "order-1"
        }
      ]
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
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task ComplexEnvelope_Roundtrips_WithoutDataLossAsync() {
    // Arrange - Create a fully populated envelope
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("StreamSelection", "Order.* → order-{id}", true, "order-123", "Matched pattern");

    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("complex test", 999),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Dispatcher",
            InstanceId = Guid.NewGuid(),
            HostName = "machine-1",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "orders",
          StreamKey = "order-123",
          PartitionIndex = 5,
          SequenceNumber = 100,
          ExecutionStrategy = "SerialExecutor",
          SecurityContext = new SecurityContext { UserId = "user-1", TenantId = "tenant-a" },
          Metadata = new Dictionary<string, JsonElement> {
            ["priority"] = JsonSerializer.SerializeToElement("high"),
            ["retryCount"] = JsonSerializer.SerializeToElement(0)
          },
          Trail = trail,
          CallerMemberName = "ProcessAsync",
          CallerFilePath = "/src/Service.cs",
          CallerLineNumber = 42,
          Duration = TimeSpan.FromMilliseconds(150)
        }
      ]
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
    await Assert.That(hop.ServiceInstance.ServiceName).IsEqualTo("Dispatcher");
    await Assert.That(hop.Topic).IsEqualTo("orders");
    await Assert.That(hop.PartitionIndex).IsEqualTo(5);
    await Assert.That(hop.SecurityContext!.UserId).IsEqualTo("user-1");
    await Assert.That(hop.Trail!.Decisions).HasCount().EqualTo(1);
  }

  #endregion
}
