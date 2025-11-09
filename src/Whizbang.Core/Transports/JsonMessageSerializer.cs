using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
  private static readonly JsonSerializerOptions _options = new() {
    PropertyNameCaseInsensitive = true,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters = {
      new MessageIdConverter(),
      new CorrelationIdConverter(),
      new MetadataConverter(),
      new JsonStringEnumConverter()
    }
  };

  /// <inheritdoc />
  [RequiresUnreferencedCode("JSON serialization may require types that cannot be statically analyzed")]
  public Task<byte[]> SerializeAsync(IMessageEnvelope envelope) {
    // Serialize the envelope directly
    // System.Text.Json will handle the derived type (MessageEnvelope<T>)
    var json = JsonSerializer.Serialize(envelope, envelope.GetType(), _options);
    return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(json));
  }

  /// <inheritdoc />
  [RequiresUnreferencedCode("JSON deserialization may require types that cannot be statically analyzed")]
  public Task<IMessageEnvelope> DeserializeAsync<TMessage>(byte[] bytes) where TMessage : notnull {
    var json = System.Text.Encoding.UTF8.GetString(bytes);
    var envelopeType = typeof(MessageEnvelope<TMessage>);
    var envelope = JsonSerializer.Deserialize(json, envelopeType, _options) as IMessageEnvelope;

    if (envelope is null) {
      throw new InvalidOperationException($"Failed to deserialize envelope for message type {typeof(TMessage).Name}");
    }

    return Task.FromResult(envelope);
  }
}

/// <summary>
/// JSON converter for MessageId value object.
/// </summary>
internal class MessageIdConverter : JsonConverter<MessageId> {
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
internal class CorrelationIdConverter : JsonConverter<CorrelationId> {
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
