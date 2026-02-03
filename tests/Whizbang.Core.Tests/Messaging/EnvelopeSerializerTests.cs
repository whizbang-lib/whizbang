using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for EnvelopeSerializer - centralized envelope serialization/deserialization.
/// Covers typed envelope serialization to JsonElement form and message deserialization.
/// </summary>
[Category("Messaging")]
[Category("Serialization")]
public partial class EnvelopeSerializerTests {
  private static JsonSerializerOptions _createTestJsonOptions() {
    var options = new JsonSerializerOptions {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      WriteIndented = false,
      TypeInfoResolver = JsonTypeInfoResolver.Combine(
        WhizbangIdJsonContext.Default,  // FIRST: custom converters for MessageId/CorrelationId
        EnvelopeTestJsonContext.Default,
        InfrastructureJsonContext.Default
      )
    };
    return options;
  }

  private static MessageHop _createTestHop() {
    return new MessageHop {
      Type = HopType.Current,
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Timestamp = DateTimeOffset.UtcNow
    };
  }

  // ========================================
  // SerializeEnvelope Tests
  // ========================================

  [Test]
  public async Task SerializeEnvelope_WithValidEnvelope_ReturnsSerializedEnvelopeAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var serializer = new EnvelopeSerializer(options);
    var msgId = MessageId.New();
    var envelope = new MessageEnvelope<EnvelopeTestMsg> {
      MessageId = msgId,
      Payload = new EnvelopeTestMsg("TestValue"),
      Hops = [_createTestHop()]
    };

