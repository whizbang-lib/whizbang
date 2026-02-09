using System.Text;
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
/// Tests for JsonLifecycleMessageDeserializer - AOT-safe JSON deserialization.
/// Covers envelope parsing, type extraction, and error handling.
/// </summary>
[Category("Messaging")]
[Category("Serialization")]
public partial class JsonLifecycleMessageDeserializerTests {
  private static JsonSerializerOptions _createTestJsonOptions() {
    var options = new JsonSerializerOptions {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      WriteIndented = false,
      TypeInfoResolver = JsonTypeInfoResolver.Combine(
        WhizbangIdJsonContext.Default,
        LifecycleTestJsonContext.Default,
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
  // Constructor Tests
  // ========================================

  [Test]
  public async Task Constructor_WithNullOptions_UsesDefaultOptionsAsync() {
    // Arrange & Act
    var deserializer = new JsonLifecycleMessageDeserializer(null);

    // Assert - Should not throw
    await Assert.That(deserializer).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithOptions_UsesProvidedOptionsAsync() {
    // Arrange
    var options = _createTestJsonOptions();

    // Act
    var deserializer = new JsonLifecycleMessageDeserializer(options);

    // Assert
    await Assert.That(deserializer).IsNotNull();
  }

  // ========================================
  // DeserializeFromEnvelope(envelope, typeName) Tests
  // ========================================

  [Test]
  public async Task DeserializeFromEnvelope_WithNullEnvelope_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);

    // Act & Assert
    await Assert.That(() => deserializer.DeserializeFromEnvelope(null!, "SomeType"))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task DeserializeFromEnvelope_WithNullTypeName_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [_createTestHop()]
    };

    // Act & Assert
    await Assert.That(() => deserializer.DeserializeFromEnvelope(envelope, null!))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task DeserializeFromEnvelope_WithEmptyTypeName_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [_createTestHop()]
    };

    // Act & Assert
    await Assert.That(() => deserializer.DeserializeFromEnvelope(envelope, "   "))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task DeserializeFromEnvelope_WithInvalidEnvelopeTypeFormat_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{\"value\":\"test\"}").RootElement,
      Hops = [_createTestHop()]
    };

    // Act & Assert - Type name without [[ and ]]
    await Assert.That(() => deserializer.DeserializeFromEnvelope(envelope, "InvalidFormat"))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*Invalid envelope type name format*");
  }

  // ========================================
  // DeserializeFromEnvelope(envelope) Tests
  // ========================================

  [Test]
  public async Task DeserializeFromEnvelope_WithoutTypeName_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [_createTestHop()]
    };

    // Act & Assert
    await Assert.That(() => deserializer.DeserializeFromEnvelope(envelope))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*requires the envelope type name*");
  }

  // ========================================
  // DeserializeFromBytes Tests
  // ========================================

  [Test]
  public async Task DeserializeFromBytes_WithNullBytes_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);

    // Act & Assert
    await Assert.That(() => deserializer.DeserializeFromBytes(null!, "SomeType"))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task DeserializeFromBytes_WithNullTypeName_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var bytes = Encoding.UTF8.GetBytes("{}");

    // Act & Assert
    await Assert.That(() => deserializer.DeserializeFromBytes(bytes, null!))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task DeserializeFromBytes_WithEmptyTypeName_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var bytes = Encoding.UTF8.GetBytes("{}");

    // Act & Assert
    await Assert.That(() => deserializer.DeserializeFromBytes(bytes, "   "))
      .Throws<ArgumentException>();
  }

  // ========================================
  // DeserializeFromJsonElement Tests
  // ========================================

  [Test]
  public async Task DeserializeFromJsonElement_WithNullTypeName_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var jsonElement = JsonDocument.Parse("{}").RootElement;

    // Act & Assert
    await Assert.That(() => deserializer.DeserializeFromJsonElement(jsonElement, null!))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task DeserializeFromJsonElement_WithEmptyTypeName_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var jsonElement = JsonDocument.Parse("{}").RootElement;

    // Act & Assert
    await Assert.That(() => deserializer.DeserializeFromJsonElement(jsonElement, "   "))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task DeserializeFromJsonElement_WithUnknownType_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var jsonElement = JsonDocument.Parse("{\"value\":\"test\"}").RootElement;

    // Act & Assert
    await Assert.That(() => deserializer.DeserializeFromJsonElement(jsonElement, "Unknown.NonExistent.Type, UnknownAssembly"))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*Failed to resolve message type*");
  }

  // ========================================
  // Envelope Type Extraction Tests
  // ========================================

  [Test]
  public async Task ExtractMessageType_WithMalformedBrackets_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [_createTestHop()]
    };

    // Act & Assert - Type with only [[ but no ]]
    await Assert.That(() => deserializer.DeserializeFromEnvelope(envelope, "MessageEnvelope`1[[MyType"))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*Invalid envelope type name format*");
  }

  [Test]
  public async Task ExtractMessageType_WithEmptyBrackets_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [_createTestHop()]
    };

    // Act & Assert - Empty type between brackets
    await Assert.That(() => deserializer.DeserializeFromEnvelope(envelope, "MessageEnvelope`1[[]]"))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*Failed to extract message type*");
  }

  // ========================================
  // Test Types
  // ========================================

  /// <summary>
  /// Test message type for lifecycle deserialization tests.
  /// </summary>
  public sealed record LifecycleTestMsg(string Value);

  /// <summary>
  /// JSON context for lifecycle test message types.
  /// </summary>
  [JsonSerializable(typeof(LifecycleTestMsg))]
  [JsonSerializable(typeof(MessageEnvelope<LifecycleTestMsg>))]
  [JsonSerializable(typeof(MessageEnvelope<JsonElement>))]
  internal sealed partial class LifecycleTestJsonContext : JsonSerializerContext {
  }
}
