using System.Text.Json;
using TUnit.Assertions;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
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
      StreamId = "test-stream",
      ExecutionStrategy = "SerialExecutor"
    };

    // Act
    var json = JsonSerializer.Serialize(hop, InfrastructureJsonContext.Default.MessageHop);

    // Assert - Uses short property names for wire efficiency
    await Assert.That(json).IsNotNull();
    await Assert.That(json).IsNotEmpty();
    // Type is omitted when default value (HopType.Current = 0)
    await Assert.That(json).Contains("\"si\":"); // ServiceInstance
    await Assert.That(json).Contains("\"ts\":"); // Timestamp
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
          StreamId = "test-stream",
          ExecutionStrategy = "SerialExecutor"
        }
      ]
    };

    // Act
    var json = JsonSerializer.Serialize(metadata, InfrastructureJsonContext.Default.EnvelopeMetadata);

    // Assert - EnvelopeMetadata uses full property names (not short)
    // Note: MessageEnvelope<T> uses short names, but EnvelopeMetadata doesn't
    await Assert.That(json).IsNotNull();
    await Assert.That(json).IsNotEmpty();
    await Assert.That(json).Contains("\"MessageId\":"); // Full name
    await Assert.That(json).Contains("\"Hops\":"); // Full name
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

    // Assert - Uses short property names for wire efficiency
    await Assert.That(json).IsNotNull();
    await Assert.That(json).IsNotEmpty();
    await Assert.That(json).Contains("\"ii\":"); // InstanceId
    await Assert.That(json).Contains("\"sn\":"); // ServiceName
    await Assert.That(json).Contains("\"hn\":"); // HostName
    await Assert.That(json).Contains("\"pi\":"); // ProcessId
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
      StreamId = "test-stream",
      ExecutionStrategy = "SerialExecutor",
      Metadata = null, // Explicitly null
      Scope = null, // Explicitly null
      Trail = null // Explicitly null
    };

    // Act
    var json = JsonSerializer.Serialize(hop, InfrastructureJsonContext.Default.MessageHop);

    // Assert - null properties should be omitted from JSON (using short names)
    await Assert.That(json).IsNotNull();
    await Assert.That(json).IsNotEmpty();
    await Assert.That(json).DoesNotContain("\"md\":"); // Metadata (null)
    await Assert.That(json).DoesNotContain("\"sc\":"); // Scope (null)
    await Assert.That(json).DoesNotContain("\"tr\":"); // Trail (null)
  }

  // ========================================
  // Interface-Based Envelope Serialization Tests
  // These types are created by polymorphic event store reads (ReadPolymorphicAsync)
  // ========================================

  [Test]
  public async Task InfrastructureJsonContext_SerializesMessageEnvelopeIEvent_Async() {
    // Arrange - MessageEnvelope<IEvent> as created by ReadPolymorphicAsync
    var envelope = new MessageEnvelope<IEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent { Name = "Test" },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
    };

    // Act - This would fail before the fix with "No JsonTypeInfo found"
    var json = JsonSerializer.Serialize(envelope, InfrastructureJsonContext.Default.Options);

    // Assert
    await Assert.That(json).IsNotNull();
    await Assert.That(json).IsNotEmpty();
    await Assert.That(json).Contains("\"id\":"); // MessageId
    await Assert.That(json).Contains("\"p\":"); // Payload
    await Assert.That(json).Contains("\"h\":"); // Hops
  }

  [Test]
  public async Task InfrastructureJsonContext_SerializesMessageEnvelopeICommand_Async() {
    // Arrange - MessageEnvelope<ICommand>
    var envelope = new MessageEnvelope<ICommand> {
      MessageId = MessageId.New(),
      Payload = new TestCommand { Data = "Test" },
      Hops = []
    };

    // Act
    var json = JsonSerializer.Serialize(envelope, InfrastructureJsonContext.Default.Options);

    // Assert
    await Assert.That(json).IsNotNull();
    await Assert.That(json).IsNotEmpty();
    await Assert.That(json).Contains("\"id\":"); // MessageId
    await Assert.That(json).Contains("\"p\":"); // Payload
  }

  [Test]
  public async Task InfrastructureJsonContext_SerializesMessageEnvelopeIMessage_Async() {
    // Arrange - MessageEnvelope<IMessage>
    var envelope = new MessageEnvelope<IMessage> {
      MessageId = MessageId.New(),
      Payload = new TestEvent { Name = "Test" },
      Hops = []
    };

    // Act
    var json = JsonSerializer.Serialize(envelope, InfrastructureJsonContext.Default.Options);

    // Assert
    await Assert.That(json).IsNotNull();
    await Assert.That(json).IsNotEmpty();
    await Assert.That(json).Contains("\"id\":"); // MessageId
  }

  [Test]
  public async Task InfrastructureJsonContext_SerializesMessageEnvelopeObject_Async() {
    // Arrange - MessageEnvelope<object> for generic scenarios
    // Use a known type (TestEvent) as the payload since anonymous types aren't registered
    var envelope = new MessageEnvelope<object> {
      MessageId = MessageId.New(),
      Payload = new TestEvent { Name = "ObjectPayload" },
      Hops = []
    };

    // Act
    var json = JsonSerializer.Serialize(envelope, InfrastructureJsonContext.Default.Options);

    // Assert
    await Assert.That(json).IsNotNull();
    await Assert.That(json).IsNotEmpty();
    await Assert.That(json).Contains("\"id\":"); // MessageId
    await Assert.That(json).Contains("\"p\":"); // Payload
  }

  // ========================================
  // Test Doubles
  // ========================================

  private sealed record TestEvent : IEvent {
    public string Name { get; init; } = string.Empty;
  }

  private sealed record TestCommand : ICommand {
    public string Data { get; init; } = string.Empty;
  }
}
