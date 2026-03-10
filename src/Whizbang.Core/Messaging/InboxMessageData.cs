using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Strongly-typed message data for inbox processing.
/// Replaces JsonDocument MessageData in InboxRecord.
/// Supports both short property names (id, p, h) and long property names (MessageId, Payload, Hops)
/// for backwards compatibility with data serialized through interface types.
/// </summary>
[JsonConverter(typeof(InboxMessageDataConverter))]
public sealed class InboxMessageData {
  /// <summary>Message identifier for correlation.</summary>
  [JsonPropertyName("id")]
  public required MessageId MessageId { get; init; }

  /// <summary>Message payload as JsonElement (type-erased).</summary>
  [JsonPropertyName("p")]
  public required JsonElement Payload { get; init; }

  /// <summary>Message hops for observability.</summary>
  [JsonPropertyName("h")]
  public required List<MessageHop> Hops { get; init; }
}

/// <summary>
/// Custom JSON converter for InboxMessageData that accepts both short and long property names.
/// Short names: id, p, h (used by MessageEnvelope with [JsonPropertyName])
/// Long names: MessageId, Payload, Hops (used when serializing through IMessageEnvelope interface)
/// </summary>
internal sealed class InboxMessageDataConverter : JsonConverter<InboxMessageData> {
  public override InboxMessageData? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    if (reader.TokenType != JsonTokenType.StartObject) {
      throw new JsonException("Expected start of object");
    }

    // Read entire object as JsonElement first to handle property name variations
    using var doc = JsonDocument.ParseValue(ref reader);
    var root = doc.RootElement;

    // Extract MessageId - accept both short and long names
    JsonElement messageIdElem;
    if (!root.TryGetProperty("id", out messageIdElem) && !root.TryGetProperty("MessageId", out messageIdElem)) {
      throw new JsonException("Missing required property: MessageId (or id)");
    }

    // Extract Payload - accept both short and long names
    JsonElement payloadElem;
    if (!root.TryGetProperty("p", out payloadElem) && !root.TryGetProperty("Payload", out payloadElem)) {
      throw new JsonException("Missing required property: Payload (or p)");
    }

    // Extract Hops - accept both short and long names
    JsonElement hopsElem;
    if (!root.TryGetProperty("h", out hopsElem) && !root.TryGetProperty("Hops", out hopsElem)) {
      throw new JsonException("Missing required property: Hops (or h)");
    }

    // Deserialize using source-generated type info (AOT-safe)
    var messageIdTypeInfo = (JsonTypeInfo<MessageId>)options.GetTypeInfo(typeof(MessageId));
    var hopsTypeInfo = (JsonTypeInfo<List<MessageHop>>)options.GetTypeInfo(typeof(List<MessageHop>));

    // MessageId is a value type, so Deserialize returns the value directly
    var messageId = messageIdElem.Deserialize(messageIdTypeInfo);
    var hops = hopsElem.Deserialize(hopsTypeInfo)
      ?? throw new JsonException("Failed to deserialize Hops");

    return new InboxMessageData {
      MessageId = messageId,
      Payload = payloadElem.Clone(),
      Hops = hops
    };
  }

  public override void Write(Utf8JsonWriter writer, InboxMessageData value, JsonSerializerOptions options) {
    // Get source-generated type info (AOT-safe)
    var messageIdTypeInfo = (JsonTypeInfo<MessageId>)options.GetTypeInfo(typeof(MessageId));
    var jsonElementTypeInfo = (JsonTypeInfo<JsonElement>)options.GetTypeInfo(typeof(JsonElement));
    var hopsTypeInfo = (JsonTypeInfo<List<MessageHop>>)options.GetTypeInfo(typeof(List<MessageHop>));

    writer.WriteStartObject();

    writer.WritePropertyName("id");
    JsonSerializer.Serialize(writer, value.MessageId, messageIdTypeInfo);

    writer.WritePropertyName("p");
    JsonSerializer.Serialize(writer, value.Payload, jsonElementTypeInfo);

    writer.WritePropertyName("h");
    JsonSerializer.Serialize(writer, value.Hops, hopsTypeInfo);

    writer.WriteEndObject();
  }
}
