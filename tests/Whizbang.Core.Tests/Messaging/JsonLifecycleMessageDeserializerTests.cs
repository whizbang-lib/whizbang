using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for JsonLifecycleMessageDeserializer - AOT-safe JSON deserialization.
/// Covers envelope parsing, type extraction, and error handling.
/// </summary>
[Category("Messaging")]
[Category("Serialization")]
public partial class JsonLifecycleMessageDeserializerTests {
  /// <summary>
  /// Register test types in JsonContextRegistry so the deserializer can resolve them.
  /// This mimics what [ModuleInitializer] does in production assemblies.
  /// </summary>
  [Before(Class)]
  public static async Task RegisterTestTypesInJsonContextRegistryAsync() {
    var typeName = typeof(LifecycleTestMsg).AssemblyQualifiedName!;
    JsonContextRegistry.RegisterTypeName(typeName, typeof(LifecycleTestMsg), LifecycleTestJsonContext.Default);
    await Task.CompletedTask;
  }

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
      Hops = [_createTestHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
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
      Hops = [_createTestHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
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
      Hops = [_createTestHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
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
      Hops = [_createTestHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
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
  // DeserializeFromJsonElement - Undefined JsonElement Tests
  // ========================================

  [Test]
  public async Task DeserializeFromJsonElement_WithUndefinedValueKind_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    // Default JsonElement has ValueKind = Undefined
    var undefinedElement = default(JsonElement);

    // Act & Assert
    await Assert.That(() => deserializer.DeserializeFromJsonElement(undefinedElement, "SomeType, SomeAssembly"))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*JsonElement is Undefined*");
  }

  // ========================================
  // DeserializeFromBytes - Happy Path Tests
  // ========================================

  [Test]
  public async Task DeserializeFromBytes_WithValidJsonAndKnownType_DeserializesSuccessfullyAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var json = "{\"value\":\"hello\"}";
    var bytes = Encoding.UTF8.GetBytes(json);
    var typeName = typeof(LifecycleTestMsg).AssemblyQualifiedName!;

    // Act
    var result = deserializer.DeserializeFromBytes(bytes, typeName);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<LifecycleTestMsg>();
    await Assert.That(((LifecycleTestMsg)result).Value).IsEqualTo("hello");
  }

  // ========================================
  // DeserializeFromJsonElement - Happy Path Tests
  // ========================================

  [Test]
  public async Task DeserializeFromJsonElement_WithValidJsonAndKnownType_DeserializesSuccessfullyAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var jsonElement = JsonDocument.Parse("{\"value\":\"world\"}").RootElement.Clone();
    var typeName = typeof(LifecycleTestMsg).AssemblyQualifiedName!;

    // Act
    var result = deserializer.DeserializeFromJsonElement(jsonElement, typeName);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<LifecycleTestMsg>();
    await Assert.That(((LifecycleTestMsg)result).Value).IsEqualTo("world");
  }

  // ========================================
  // DeserializeFromEnvelope - Happy Path Tests
  // ========================================

  [Test]
  public async Task DeserializeFromEnvelope_WithValidEnvelopeTypeName_DeserializesSuccessfullyAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var jsonElement = JsonDocument.Parse("{\"value\":\"test-envelope\"}").RootElement.Clone();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = jsonElement,
      Hops = [_createTestHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Construct a valid envelope type name
    var msgType = typeof(LifecycleTestMsg);
    var envelopeTypeName = $"MessageEnvelope`1[[{msgType.AssemblyQualifiedName}]], Whizbang.Core";

    // Act
    var result = deserializer.DeserializeFromEnvelope(envelope, envelopeTypeName);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<LifecycleTestMsg>();
    await Assert.That(((LifecycleTestMsg)result).Value).IsEqualTo("test-envelope");
  }

  // ========================================
  // DeserializeFromJsonElement - Deserialization Failure Tests
  // ========================================

  [Test]
  public async Task DeserializeFromJsonElement_WithInvalidJson_ThrowsInvalidOperationExceptionAsync() {
    // Arrange - Create an options set with a known type but provide incompatible JSON
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    // This will produce a JsonElement of string type, not object
    var jsonElement = JsonDocument.Parse("\"just a string\"").RootElement.Clone();
    var typeName = typeof(LifecycleTestMsg).AssemblyQualifiedName!;

    // Act & Assert - Should throw because JsonElement is a string, not an object
    await Assert.That(() => deserializer.DeserializeFromJsonElement(jsonElement, typeName))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*Failed to deserialize message*");
  }

  // ========================================
  // WhitespaceOnly TypeName Tests
  // ========================================

  [Test]
  public async Task DeserializeFromBytes_WithWhitespaceOnlyTypeName_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = _createTestJsonOptions();
    var deserializer = new JsonLifecycleMessageDeserializer(options);
    var bytes = Encoding.UTF8.GetBytes("{}");

    // Act & Assert
    await Assert.That(() => deserializer.DeserializeFromBytes(bytes, "  \t  "))
      .Throws<ArgumentException>();
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
      Hops = [_createTestHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
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
      Hops = [_createTestHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
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
