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
    EnsureRequiredConverters(_options);
  }

  /// <summary>
  /// Ensures required converters are present in the options.
  /// Adds converters only if they are not already present.
  /// </summary>
  private static void EnsureRequiredConverters(JsonSerializerOptions options) {
    // Check if converters are already present before adding
    if (!HasConverter<MessageId>(options)) {
      options.Converters.Add(new MessageIdConverter());
    }

    if (!HasConverter<CorrelationId>(options)) {
      options.Converters.Add(new CorrelationIdConverter());
    }

    if (!HasConverter<IReadOnlyDictionary<string, object>>(options)) {
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
  private static bool HasConverter<T>(JsonSerializerOptions options) {
    return options.Converters.Any(c => c.CanConvert(typeof(T)));
  }

  /// <inheritdoc />
  public Task<byte[]> SerializeAsync(IMessageEnvelope envelope) {
    string json;

    if (_context is not null) {
      // Use JsonSerializerContext path - AOT-compatible
      var typeInfo = _context.GetTypeInfo(envelope.GetType());
      if (typeInfo is null) {
        throw new InvalidOperationException($"No JsonTypeInfo found for {envelope.GetType().Name}. Ensure the message type is registered in WhizbangJsonContext.");
      }
      json = JsonSerializer.Serialize(envelope, typeInfo);
    } else {
      // Use JsonSerializerOptions path with combined resolvers
      json = JsonSerializer.Serialize(envelope, envelope.GetType(), _options!);
    }

    return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(json));
  }

  /// <inheritdoc />
  public Task<IMessageEnvelope> DeserializeAsync<TMessage>(byte[] bytes) where TMessage : notnull {
    var json = System.Text.Encoding.UTF8.GetString(bytes);
    var envelopeType = typeof(MessageEnvelope<TMessage>);
    IMessageEnvelope? envelope;

    if (_context is not null) {
      // Use JsonSerializerContext path - AOT-compatible
      var typeInfo = _context.GetTypeInfo(envelopeType);
      if (typeInfo is null) {
        throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeType.Name}. Ensure the message type is registered in WhizbangJsonContext.");
      }
      envelope = JsonSerializer.Deserialize(json, typeInfo) as IMessageEnvelope;
    } else {
      // Use JsonSerializerOptions path with combined resolvers
      envelope = JsonSerializer.Deserialize(json, envelopeType, _options!) as IMessageEnvelope;
    }

    if (envelope is null) {
      throw new InvalidOperationException($"Failed to deserialize envelope for message type {typeof(TMessage).Name}");
    }

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
/// Handles JsonElement deserialization back to original types (string, int, bool, etc.).
/// </summary>
internal class MetadataConverter : JsonConverter<IReadOnlyDictionary<string, object>?> {
  public override IReadOnlyDictionary<string, object>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    if (reader.TokenType == JsonTokenType.Null) {
      return null;
    }

    if (reader.TokenType != JsonTokenType.StartObject) {
      throw new JsonException($"Expected StartObject token, got {reader.TokenType}");
    }

    var dictionary = new Dictionary<string, object>();

    while (reader.Read()) {
      if (reader.TokenType == JsonTokenType.EndObject) {
        return dictionary;
      }

      if (reader.TokenType != JsonTokenType.PropertyName) {
        throw new JsonException($"Expected PropertyName token, got {reader.TokenType}");
      }

      var key = reader.GetString();
      if (key is null) {
        throw new JsonException("Property name cannot be null");
      }

      reader.Read();
      var value = ReadValue(ref reader);
      dictionary[key] = value;
    }

    throw new JsonException("Unexpected end of JSON");
  }

  private static object ReadValue(ref Utf8JsonReader reader) {
    return reader.TokenType switch {
      JsonTokenType.String => reader.GetString()!,
      JsonTokenType.Number when reader.TryGetInt32(out var intValue) => intValue,
      JsonTokenType.Number when reader.TryGetInt64(out var longValue) => longValue,
      JsonTokenType.Number => reader.GetDouble(),
      JsonTokenType.True => true,
      JsonTokenType.False => false,
      JsonTokenType.Null => null!,
      _ => throw new JsonException($"Unsupported JSON token type: {reader.TokenType}")
    };
  }

  public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, object>? value, JsonSerializerOptions options) {
    if (value is null) {
      writer.WriteNullValue();
      return;
    }

    writer.WriteStartObject();
    foreach (var kvp in value) {
      writer.WritePropertyName(kvp.Key);
      WriteValue(writer, kvp.Value);
    }
    writer.WriteEndObject();
  }

  private static void WriteValue(Utf8JsonWriter writer, object value) {
    switch (value) {
      case null:
        writer.WriteNullValue();
        break;
      case string s:
        writer.WriteStringValue(s);
        break;
      case int i:
        writer.WriteNumberValue(i);
        break;
      case long l:
        writer.WriteNumberValue(l);
        break;
      case double d:
        writer.WriteNumberValue(d);
        break;
      case bool b:
        writer.WriteBooleanValue(b);
        break;
      default:
        throw new JsonException($"Unsupported metadata value type: {value.GetType().Name}");
    }
  }
}
