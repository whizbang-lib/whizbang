using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for MessageHop implementation.
/// Ensures all properties and default values are properly tested.
/// </summary>
public class MessageHopTests {
  [Test]
  public async Task MessageHop_WithRequiredProperties_InitializesWithDefaultsAsync() {
    // Arrange & Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      }
    };

    // Assert - Verify required property
    await Assert.That(hop.ServiceInstance.ServiceName).IsEqualTo("TestService");

    // Assert - Verify default values are accessible
    await Assert.That(hop.ServiceInstance.HostName).IsNotNull();
    await Assert.That(hop.ServiceInstance.HostName).IsNotEqualTo(string.Empty);
    await Assert.That(hop.Timestamp).IsNotEqualTo(default);
    await Assert.That(hop.Topic).IsEqualTo(string.Empty);
    await Assert.That(hop.StreamId).IsEqualTo(string.Empty);
    await Assert.That(hop.ExecutionStrategy).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task MessageHop_WithCausationType_StoresCausationTypeAsync() {
    // Arrange & Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Type = HopType.Causation,
      CausationType = "ParentMessage"
    };

    // Assert
    await Assert.That(hop.CausationType).IsEqualTo("ParentMessage");
    await Assert.That(hop.Type).IsEqualTo(HopType.Causation);
  }

  [Test]
  public async Task MessageHop_WithAllProperties_StoresAllValuesAsync() {
    // Arrange
    var timestamp = DateTimeOffset.UtcNow.AddHours(-1);
    var duration = TimeSpan.FromSeconds(5);

    // Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "TestMachine",
        ProcessId = 12345
      },
      Timestamp = timestamp,
      Topic = "TestTopic",
      StreamId = "TestStream",
      PartitionIndex = 3,
      SequenceNumber = 42,
      ExecutionStrategy = "SerialExecutor",
      CallerMemberName = "TestMethod",
      CallerFilePath = "/path/to/test.cs",
      CallerLineNumber = 100,
      Duration = duration
    };

    // Assert
    await Assert.That(hop.ServiceInstance.ServiceName).IsEqualTo("TestService");
    await Assert.That(hop.ServiceInstance.HostName).IsEqualTo("TestMachine");
    await Assert.That(hop.Timestamp).IsEqualTo(timestamp);
    await Assert.That(hop.Topic).IsEqualTo("TestTopic");
    await Assert.That(hop.StreamId).IsEqualTo("TestStream");
    await Assert.That(hop.PartitionIndex).IsEqualTo(3);
    await Assert.That(hop.SequenceNumber).IsEqualTo(42L);
    await Assert.That(hop.ExecutionStrategy).IsEqualTo("SerialExecutor");
    await Assert.That(hop.CallerMemberName).IsEqualTo("TestMethod");
    await Assert.That(hop.CallerFilePath).IsEqualTo("/path/to/test.cs");
    await Assert.That(hop.CallerLineNumber).IsEqualTo(100);
    await Assert.That(hop.Duration).IsEqualTo(duration);
  }

  [Test]
  public async Task MessageHop_TypeDefaultsToCurrent_WhenNotSpecifiedAsync() {
    // Arrange & Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      }
    };

    // Assert
    await Assert.That(hop.Type).IsEqualTo(HopType.Current);
  }

  [Test]
  public async Task MessageHop_MachineName_UsesEnvironmentMachineName_ByDefaultAsync() {
    // Arrange & Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = Environment.MachineName,
        ProcessId = 12345
      }
    };

    // Assert - Should default to Environment.MachineName
    await Assert.That(hop.ServiceInstance.HostName).IsEqualTo(Environment.MachineName);
  }

  [Test]
  public async Task MessageHop_WithCausationAndCorrelationIds_SetsIdsAsync() {
    // Arrange
    var causationId = MessageId.New();
    var correlationId = CorrelationId.New();

    // Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Type = HopType.Causation,
      CausationId = causationId,
      CorrelationId = correlationId
    };

    // Assert
    await Assert.That(hop.CausationId).IsEqualTo(causationId);
    await Assert.That(hop.CorrelationId).IsEqualTo(correlationId);
  }

  [Test]
  public async Task MessageHop_WithSecurityContext_SetsSecurityContextAsync() {
    // Arrange
    var securityContext = new SecurityContext {
      UserId = "user123",
      TenantId = "tenant456"
    };

    // Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Scope = ScopeDelta.FromSecurityContext(securityContext)
    };

    // Create an envelope to use GetCurrentScope()
    var envelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "test",
      Hops = [hop]
    };
    var scope = envelope.GetCurrentScope();

    // Assert
    await Assert.That(hop.Scope).IsNotNull();
    await Assert.That(scope?.Scope?.UserId).IsEqualTo("user123");
    await Assert.That(scope?.Scope?.TenantId).IsEqualTo("tenant456");
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task MessageHop_WithMetadata_SetsMetadataAsync() {
    // Arrange
    var metadata = new Dictionary<string, JsonElement> {
      ["key1"] = JsonSerializer.SerializeToElement("value1"),
      ["key2"] = JsonSerializer.SerializeToElement(42)
    };

    // Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Metadata = metadata
    };

    // Assert
    await Assert.That(hop.Metadata).IsNotNull();
    await Assert.That(hop.Metadata!.Count).IsEqualTo(2);
    await Assert.That(hop.Metadata["key1"].GetString()).IsEqualTo("value1");
    await Assert.That(hop.Metadata["key2"].GetInt32()).IsEqualTo(42);
  }

  [Test]
  public async Task MessageHop_WithTrail_SetsPolicyDecisionTrailAsync() {
    // Arrange
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("TestPolicy", "TestRule", true, "TestConfig", "Test reason");

    // Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Trail = trail
    };

    // Assert
    await Assert.That(hop.Trail).IsNotNull();
    await Assert.That(hop.Trail!.Decisions).Count().IsEqualTo(1);
    await Assert.That(hop.Trail.Decisions[0].PolicyName).IsEqualTo("TestPolicy");
  }

  [Test]
  public async Task MessageHop_WithExpression_CreatesModifiedCopyAsync() {
    // Arrange
    var original = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "OriginalService",
        InstanceId = Guid.NewGuid(),
        HostName = "original-host",
        ProcessId = 12345
      },
      Topic = "OriginalTopic",
      ExecutionStrategy = "SerialExecutor"
    };

    // Act - Use 'with' expression to create a modified copy (covers copy constructor)
    var copy = original with {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "ModifiedService",
        InstanceId = Guid.NewGuid(),
        HostName = "modified-host",
        ProcessId = 67890
      },
      Topic = "ModifiedTopic"
    };

    // Assert - Original unchanged
    await Assert.That(original.ServiceInstance.ServiceName).IsEqualTo("OriginalService");
    await Assert.That(original.Topic).IsEqualTo("OriginalTopic");
    await Assert.That(original.ExecutionStrategy).IsEqualTo("SerialExecutor");

    // Assert - Copy has modifications
    await Assert.That(copy.ServiceInstance.ServiceName).IsEqualTo("ModifiedService");
    await Assert.That(copy.Topic).IsEqualTo("ModifiedTopic");
    await Assert.That(copy.ExecutionStrategy).IsEqualTo("SerialExecutor"); // Unchanged property carried over
  }

  // =============================================================================
  // JSON Serialization Backwards Compatibility Tests
  // =============================================================================
  // These tests ensure that MessageHop can be deserialized from JSON with either
  // short property names (new format) or long property names (legacy format in
  // existing database records).

  [Test]
  public async Task MessageHop_Deserialize_ShortPropertyNames_WorksAsync() {
    // Arrange - JSON with short property names (new format)
    // Short names: si=ServiceInstance, sn=ServiceName, ii=InstanceId, hn=HostName, pi=ProcessId
    //              to=Topic, st=StreamId, es=ExecutionStrategy
    var instanceId = Guid.NewGuid();
    var json = $$"""
    {
      "si": {
        "sn": "TestService",
        "ii": "{{instanceId}}",
        "hn": "test-host",
        "pi": 12345
      },
      "to": "TestTopic",
      "st": "TestStream",
      "es": "SerialExecutor"
    }
    """;

    // Act
    var options = InfrastructureJsonContext.Default.Options;
    var hop = JsonSerializer.Deserialize<MessageHop>(json, options);

    // Assert
    await Assert.That(hop).IsNotNull();
    await Assert.That(hop!.ServiceInstance.ServiceName).IsEqualTo("TestService");
    await Assert.That(hop.ServiceInstance.InstanceId).IsEqualTo(instanceId);
    await Assert.That(hop.ServiceInstance.HostName).IsEqualTo("test-host");
    await Assert.That(hop.ServiceInstance.ProcessId).IsEqualTo(12345);
    await Assert.That(hop.Topic).IsEqualTo("TestTopic");
    await Assert.That(hop.StreamId).IsEqualTo("TestStream");
    await Assert.That(hop.ExecutionStrategy).IsEqualTo("SerialExecutor");
  }

  [Test]
  public async Task MessageHop_Deserialize_LongPropertyNames_WorksAsync() {
    // Arrange - JSON with long property names (legacy format in existing database)
    // This is what existing data in JDNext looks like
    var instanceId = Guid.NewGuid();
    var json = $$"""
    {
      "ServiceInstance": {
        "ServiceName": "TestService",
        "InstanceId": "{{instanceId}}",
        "HostName": "test-host",
        "ProcessId": 12345
      },
      "Topic": "TestTopic",
      "StreamId": "TestStream",
      "ExecutionStrategy": "SerialExecutor",
      "Type": 0
    }
    """;

    // Act
    var options = InfrastructureJsonContext.Default.Options;
    var hop = JsonSerializer.Deserialize<MessageHop>(json, options);

    // Assert - Should successfully deserialize legacy format
    await Assert.That(hop).IsNotNull();
    await Assert.That(hop!.ServiceInstance.ServiceName).IsEqualTo("TestService");
    await Assert.That(hop.ServiceInstance.InstanceId).IsEqualTo(instanceId);
    await Assert.That(hop.ServiceInstance.HostName).IsEqualTo("test-host");
    await Assert.That(hop.ServiceInstance.ProcessId).IsEqualTo(12345);
    await Assert.That(hop.Topic).IsEqualTo("TestTopic");
    await Assert.That(hop.StreamId).IsEqualTo("TestStream");
    await Assert.That(hop.ExecutionStrategy).IsEqualTo("SerialExecutor");
    await Assert.That(hop.Type).IsEqualTo(HopType.Current);
  }

  [Test]
  public async Task MessageHop_Deserialize_MixedPropertyNames_WorksAsync() {
    // Arrange - JSON with a mix of short and long property names
    // Could happen during transition period
    var instanceId = Guid.NewGuid();
    var json = $$"""
    {
      "ServiceInstance": {
        "sn": "TestService",
        "ii": "{{instanceId}}",
        "HostName": "test-host",
        "pi": 12345
      },
      "to": "TestTopic",
      "StreamId": "TestStream"
    }
    """;

    // Act
    var options = InfrastructureJsonContext.Default.Options;
    var hop = JsonSerializer.Deserialize<MessageHop>(json, options);

    // Assert - Should handle mixed property names
    await Assert.That(hop).IsNotNull();
    await Assert.That(hop!.ServiceInstance.ServiceName).IsEqualTo("TestService");
    await Assert.That(hop.ServiceInstance.InstanceId).IsEqualTo(instanceId);
    await Assert.That(hop.ServiceInstance.HostName).IsEqualTo("test-host");
    await Assert.That(hop.ServiceInstance.ProcessId).IsEqualTo(12345);
    await Assert.That(hop.Topic).IsEqualTo("TestTopic");
    await Assert.That(hop.StreamId).IsEqualTo("TestStream");
  }

  [Test]
  public async Task MessageHop_Serialize_UsesShortPropertyNamesAsync() {
    // Arrange
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Topic = "TestTopic",
      StreamId = "TestStream",
      ExecutionStrategy = "SerialExecutor"
    };

    // Act
    var options = InfrastructureJsonContext.Default.Options;
    var json = JsonSerializer.Serialize(hop, options);

    // Assert - Should use short property names
    await Assert.That(json).Contains("\"si\":");
    await Assert.That(json).Contains("\"sn\":");
    await Assert.That(json).Contains("\"to\":");
    await Assert.That(json).Contains("\"st\":");
    await Assert.That(json).Contains("\"es\":");
    // Should NOT contain long property names
    await Assert.That(json).DoesNotContain("\"ServiceInstance\":");
    await Assert.That(json).DoesNotContain("\"ServiceName\":");
    await Assert.That(json).DoesNotContain("\"Topic\":");
    await Assert.That(json).DoesNotContain("\"StreamId\":");
    await Assert.That(json).DoesNotContain("\"ExecutionStrategy\":");
  }

  [Test]
  public async Task MessageHop_RoundTrip_WithAllProperties_PreservesDataAsync() {
    // Arrange
    var causationId = MessageId.New();
    var correlationId = CorrelationId.New();
    var timestamp = DateTimeOffset.UtcNow;
    var original = new MessageHop {
      Type = HopType.Causation,
      CausationId = causationId,
      CorrelationId = correlationId,
      CausationType = "ParentMessage",
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Timestamp = timestamp,
      Topic = "TestTopic",
      StreamId = "TestStream",
      PartitionIndex = 5,
      SequenceNumber = 100,
      ExecutionStrategy = "ParallelExecutor",
      CallerMemberName = "TestMethod",
      CallerFilePath = "/test/path.cs",
      CallerLineNumber = 42,
      Duration = TimeSpan.FromSeconds(2.5),
      TraceParent = "00-traceid-spanid-01"
    };

    // Act - Serialize then deserialize
    var options = InfrastructureJsonContext.Default.Options;
    var json = JsonSerializer.Serialize(original, options);
    var deserialized = JsonSerializer.Deserialize<MessageHop>(json, options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Type).IsEqualTo(HopType.Causation);
    await Assert.That(deserialized.CausationId).IsEqualTo(causationId);
    await Assert.That(deserialized.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(deserialized.CausationType).IsEqualTo("ParentMessage");
    await Assert.That(deserialized.ServiceInstance.ServiceName).IsEqualTo("TestService");
    await Assert.That(deserialized.Timestamp).IsEqualTo(timestamp);
    await Assert.That(deserialized.Topic).IsEqualTo("TestTopic");
    await Assert.That(deserialized.StreamId).IsEqualTo("TestStream");
    await Assert.That(deserialized.PartitionIndex).IsEqualTo(5);
    await Assert.That(deserialized.SequenceNumber).IsEqualTo(100L);
    await Assert.That(deserialized.ExecutionStrategy).IsEqualTo("ParallelExecutor");
    await Assert.That(deserialized.CallerMemberName).IsEqualTo("TestMethod");
    await Assert.That(deserialized.CallerFilePath).IsEqualTo("/test/path.cs");
    await Assert.That(deserialized.CallerLineNumber).IsEqualTo(42);
    await Assert.That(deserialized.Duration).IsEqualTo(TimeSpan.FromSeconds(2.5));
    await Assert.That(deserialized.TraceParent).IsEqualTo("00-traceid-spanid-01");
  }
}
