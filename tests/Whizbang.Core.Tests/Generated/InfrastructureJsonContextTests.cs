using System.Text.Json;
using TUnit.Assertions;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Generated;

/// <summary>
/// Tests for InfrastructureJsonContext - AOT-compatible JSON serialization for infrastructure types.
/// </summary>
[Category("Serialization")]
public class InfrastructureJsonContextTests {

  [Test]
  public async Task InfrastructureJsonContext_SerializesMessageHop_Async() {
    // Arrange
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        InstanceId = Guid.NewGuid(),
        ServiceName = "TestService",
        HostName = "test-host",
        ProcessId = 12345
      },
      Timestamp = DateTimeOffset.UtcNow,
      Topic = "test-topic",
      StreamKey = "test-stream",
      ExecutionStrategy = "SerialExecutor"
    };

    // Act
    var json = JsonSerializer.Serialize(hop, InfrastructureJsonContext.Default.MessageHop);

    // Assert
    await Assert.That(json).IsNotNull();
    await Assert.That(json).IsNotEmpty();
    await Assert.That(json).Contains("\"Type\":");
    await Assert.That(json).Contains("\"ServiceInstance\":");
    await Assert.That(json).Contains("\"Timestamp\":");
  }

  [Test]
  public async Task InfrastructureJsonContext_SerializesEnvelopeMetadata_Async() {
    // Arrange
    var metadata = new EnvelopeMetadata {
      MessageId = MessageId.New(),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          StreamKey = "test-stream",
          ExecutionStrategy = "SerialExecutor"
        }
      ]
    };

    // Act
    var json = JsonSerializer.Serialize(metadata, InfrastructureJsonContext.Default.EnvelopeMetadata);

    // Assert
    await Assert.That(json).IsNotNull();
    await Assert.That(json).IsNotEmpty();
    await Assert.That(json).Contains("\"MessageId\":");
    await Assert.That(json).Contains("\"Hops\":");
  }

  [Test]
  public async Task InfrastructureJsonContext_SerializesServiceInstanceInfo_Async() {
    // Arrange
    var instanceInfo = new ServiceInstanceInfo {
      InstanceId = Guid.NewGuid(),
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345
    };

    // Act
    var json = JsonSerializer.Serialize(instanceInfo, InfrastructureJsonContext.Default.ServiceInstanceInfo);

    // Assert
    await Assert.That(json).IsNotNull();
    await Assert.That(json).IsNotEmpty();
    await Assert.That(json).Contains("\"InstanceId\":");
    await Assert.That(json).Contains("\"ServiceName\":");
    await Assert.That(json).Contains("\"HostName\":");
    await Assert.That(json).Contains("\"ProcessId\":");
  }

  [Test]
  public async Task InfrastructureJsonContext_IgnoresNullPropertiesWhenSerializing_Async() {
    // Arrange - MessageHop with null Metadata, SecurityContext, and Trail
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        InstanceId = Guid.NewGuid(),
        ServiceName = "TestService",
        HostName = "test-host",
        ProcessId = 12345
      },
      Timestamp = DateTimeOffset.UtcNow,
      Topic = "test-topic",
      StreamKey = "test-stream",
      ExecutionStrategy = "SerialExecutor",
      Metadata = null, // Explicitly null
      SecurityContext = null, // Explicitly null
      Trail = null // Explicitly null
    };

    // Act
    var json = JsonSerializer.Serialize(hop, InfrastructureJsonContext.Default.MessageHop);

    // Assert - null properties should be omitted from JSON
    await Assert.That(json).IsNotNull();
    await Assert.That(json).IsNotEmpty();
    await Assert.That(json).DoesNotContain("\"Metadata\":");
    await Assert.That(json).DoesNotContain("\"SecurityContext\":");
    await Assert.That(json).DoesNotContain("\"Trail\":");
  }
}
