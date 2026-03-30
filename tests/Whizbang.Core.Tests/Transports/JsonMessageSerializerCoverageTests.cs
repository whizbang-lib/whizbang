using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Additional coverage tests for JsonMessageSerializer targeting uncovered branches.
/// Focuses on: converter deduplication paths, options-only vs context-only serializer paths,
/// MetadataConverter edge cases, and _hasConverter logic.
/// </summary>
[Category("Core")]
[Category("Transports")]
[Category("Serialization")]
public class JsonMessageSerializerCoverageTests {
  // ===========================
  // _ensureRequiredConverters: MetadataConverter deduplication
  // ===========================

  [Test]
  public async Task Constructor_WithExistingMetadataConverter_ShouldNotDuplicateAsync() {
    // Arrange - Pre-add a MetadataConverter so _hasConverter<IReadOnlyDictionary<string, JsonElement>> returns true
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    options.Converters.Add(new MetadataConverter());
    var initialMetadataCount = options.Converters.Count(c =>
        c.CanConvert(typeof(IReadOnlyDictionary<string, JsonElement>)));

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert - Should not add a second MetadataConverter
    var finalMetadataCount = options.Converters.Count(c =>
        c.CanConvert(typeof(IReadOnlyDictionary<string, JsonElement>)));
    await Assert.That(finalMetadataCount).IsEqualTo(initialMetadataCount);
  }

  [Test]
  public async Task Constructor_WithAllConvertersPreAdded_ShouldNotAddAnyAsync() {
    // Arrange - Pre-add all converters that _ensureRequiredConverters would add
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    options.Converters.Add(new MessageIdConverter());
    options.Converters.Add(new CorrelationIdConverter());
    options.Converters.Add(new MetadataConverter());
    options.Converters.Add(new JsonStringEnumConverter());
    var initialCount = options.Converters.Count;

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert - No new converters should be added
    await Assert.That(options.Converters.Count).IsEqualTo(initialCount);
  }

  // ===========================
  // SerializeAsync: options-only path (_context is null)
  // ===========================

  [Test]
  [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reflection-based serialization for testing only")]
  [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Reflection-based serialization for testing only")]
  public async Task SerializeAsync_WithOptionsOnlySerializer_ShouldUseOptionsPathAsync() {
    // Arrange - Construct with options (not context) so _context is null, _options is set
    // This covers the _options?.GetTypeInfo branch returning non-null (short-circuit before _context)
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    options.Converters.Add(new MessageIdConverter());
    options.Converters.Add(new CorrelationIdConverter());
    options.Converters.Add(new MetadataConverter());
    options.Converters.Add(new JsonStringEnumConverter());
    var serializer = new JsonMessageSerializer(options);
    var envelope = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "options-only", Amount = 10 },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var bytes = await serializer.SerializeAsync(envelope);

    // Assert
    await Assert.That(bytes).IsNotNull();
    var json = Encoding.UTF8.GetString(bytes);
    await Assert.That(json).Contains("options-only");
  }

  // ===========================
  // DeserializeAsync: options-only path (_context is null)
  // ===========================

  [Test]
  [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reflection-based serialization for testing only")]
  [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Reflection-based serialization for testing only")]
  public async Task DeserializeAsync_WithOptionsOnlySerializer_ShouldUseOptionsPathAsync() {
    // Arrange - options-only serializer round-trip
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    options.Converters.Add(new MessageIdConverter());
    options.Converters.Add(new CorrelationIdConverter());
    options.Converters.Add(new MetadataConverter());
    options.Converters.Add(new JsonStringEnumConverter());
    var serializer = new JsonMessageSerializer(options);
    var original = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "options-roundtrip", Amount = 77 },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var bytes = await serializer.SerializeAsync(original);

    // Act
    var deserialized = await serializer.DeserializeAsync<SerializerTestCommand>(bytes);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    var payload = ((MessageEnvelope<SerializerTestCommand>)deserialized).Payload;
    await Assert.That(payload.Name).IsEqualTo("options-roundtrip");
    await Assert.That(payload.Amount).IsEqualTo(77);
  }

  // ===========================
  // SerializeAsync: context-only path (_options is null)
  // ===========================

  [Test]
  public async Task SerializeAsync_WithContextOnlySerializer_ShouldUseContextPathAsync() {
    // Arrange - Construct with context (not options) so _options is null, _context is set
    // This covers the _options?.GetTypeInfo returning null, then _context?.GetTypeInfo returning non-null
    var serializer = new JsonMessageSerializer(SerializerTestJsonContext.Default);
    var envelope = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "context-only", Amount = 33 },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var bytes = await serializer.SerializeAsync(envelope);

    // Assert
    await Assert.That(bytes).IsNotNull();
    var json = Encoding.UTF8.GetString(bytes);
    await Assert.That(json).Contains("context-only");
  }

  // ===========================
  // DeserializeAsync: context-only path (_options is null)
  // ===========================

  [Test]
  public async Task DeserializeAsync_WithContextOnlySerializer_ShouldUseContextPathAsync() {
    // Arrange - context-only serializer round-trip
    var serializer = new JsonMessageSerializer(SerializerTestJsonContext.Default);
    var original = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "context-roundtrip", Amount = 44 },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var bytes = await serializer.SerializeAsync(original);

    // Act
    var deserialized = await serializer.DeserializeAsync<SerializerTestCommand>(bytes);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    var payload = ((MessageEnvelope<SerializerTestCommand>)deserialized).Payload;
    await Assert.That(payload.Name).IsEqualTo("context-roundtrip");
    await Assert.That(payload.Amount).IsEqualTo(44);
  }