    // Act
    var result = serializer.SerializeEnvelope(envelope);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.JsonEnvelope).IsNotNull();
    await Assert.That(result.EnvelopeType).Contains("MessageEnvelope");
    await Assert.That(result.MessageType).Contains("EnvelopeTestMsg");
  }

  [Test]
  public async Task SerializeEnvelope_WithNullEnvelope_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var serializer = new EnvelopeSerializer(options);

    // Act & Assert
    await Assert.That(() => serializer.SerializeEnvelope<EnvelopeTestMsg>(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task SerializeEnvelope_WithJsonElementPayload_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var serializer = new EnvelopeSerializer(options);
    var msgId = MessageId.New();
    var jsonElement = JsonDocument.Parse("{\"value\":\"test\"}").RootElement;

    // Create envelope with JsonElement payload (simulating double serialization)
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = msgId,
      Payload = jsonElement,
      Hops = [_createTestHop()]
    };

    // Act & Assert - Detects double serialization because payload is already JsonElement
    await Assert.That(() => serializer.SerializeEnvelope(envelope))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*DOUBLE SERIALIZATION DETECTED*");
  }

  [Test]
  public async Task SerializeEnvelope_CapturesCorrectTypeMetadataAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var serializer = new EnvelopeSerializer(options);
    var msgId = MessageId.New();
    var envelope = new MessageEnvelope<EnvelopeTestMsg> {
      MessageId = msgId,
      Payload = new EnvelopeTestMsg("TestValue"),
      Hops = [_createTestHop()]
    };

    // Act
    var result = serializer.SerializeEnvelope(envelope);

    // Assert
    await Assert.That(result.MessageType).Contains("EnvelopeTestMsg");
    await Assert.That(result.EnvelopeType).Contains("MessageEnvelope");
  }

  [Test]
  public async Task SerializeEnvelope_PreservesMessageIdAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var serializer = new EnvelopeSerializer(options);
    var msgId = MessageId.New();
    var envelope = new MessageEnvelope<EnvelopeTestMsg> {
      MessageId = msgId,
      Payload = new EnvelopeTestMsg("TestValue"),
      Hops = [_createTestHop()]
    };

    // Act
    var result = serializer.SerializeEnvelope(envelope);

    // Assert
    await Assert.That(result.JsonEnvelope.MessageId).IsEqualTo(msgId);
  }

  [Test]
  public async Task SerializeEnvelope_PreservesHopsAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var serializer = new EnvelopeSerializer(options);
    var msgId = MessageId.New();
    var hop1 = _createTestHop();
    var hop2 = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        InstanceId = Guid.NewGuid(),
        ServiceName = "Service2",
        HostName = "host2",
        ProcessId = 12345
      },
      Timestamp = DateTimeOffset.UtcNow
    };
    var envelope = new MessageEnvelope<EnvelopeTestMsg> {
      MessageId = msgId,
      Payload = new EnvelopeTestMsg("TestValue"),
      Hops = [hop1, hop2]
    };

    // Act
    var result = serializer.SerializeEnvelope(envelope);

    // Assert
    await Assert.That(result.JsonEnvelope.Hops.Count).IsEqualTo(2);
  }

  [Test]
  public async Task SerializeEnvelope_WithDefaultOptions_ThrowsNotSupportedExceptionAsync() {
    // Arrange - use null options (no TypeInfoResolver configured)
    // In AOT mode, JsonSerializer requires explicit TypeInfoResolver
    var serializer = new EnvelopeSerializer(null);
    var msgId = MessageId.New();
    var envelope = new MessageEnvelope<EnvelopeTestMsg> {
      MessageId = msgId,
      Payload = new EnvelopeTestMsg("TestValue"),
      Hops = [_createTestHop()]
    };

    // Act & Assert - Without TypeInfoResolver, AOT serialization throws NotSupportedException
    await Assert.That(() => serializer.SerializeEnvelope(envelope))
      .Throws<NotSupportedException>();
  }

  // ========================================
  // DeserializeMessage Tests
  // ========================================

  [Test]
  public async Task DeserializeMessage_WithNullEnvelope_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var serializer = new EnvelopeSerializer(options);

    // Act & Assert
    await Assert.That(() => serializer.DeserializeMessage(null!, "SomeType"))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task DeserializeMessage_WithNullTypeName_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var serializer = new EnvelopeSerializer(options);
    var msgId = MessageId.New();
    var jsonEnvelope = new MessageEnvelope<JsonElement> {
      MessageId = msgId,
      Payload = JsonDocument.Parse("{\"value\":\"test\"}").RootElement,
      Hops = [_createTestHop()]
    };

    // Act & Assert
    await Assert.That(() => serializer.DeserializeMessage(jsonEnvelope, null!))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task DeserializeMessage_WithEmptyTypeName_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var serializer = new EnvelopeSerializer(options);
    var msgId = MessageId.New();
    var jsonEnvelope = new MessageEnvelope<JsonElement> {
      MessageId = msgId,
      Payload = JsonDocument.Parse("{\"value\":\"test\"}").RootElement,
      Hops = [_createTestHop()]
    };

    // Act & Assert
    await Assert.That(() => serializer.DeserializeMessage(jsonEnvelope, "   "))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task DeserializeMessage_WithUnknownTypeName_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var serializer = new EnvelopeSerializer(options);
    var msgId = MessageId.New();
    var jsonEnvelope = new MessageEnvelope<JsonElement> {
      MessageId = msgId,
      Payload = JsonDocument.Parse("{\"value\":\"test\"}").RootElement,
      Hops = [_createTestHop()]
    };

    // Act & Assert
    await Assert.That(() => serializer.DeserializeMessage(jsonEnvelope, "Unknown.NonExistent.Type, UnknownAssembly"))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*Failed to resolve message type*");
  }

  // ========================================
  // SerializedEnvelope Record Tests
  // ========================================

  [Test]
  public async Task SerializedEnvelope_RecordEquality_WorksCorrectlyAsync() {
    // Arrange
    var msgId = MessageId.New();
    var jsonEnvelope = new MessageEnvelope<JsonElement> {
      MessageId = msgId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [_createTestHop()]
    };

    var envelope1 = new SerializedEnvelope(jsonEnvelope, "EnvelopeType", "MessageType");
    var envelope2 = new SerializedEnvelope(jsonEnvelope, "EnvelopeType", "MessageType");

    // Assert
    await Assert.That(envelope1).IsEqualTo(envelope2);
  }

  // ========================================
  // Test Types
  // ========================================

  /// <summary>
  /// Test message type for envelope serialization tests.
  /// </summary>
  public sealed record EnvelopeTestMsg(string Value);

  /// <summary>
  /// JSON context for envelope test message types.
  /// </summary>
  [JsonSerializable(typeof(EnvelopeTestMsg))]
  [JsonSerializable(typeof(MessageEnvelope<EnvelopeTestMsg>))]
  [JsonSerializable(typeof(MessageEnvelope<JsonElement>))]
  [JsonSerializable(typeof(object))]
  internal sealed partial class EnvelopeTestJsonContext : JsonSerializerContext {
  }
}
