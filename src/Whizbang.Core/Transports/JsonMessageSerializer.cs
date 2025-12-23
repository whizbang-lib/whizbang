using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Transports;

/// <summary>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:SerializeAsync_WithValidEnvelope_ShouldSerializeAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:SerializeAsync_WithMetadataContainingAllTypes_ShouldSerializeAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:SerializeAsync_WithNullMetadata_ShouldHandleNullAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:DeserializeAsync_WithInvalidJson_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:DeserializeAsync_WithInvalidMessageId_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:DeserializeAsync_WithNullMessageId_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:DeserializeAsync_WithInvalidCorrelationId_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:DeserializeAsync_WithNullCorrelationId_ShouldHandleGracefullyAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:SerializeAsync_WithValidMessageId_ShouldSerializeAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:SerializeAsync_WithValidCorrelationId_ShouldSerializeAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:Metadata_WithInvalidStartToken_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:Metadata_WithInvalidPropertyToken_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:Metadata_WithUnsupportedValueType_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:Metadata_WithUnsupportedWriteType_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:Metadata_WithNullPropertyName_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:Metadata_WithDoubleValue_ShouldRoundTripAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:Metadata_WithLargeInt64Value_ShouldRoundTripAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/JsonMessageSerializerTests.cs:RoundTrip_WithComplexEnvelope_ShouldPreserveAllDataAsync</tests>
/// JSON-based message serializer using System.Text.Json.
/// Preserves all MessageEnvelope metadata including hops, IDs, and routing information.
/// </summary>
public class JsonMessageSerializer : IMessageSerializer {
  private readonly JsonSerializerContext? _context;
  private readonly JsonSerializerOptions? _options;

  /// <summary>
  /// Creates an AOT-compatible JsonMessageSerializer using the provided JsonSerializerContext.
  /// This constructor enables zero-reflection serialization for native AOT compilation.
  /// </summary>
  /// <param name="context">JsonSerializerContext containing JsonTypeInfo for all message types.</param>
  public JsonMessageSerializer(JsonSerializerContext context) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
  }

  /// <summary>
  /// Creates an AOT-compatible JsonMessageSerializer using the provided JsonSerializerOptions.
  /// Use this constructor when combining WhizbangJsonContext with user-defined contexts.
  /// </summary>
  /// <param name="options">JsonSerializerOptions with TypeInfoResolver configured.</param>
  public JsonMessageSerializer(JsonSerializerOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    if (options.TypeInfoResolver is null) {
      throw new ArgumentException("JsonSerializerOptions must have a TypeInfoResolver configured.", nameof(options));
    }

    _options = options;
    _ensureRequiredConverters(_options);
  }

  /// <summary>
  /// Ensures required converters are present in the options.
  /// Adds converters only if they are not already present.
  /// </summary>
  private static void _ensureRequiredConverters(JsonSerializerOptions options) {
    // Check if converters are already present before adding
    if (!_hasConverter<MessageId>(options)) {
      options.Converters.Add(new MessageIdConverter());
    }

    if (!_hasConverter<CorrelationId>(options)) {
      options.Converters.Add(new CorrelationIdConverter());
    }

    if (!_hasConverter<IReadOnlyDictionary<string, JsonElement>>(options)) {
      options.Converters.Add(new MetadataConverter());
    }

    // Add JsonStringEnumConverter if not present
    if (!options.Converters.Any(c => c is JsonStringEnumConverter)) {
      options.Converters.Add(new JsonStringEnumConverter());
    }
  }

  /// <summary>
  /// Checks if a converter for the specified type is already present in the options.
  /// </summary>
  private static bool _hasConverter<T>(JsonSerializerOptions options) {
    return options.Converters.Any(c => c.CanConvert(typeof(T)));
  }

  /// <inheritdoc />
  public Task<byte[]> SerializeAsync(IMessageEnvelope envelope) {
    // Get JsonTypeInfo from options or context - fully AOT-compatible
    var envelopeType = envelope.GetType();
    var typeInfo = (_options?.GetTypeInfo(envelopeType) ?? _context?.GetTypeInfo(envelopeType)) ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeType.Name}. Ensure the message type is registered in WhizbangJsonContext.");
    var json = JsonSerializer.Serialize(envelope, typeInfo);
    return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(json));
  }

  /// <inheritdoc />
  public Task<IMessageEnvelope> DeserializeAsync<TMessage>(byte[] bytes) where TMessage : notnull {
    var json = System.Text.Encoding.UTF8.GetString(bytes);
    var envelopeType = typeof(MessageEnvelope<TMessage>);

    // Get JsonTypeInfo from options or context - fully AOT-compatible
    var typeInfo = (_options?.GetTypeInfo(envelopeType) ?? _context?.GetTypeInfo(envelopeType)) ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeType.Name}. Ensure the message type is registered in WhizbangJsonContext.");
    var envelope = JsonSerializer.Deserialize(json, typeInfo) as IMessageEnvelope ?? throw new InvalidOperationException($"Failed to deserialize envelope for message type {typeof(TMessage).Name}");
    return Task.FromResult(envelope);
  }
}