  // ===========================
  // SerializeAsync/DeserializeAsync: no type info found at all (both null)
  // ===========================

  [Test]
  public async Task SerializeAsync_WithContextMissingType_ShouldThrowWithDescriptiveMessageAsync() {
    // Arrange - Use InfrastructureJsonContext which does NOT include SerializerTestCommand
    var serializer = new JsonMessageSerializer(InfrastructureJsonContext.Default);
    var envelope = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "not-found", Amount = 0 },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act & Assert - context path returns null, throw path exercised
    InvalidOperationException? caughtException = null;
    try {
      await serializer.SerializeAsync(envelope);
    } catch (InvalidOperationException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("No JsonTypeInfo found");
    await Assert.That(caughtException.Message).Contains("MessageEnvelope");
  }

  [Test]
  public async Task DeserializeAsync_WithContextMissingType_ShouldThrowWithDescriptiveMessageAsync() {
    // Arrange - Serialize with known context, deserialize with context that lacks the type
    var validSerializer = new JsonMessageSerializer(SerializerTestJsonContext.Default);
    var envelope = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "not-found", Amount = 0 },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var bytes = await validSerializer.SerializeAsync(envelope);

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
    await Assert.That(caughtException.Message).Contains("MessageEnvelope");
  }

  // ===========================
  // DeserializeAsync: null deserialization result (line 111 ?? throw)
  // ===========================

  [Test]
  [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reflection-based serialization for testing only")]
  [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Reflection-based serialization for testing only")]
  public async Task DeserializeAsync_WithNullLiteralJson_ShouldThrowFailedToDeserializeAsync() {
    // Arrange - "null" JSON literal causes Deserialize to return null, hitting the ?? throw on line 111
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    options.Converters.Add(new MessageIdConverter());
    options.Converters.Add(new CorrelationIdConverter());
    options.Converters.Add(new MetadataConverter());
    options.Converters.Add(new JsonStringEnumConverter());
    var serializer = new JsonMessageSerializer(options);
    var nullJson = Encoding.UTF8.GetBytes("null");

    // Act & Assert
    InvalidOperationException? caughtException = null;
    try {
      await serializer.DeserializeAsync<SerializerTestCommand>(nullJson);
    } catch (InvalidOperationException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("Failed to deserialize envelope");
  }

  // ===========================
  // Constructor null checks
  // ===========================

  [Test]
  public async Task Constructor_WithNullContext_ShouldThrowArgumentNullExceptionAsync() {
    // Act & Assert
    ArgumentNullException? caughtException = null;
    try {
      _ = new JsonMessageSerializer((JsonSerializerContext)null!);
    } catch (ArgumentNullException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.ParamName).IsEqualTo("context");
  }

  [Test]
  public async Task Constructor_WithNullOptions_ShouldThrowArgumentNullExceptionAsync() {
    // Act & Assert
    ArgumentNullException? caughtException = null;
    try {
      _ = new JsonMessageSerializer((JsonSerializerOptions)null!);
    } catch (ArgumentNullException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task Constructor_WithOptionsWithoutTypeInfoResolver_ShouldThrowArgumentExceptionAsync() {
    // Arrange
    var options = new JsonSerializerOptions();

    // Act & Assert
    ArgumentException? caughtException = null;
    try {
      _ = new JsonMessageSerializer(options);
    } catch (ArgumentException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("TypeInfoResolver");
    await Assert.That(caughtException.ParamName).IsEqualTo("options");
  }

  // ===========================
  // _ensureRequiredConverters: each individual converter addition
  // ===========================

  [Test]
  public async Task Constructor_WithNoConverters_ShouldAddAllRequiredConvertersAsync() {
    // Arrange - Fresh options with no converters at all
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert - All four converter types should be present
    var hasMessageId = options.Converters.Any(c => c is MessageIdConverter);
    var hasCorrelationId = options.Converters.Any(c => c is CorrelationIdConverter);
    var hasMetadata = options.Converters.Any(c =>
        c.CanConvert(typeof(IReadOnlyDictionary<string, JsonElement>)));
    var hasEnum = options.Converters.Any(c => c is JsonStringEnumConverter);

    await Assert.That(hasMessageId).IsTrue();
    await Assert.That(hasCorrelationId).IsTrue();
    await Assert.That(hasMetadata).IsTrue();
    await Assert.That(hasEnum).IsTrue();
    await Assert.That(options.Converters.Count).IsGreaterThanOrEqualTo(4);
  }

  [Test]
  public async Task Constructor_WithOnlyMessageIdConverter_ShouldAddRemainingConvertersAsync() {
    // Arrange - Pre-add only MessageIdConverter
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    options.Converters.Add(new MessageIdConverter());

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert - MessageIdConverter should not be duplicated, others should be added
    var messageIdCount = options.Converters.Count(c => c is MessageIdConverter);
    var hasCorrelationId = options.Converters.Any(c => c is CorrelationIdConverter);
    var hasMetadata = options.Converters.Any(c =>
        c.CanConvert(typeof(IReadOnlyDictionary<string, JsonElement>)));
    var hasEnum = options.Converters.Any(c => c is JsonStringEnumConverter);

    await Assert.That(messageIdCount).IsEqualTo(1);
    await Assert.That(hasCorrelationId).IsTrue();
    await Assert.That(hasMetadata).IsTrue();
    await Assert.That(hasEnum).IsTrue();
  }

  [Test]
  public async Task Constructor_WithOnlyCorrelationIdConverter_ShouldAddRemainingConvertersAsync() {
    // Arrange - Pre-add only CorrelationIdConverter
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    options.Converters.Add(new CorrelationIdConverter());

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert
    var correlationIdCount = options.Converters.Count(c => c is CorrelationIdConverter);
    var hasMessageId = options.Converters.Any(c => c is MessageIdConverter);
    var hasMetadata = options.Converters.Any(c =>
        c.CanConvert(typeof(IReadOnlyDictionary<string, JsonElement>)));
    var hasEnum = options.Converters.Any(c => c is JsonStringEnumConverter);

    await Assert.That(correlationIdCount).IsEqualTo(1);
    await Assert.That(hasMessageId).IsTrue();
    await Assert.That(hasMetadata).IsTrue();
    await Assert.That(hasEnum).IsTrue();
  }

  [Test]
  public async Task Constructor_WithOnlyEnumConverter_ShouldAddRemainingConvertersAsync() {
    // Arrange - Pre-add only JsonStringEnumConverter
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    options.Converters.Add(new JsonStringEnumConverter());

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert
    var enumCount = options.Converters.Count(c => c is JsonStringEnumConverter);
    var hasMessageId = options.Converters.Any(c => c is MessageIdConverter);
    var hasCorrelationId = options.Converters.Any(c => c is CorrelationIdConverter);
    var hasMetadata = options.Converters.Any(c =>
        c.CanConvert(typeof(IReadOnlyDictionary<string, JsonElement>)));

    await Assert.That(enumCount).IsEqualTo(1);
    await Assert.That(hasMessageId).IsTrue();
    await Assert.That(hasCorrelationId).IsTrue();
    await Assert.That(hasMetadata).IsTrue();
  }

  // ===========================
  // Full round-trip with hops/metadata via options-only path
  // ===========================

  [Test]
  [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reflection-based serialization for testing only")]
  [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Reflection-based serialization for testing only")]
  public async Task RoundTrip_WithOptionsOnly_HopsAndMetadata_ShouldPreserveAllDataAsync() {
    // Arrange - Use options-only path (exercises custom converters for Write and Read)
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    // Let _ensureRequiredConverters add them (no pre-add)
    var serializer = new JsonMessageSerializer(options);
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var metadata = new Dictionary<string, JsonElement> {
      ["str"] = JsonSerializer.SerializeToElement("hello"),
      ["num"] = JsonSerializer.SerializeToElement(42),
      ["flag"] = JsonSerializer.SerializeToElement(true),
      ["nested"] = JsonSerializer.SerializeToElement(new { inner = "data" }),
      ["arr"] = JsonSerializer.SerializeToElement(new List<int> { 1, 2, 3 })
    };

    var original = new MessageEnvelope<SerializerTestCommand> {
      MessageId = messageId,
      Payload = new SerializerTestCommand { Name = "full-coverage", Amount = 100 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "CoverageService",
            InstanceId = Guid.NewGuid(),
            HostName = "coverage-host",
            ProcessId = 9999
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = correlationId,
          Metadata = metadata
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<SerializerTestCommand>(bytes);

    // Assert
    await Assert.That(deserialized.MessageId).IsEqualTo(messageId);
    await Assert.That(deserialized.Hops).Count().IsEqualTo(1);
    await Assert.That(deserialized.Hops[0].CorrelationId).IsEqualTo(correlationId);
    await Assert.That(deserialized.Hops[0].ServiceInstance.ServiceName).IsEqualTo("CoverageService");

    var hopMetadata = deserialized.Hops[0].Metadata;
    await Assert.That(hopMetadata).IsNotNull();
    await Assert.That(hopMetadata!["str"].GetString()).IsEqualTo("hello");
    await Assert.That(hopMetadata["num"].GetInt32()).IsEqualTo(42);
    await Assert.That(hopMetadata["flag"].GetBoolean()).IsTrue();
    await Assert.That(hopMetadata["nested"].ValueKind).IsEqualTo(JsonValueKind.Object);
    await Assert.That(hopMetadata["arr"].ValueKind).IsEqualTo(JsonValueKind.Array);
  }

  [Test]
  [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reflection-based serialization for testing only")]
  [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Reflection-based serialization for testing only")]
  public async Task RoundTrip_WithOptionsOnly_NullMetadataAndCorrelation_ShouldPreserveNullsAsync() {
    // Arrange - Options-only path with null metadata and null correlationId
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    var serializer = new JsonMessageSerializer(options);
    var original = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "null-coverage", Amount = 0 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "NullTest",
            InstanceId = Guid.NewGuid(),
            HostName = "null-host",
            ProcessId = 1
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = null,
          Metadata = null
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<SerializerTestCommand>(bytes);

    // Assert
    await Assert.That(deserialized.Hops[0].CorrelationId).IsNull();
    await Assert.That(deserialized.Hops[0].Metadata).IsNull();
  }

  // ===========================
  // MetadataConverter: edge case - "Unexpected end of JSON" path (line 183)
  // ===========================

  [Test]
  public async Task MetadataConverter_Read_WithTruncatedJson_ShouldThrowJsonExceptionAsync() {
    // Arrange - Truncated JSON object that runs out before EndObject
    // This tests the "Unexpected end of JSON" throw at line 183
    var converter = new MetadataConverter();
    // A JSON object missing its closing brace - reader.Read() will return false
    var truncatedJson = Encoding.UTF8.GetBytes("{\"key\":\"value\"");

    // Act & Assert
    JsonException? caughtException = null;
    try {
      var reader = new Utf8JsonReader(truncatedJson);
      reader.Read(); // StartObject
      converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    // Either our custom message or the system's JSON reader exception
    await Assert.That(caughtException!.Message.Length).IsGreaterThan(0);
  }

  // ===========================
  // MetadataConverter: Write with various JsonElement value kinds
  // ===========================

  [Test]
  public async Task MetadataConverter_Write_WithNestedObjectsAndArrays_ShouldSerializeAsync() {
    // Arrange - Complex metadata with nested objects and arrays to exercise WriteTo
    var converter = new MetadataConverter();
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);
    var dictionary = new Dictionary<string, JsonElement> {
      ["obj"] = JsonSerializer.SerializeToElement(new { a = 1, b = "two" }),
      ["arr"] = JsonSerializer.SerializeToElement(new List<string> { "x", "y", "z" }),
      ["nullVal"] = JsonSerializer.SerializeToElement<string?>(null),
      ["boolVal"] = JsonSerializer.SerializeToElement(false),
      ["numVal"] = JsonSerializer.SerializeToElement(999.99)
    };

    // Act
    converter.Write(writer, dictionary, JsonSerializerOptions.Default);
    writer.Flush();

    // Assert - Parse back and verify structure
    var json = Encoding.UTF8.GetString(stream.ToArray());
    var doc = JsonDocument.Parse(json);
    await Assert.That(doc.RootElement.GetProperty("obj").ValueKind).IsEqualTo(JsonValueKind.Object);
    await Assert.That(doc.RootElement.GetProperty("arr").ValueKind).IsEqualTo(JsonValueKind.Array);
    await Assert.That(doc.RootElement.GetProperty("nullVal").ValueKind).IsEqualTo(JsonValueKind.Null);
    await Assert.That(doc.RootElement.GetProperty("boolVal").GetBoolean()).IsFalse();
    await Assert.That(doc.RootElement.GetProperty("numVal").GetDouble()).IsEqualTo(999.99);
  }

  // ===========================
  // _hasConverter: verify generic type matching behavior
  // ===========================

  [Test]
  public async Task Constructor_WithOnlyMetadataConverter_ShouldAddOtherConvertersAsync() {
    // Arrange - Pre-add only MetadataConverter to test _hasConverter for other types
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    options.Converters.Add(new MetadataConverter());

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert - MetadataConverter not duplicated, others added
    var metadataCount = options.Converters.Count(c =>
        c.CanConvert(typeof(IReadOnlyDictionary<string, JsonElement>)));
    var hasMessageId = options.Converters.Any(c => c is MessageIdConverter);
    var hasCorrelationId = options.Converters.Any(c => c is CorrelationIdConverter);
    var hasEnum = options.Converters.Any(c => c is JsonStringEnumConverter);

    await Assert.That(metadataCount).IsEqualTo(1);
    await Assert.That(hasMessageId).IsTrue();
    await Assert.That(hasCorrelationId).IsTrue();
    await Assert.That(hasEnum).IsTrue();
  }

  // ===========================
  // SerializeAsync: verify exact type used in GetTypeInfo
  // ===========================

  [Test]
  [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reflection-based serialization for testing only")]
  [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Reflection-based serialization for testing only")]
  public async Task SerializeAsync_WithOptionsOnly_ShouldUseEnvelopeRuntimeTypeAsync() {
    // Arrange - Ensure GetType() on envelope returns the correct type for GetTypeInfo lookup
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var serializer = new JsonMessageSerializer(options);
    IMessageEnvelope envelope = new MessageEnvelope<SerializerTestCommand> {
      MessageId = MessageId.New(),
      Payload = new SerializerTestCommand { Name = "runtime-type", Amount = 5 },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act - Pass as IMessageEnvelope (the parameter type) to verify GetType() usage
    var bytes = await serializer.SerializeAsync(envelope);

    // Assert
    var json = Encoding.UTF8.GetString(bytes);
    await Assert.That(json).Contains("runtime-type");
  }

  // ===========================
  // DeserializeAsync: invalid JSON through options path
  // ===========================

  [Test]
  [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reflection-based serialization for testing only")]
  [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Reflection-based serialization for testing only")]
  public async Task DeserializeAsync_WithOptionsAndInvalidJson_ShouldThrowJsonExceptionAsync() {
    // Arrange - Options-only path with invalid JSON
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var serializer = new JsonMessageSerializer(options);
    var invalidBytes = Encoding.UTF8.GetBytes("{not valid json}");

    // Act & Assert
    JsonException? caughtException = null;
    try {
      await serializer.DeserializeAsync<SerializerTestCommand>(invalidBytes);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
  }
}
