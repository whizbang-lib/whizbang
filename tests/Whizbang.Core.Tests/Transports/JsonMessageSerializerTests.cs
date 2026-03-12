using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Test command for JsonMessageSerializer serialization tests.
/// </summary>
public record SerializerTestCommand : ICommand {
  public required string Name { get; init; }
  public required int Amount { get; init; }
}

/// <summary>
/// Source-generated JSON context for serializer test types.
/// </summary>
[JsonSerializable(typeof(SerializerTestCommand))]
[JsonSerializable(typeof(MessageEnvelope<SerializerTestCommand>))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class SerializerTestJsonContext : JsonSerializerContext {
}

/// <summary>
/// Tests for JsonMessageSerializer constructor validation, converter integration,
/// serialization/deserialization paths, and error handling.
/// </summary>
[Category("Core")]
[Category("Transports")]
public class JsonMessageSerializerTests {
  [Test]
  public async Task Constructor_WithNullTypeInfoResolver_ShouldThrowArgumentExceptionAsync() {
    // Arrange - Create options without TypeInfoResolver (default is null)
    var options = new JsonSerializerOptions();

    // Act - Call constructor directly to ensure coverage is collected
    ArgumentException? caughtException = null;
    try {
      _ = new JsonMessageSerializer(options);
    } catch (ArgumentException ex) {
      caughtException = ex;
    }

    // Assert - Should have thrown ArgumentException with correct message
    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("TypeInfoResolver");
  }

  [Test]
  public async Task Constructor_WithNullOptions_ShouldThrowArgumentNullExceptionAsync() {
    // Act - Call constructor directly to ensure coverage is collected
    ArgumentNullException? caughtException = null;
    try {
      _ = new JsonMessageSerializer((JsonSerializerOptions)null!);
    } catch (ArgumentNullException ex) {
      caughtException = ex;
    }

    // Assert - Should have thrown ArgumentNullException
    await Assert.That(caughtException).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithValidTypeInfoResolver_ShouldSucceedAsync() {
    // Arrange - Create options with a valid TypeInfoResolver
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    // Act - Should not throw
    var serializer = new JsonMessageSerializer(options);

    // Assert - Serializer was created successfully
    await Assert.That(serializer).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithValidOptions_ShouldAddRequiredConvertersAsync() {
    // Arrange - Create options with TypeInfoResolver but no converters
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert - Required converters should be added
    var hasMessageIdConverter = options.Converters.Any(c => c is MessageIdConverter);
    var hasCorrelationIdConverter = options.Converters.Any(c => c is CorrelationIdConverter);
    var hasEnumConverter = options.Converters.Any(c => c is JsonStringEnumConverter);

    await Assert.That(hasMessageIdConverter).IsTrue();
    await Assert.That(hasCorrelationIdConverter).IsTrue();
    await Assert.That(hasEnumConverter).IsTrue();
  }

  [Test]
  public async Task Constructor_WithExistingConverters_ShouldNotDuplicateAsync() {
    // Arrange - Create options with converters already present
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    options.Converters.Add(new MessageIdConverter());
    options.Converters.Add(new CorrelationIdConverter());
    var initialCount = options.Converters.Count;

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert - Should not add duplicate converters for MessageId and CorrelationId
    var messageIdConverterCount = options.Converters.Count(c => c is MessageIdConverter);
    var correlationIdConverterCount = options.Converters.Count(c => c is CorrelationIdConverter);

    await Assert.That(messageIdConverterCount).IsEqualTo(1);
    await Assert.That(correlationIdConverterCount).IsEqualTo(1);
  }

  [Test]
  public async Task Constructor_WithNullContext_ShouldThrowArgumentNullExceptionAsync() {
    // Act - Call constructor with null context
    ArgumentNullException? caughtException = null;
    try {
      _ = new JsonMessageSerializer((JsonSerializerContext)null!);
    } catch (ArgumentNullException ex) {
      caughtException = ex;
    }

    // Assert
    await Assert.That(caughtException).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithValidTypeInfoResolverChain_ShouldSucceedAsync() {
    // Arrange - Create options with TypeInfoResolverChain
    var options = new JsonSerializerOptions();
    options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());

    // Act - Should not throw
    var serializer = new JsonMessageSerializer(options);

    // Assert
    await Assert.That(serializer).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithOptionsWithExistingEnumConverter_ShouldNotDuplicateAsync() {
    // Arrange
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    options.Converters.Add(new JsonStringEnumConverter());
    var initialConverterCount = options.Converters.Count(c => c is JsonStringEnumConverter);

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert - Should not add another JsonStringEnumConverter
    var finalConverterCount = options.Converters.Count(c => c is JsonStringEnumConverter);
    await Assert.That(finalConverterCount).IsEqualTo(initialConverterCount);
  }

  [Test]
  public async Task Constructor_WithValidOptions_ShouldAddMetadataConverterAsync() {
    // Arrange
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert - MetadataConverter should be added (it's internal so check by CanConvert)
    var hasMetadataConverter = options.Converters.Any(c =>
        c.CanConvert(typeof(IReadOnlyDictionary<string, JsonElement>)));

    await Assert.That(hasMetadataConverter).IsTrue();
  }

  [Test]
  public async Task Constructor_WithEmptyOptionsTypeInfoResolver_ShouldThrowArgumentExceptionAsync() {
    // Arrange - Create options with an empty TypeInfoResolverChain
    var options = new JsonSerializerOptions();
    // Don't add any resolver, but make sure it's not null by accessing the chain
    _ = options.TypeInfoResolverChain.Count; // Just accessing to ensure it exists but is empty

    // Act & Assert
    ArgumentException? caughtException = null;
    try {
      _ = new JsonMessageSerializer(options);
    } catch (ArgumentException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("TypeInfoResolver");
  }

  // ===========================
  // SerializeAsync Tests
  // ===========================

  [Test]
  public async Task SerializeAsync_WithValidEnvelope_ShouldReturnBytesAsync() {
    // Arrange - Use combined options that include our test context
    var options = _createOptionsWithTestContext();
    var serializer = new JsonMessageSerializer(options);
    var envelope = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "test-payload", Amount = 42 },
      Hops = []
    };

    // Act
    var bytes = await serializer.SerializeAsync(envelope);

    // Assert
    await Assert.That(bytes).IsNotNull();
    var json = Encoding.UTF8.GetString(bytes);
    await Assert.That(json).Contains("test-payload");
    await Assert.That(json).Contains("42");
  }

  [Test]
  public async Task SerializeAsync_WithContextConstructor_ShouldReturnBytesAsync() {
    // Arrange - Use the context constructor path
    var serializer = new JsonMessageSerializer(SerializerTestJsonContext.Default);
    var envelope = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "context-test", Amount = 99 },
      Hops = []
    };

    // Act
    var bytes = await serializer.SerializeAsync(envelope);

    // Assert
    await Assert.That(bytes).IsNotNull();
    var json = Encoding.UTF8.GetString(bytes);
    await Assert.That(json).Contains("context-test");
  }

  [Test]
  public async Task SerializeAsync_WithNoTypeInfo_ShouldThrowInvalidOperationExceptionAsync() {
    // Arrange - Use a context that does NOT include our test type
    var serializer = new JsonMessageSerializer(InfrastructureJsonContext.Default);
    var envelope = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "missing", Amount = 1 },
      Hops = []
    };

    // Act & Assert
    InvalidOperationException? caughtException = null;
    try {
      await serializer.SerializeAsync(envelope);
    } catch (InvalidOperationException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("No JsonTypeInfo found");
  }

  // ===========================
  // DeserializeAsync Tests
  // ===========================

  [Test]
  public async Task DeserializeAsync_WithValidBytes_ShouldReturnEnvelopeAsync() {
    // Arrange
    var options = _createOptionsWithTestContext();
    var serializer = new JsonMessageSerializer(options);
    var original = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "roundtrip", Amount = 7 },
      Hops = []
    };
    var bytes = await serializer.SerializeAsync(original);

    // Act
    var deserialized = await serializer.DeserializeAsync<SerializerTestCommand>(bytes);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized.MessageId).IsEqualTo(original.MessageId);
    var payload = ((MessageEnvelope<SerializerTestCommand>)deserialized).Payload;
    await Assert.That(payload.Name).IsEqualTo("roundtrip");
    await Assert.That(payload.Amount).IsEqualTo(7);
  }

  [Test]
  public async Task DeserializeAsync_WithContextConstructor_ShouldReturnEnvelopeAsync() {
    // Arrange - Use the context constructor path
    var serializer = new JsonMessageSerializer(SerializerTestJsonContext.Default);
    var original = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "context-roundtrip", Amount = 3 },
      Hops = []
    };
    var bytes = await serializer.SerializeAsync(original);

    // Act
    var deserialized = await serializer.DeserializeAsync<SerializerTestCommand>(bytes);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    var payload = ((MessageEnvelope<SerializerTestCommand>)deserialized).Payload;
    await Assert.That(payload.Name).IsEqualTo("context-roundtrip");
  }

  [Test]
  public async Task DeserializeAsync_WithNoTypeInfo_ShouldThrowInvalidOperationExceptionAsync() {
    // Arrange - Serialize with a valid context, then try to deserialize with one that doesn't know the type
    var options = _createOptionsWithTestContext();
    var serializer = new JsonMessageSerializer(options);
    var original = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "missing", Amount = 1 },
      Hops = []
    };
    var bytes = await serializer.SerializeAsync(original);

    // Now create a serializer that does NOT have the test type
    var limitedSerializer = new JsonMessageSerializer(InfrastructureJsonContext.Default);

    // Act & Assert
    InvalidOperationException? caughtException = null;
    try {
      await limitedSerializer.DeserializeAsync<SerializerTestCommand>(bytes);
    } catch (InvalidOperationException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("No JsonTypeInfo found");
  }

  [Test]
  public async Task DeserializeAsync_WithInvalidJson_ShouldThrowJsonExceptionAsync() {
    // Arrange
    var options = _createOptionsWithTestContext();
    var serializer = new JsonMessageSerializer(options);
    var invalidJson = Encoding.UTF8.GetBytes("{ invalid json }");

    // Act & Assert
    JsonException? caughtException = null;
    try {
      await serializer.DeserializeAsync<SerializerTestCommand>(invalidJson);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
  }

  [Test]
  public async Task RoundTrip_WithHopsAndMetadata_ShouldPreserveAllDataAsync() {
    // Arrange
    var options = _createOptionsWithTestContext();
    var serializer = new JsonMessageSerializer(options);
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var metadata = new Dictionary<string, JsonElement> {
      ["key1"] = JsonSerializer.SerializeToElement("value1"),
      ["key2"] = JsonSerializer.SerializeToElement(123)
    };

    var original = new MessageEnvelope<SerializerTestCommand> {
      MessageId = messageId,
      Payload = new SerializerTestCommand { Name = "full-roundtrip", Amount = 55 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = correlationId,
          Metadata = metadata
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<SerializerTestCommand>(bytes);

    // Assert
    await Assert.That(deserialized.MessageId).IsEqualTo(messageId);
    await Assert.That(deserialized.Hops).Count().IsEqualTo(1);
    await Assert.That(deserialized.Hops[0].CorrelationId).IsEqualTo(correlationId);
    await Assert.That(deserialized.Hops[0].ServiceInstance.ServiceName).IsEqualTo("TestService");

    var hopMetadata = deserialized.Hops[0].Metadata;
    if (hopMetadata == null) {
      throw new InvalidOperationException("Test failed: Expected metadata to be non-null");
    }
    await Assert.That(hopMetadata["key1"].GetString()).IsEqualTo("value1");
    await Assert.That(hopMetadata["key2"].GetInt32()).IsEqualTo(123);
  }

  [Test]
  public async Task RoundTrip_WithNullMetadata_ShouldPreserveNullAsync() {
    // Arrange
    var options = _createOptionsWithTestContext();
    var serializer = new JsonMessageSerializer(options);
    var original = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "null-metadata", Amount = 0 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Test",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 1
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Metadata = null
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<SerializerTestCommand>(bytes);

    // Assert - Metadata should remain null after round-trip
    await Assert.That(deserialized.Hops[0].Metadata!).IsNull();
  }

  // ===========================
  // Helper Methods
  // ===========================

  /// <summary>
  /// Creates JsonSerializerOptions that include both the test context and infrastructure context.
  /// </summary>
  private static JsonSerializerOptions _createOptionsWithTestContext() {
    var options = new JsonSerializerOptions {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(
        SerializerTestJsonContext.Default,
        InfrastructureJsonContext.Default
      ),
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    options.Converters.Add(new MessageIdConverter());
    options.Converters.Add(new CorrelationIdConverter());
    options.Converters.Add(new MetadataConverter());
    options.Converters.Add(new JsonStringEnumConverter());
    return options;
  }
}