/// <summary>
/// JSON converter for MessageId value object.
/// </summary>
public class MessageIdConverter : JsonConverter<MessageId> {
  public override MessageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    var guidString = reader.GetString();
    if (guidString is null || !Guid.TryParse(guidString, out var guid)) {
      throw new JsonException($"Invalid MessageId format");
    }
    return MessageId.From(guid);
  }

  public override void Write(Utf8JsonWriter writer, MessageId value, JsonSerializerOptions options) {
    writer.WriteStringValue(value.Value.ToString());
  }
}

/// <summary>
/// JSON converter for CorrelationId value object.
/// </summary>
public class CorrelationIdConverter : JsonConverter<CorrelationId> {
  public override CorrelationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    var guidString = reader.GetString();
    if (guidString is null || !Guid.TryParse(guidString, out var guid)) {
      throw new JsonException($"Invalid CorrelationId format");
    }
    return CorrelationId.From(guid);
  }

  public override void Write(Utf8JsonWriter writer, CorrelationId value, JsonSerializerOptions options) {
    writer.WriteStringValue(value.Value.ToString());
  }
}

/// <summary>
/// JSON converter for metadata dictionaries.
/// Supports any JSON value type (string, number, boolean, object, array) via JsonElement.
/// </summary>
internal class MetadataConverter : JsonConverter<IReadOnlyDictionary<string, JsonElement>?> {
  public override IReadOnlyDictionary<string, JsonElement>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    if (reader.TokenType == JsonTokenType.Null) {
      return null;
    }

    if (reader.TokenType != JsonTokenType.StartObject) {
      throw new JsonException($"Expected StartObject token, got {reader.TokenType}");
    }

    var dictionary = new Dictionary<string, JsonElement>();

    while (reader.Read()) {
      if (reader.TokenType == JsonTokenType.EndObject) {
        return dictionary;
      }

      if (reader.TokenType != JsonTokenType.PropertyName) {
        throw new JsonException($"Expected PropertyName token, got {reader.TokenType}");
      }

      var key = reader.GetString() ?? throw new JsonException("Property name cannot be null");

      // Read the value as a JsonElement to preserve its type
      reader.Read();
      var value = JsonElement.ParseValue(ref reader);
      dictionary[key] = value;
    }

    throw new JsonException("Unexpected end of JSON");
  }

  public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, JsonElement>? value, JsonSerializerOptions options) {
    if (value is null) {
      writer.WriteNullValue();
      return;
    }

    writer.WriteStartObject();
    foreach (var kvp in value) {
      writer.WritePropertyName(kvp.Key);
      kvp.Value.WriteTo(writer);
    }
    writer.WriteEndObject();
  }
}
