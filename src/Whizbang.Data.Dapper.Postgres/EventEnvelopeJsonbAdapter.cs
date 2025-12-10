using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Data;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Adapts IMessageEnvelope to the 3-column JSONB pattern for Event Store persistence.
/// Splits envelope into: event_data (payload), metadata (correlation/causation/hops), scope (tenant/user).
/// </summary>
/// <remarks>
/// Constructor that accepts JsonSerializerOptions with a source-generated context.
/// This is the AOT-compatible way to use this adapter.
/// </remarks>
public class EventEnvelopeJsonbAdapter(JsonSerializerOptions jsonOptions) : IJsonbPersistenceAdapter<IMessageEnvelope> {
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));

  public JsonbPersistenceModel ToJsonb(IMessageEnvelope source, PolicyConfiguration? policyConfig = null) {
    ArgumentNullException.ThrowIfNull(source);

    // 1. Event data: The actual event payload (AOT-compatible)
    var payload = source.Payload;
    var payloadType = payload.GetType();
    var payloadTypeInfo = _jsonOptions.GetTypeInfo(payloadType) ?? throw new InvalidOperationException($"No JsonTypeInfo found for {payloadType.Name}. Ensure the message type is registered in WhizbangJsonContext.");
    var eventDataJson = JsonSerializer.Serialize(payload, payloadTypeInfo);

    // 2. Metadata: Correlation, Causation, Hops, Message ID (AOT-compatible with JsonElement)
    var correlationId = source.GetCorrelationId();
    var causationId = source.GetCausationId();

    // Serialize individual values using JsonTypeInfo
    var stringTypeInfo = _jsonOptions.GetTypeInfo(typeof(string)) ?? throw new InvalidOperationException("No JsonTypeInfo found for string");
    var hopsTypeInfo = _jsonOptions.GetTypeInfo(typeof(List<MessageHop>)) ?? throw new InvalidOperationException("No JsonTypeInfo found for List<MessageHop>");

    var messageIdJson = JsonSerializer.Serialize(source.MessageId.Value.ToString(), stringTypeInfo);
    var correlationIdJson = JsonSerializer.Serialize(correlationId?.Value.ToString() ?? string.Empty, stringTypeInfo);
    var causationIdJson = JsonSerializer.Serialize(causationId?.Value.ToString() ?? string.Empty, stringTypeInfo);
    var hopsJson = JsonSerializer.Serialize(source.Hops.ToList(), hopsTypeInfo);

    var metadataDict = new Dictionary<string, JsonElement> {
      ["message_id"] = JsonDocument.Parse(messageIdJson).RootElement.Clone(),
      ["correlation_id"] = JsonDocument.Parse(correlationIdJson).RootElement.Clone(),
      ["causation_id"] = JsonDocument.Parse(causationIdJson).RootElement.Clone(),
      ["hops"] = JsonDocument.Parse(hopsJson).RootElement.Clone()
    };

    var metadataDictTypeInfo = _jsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>)) ?? throw new InvalidOperationException("No JsonTypeInfo found for Dictionary<string, JsonElement>. Ensure the type is registered in WhizbangJsonContext.");
    var metadataJson = JsonSerializer.Serialize(metadataDict, metadataDictTypeInfo);

    // 3. Scope: Extract from first hop's SecurityContext if available (AOT-compatible with JsonElement)
    string? scopeJson = null;
    var firstHop = source.Hops.FirstOrDefault();
    if (firstHop?.SecurityContext != null) {
      var scopeDict = new Dictionary<string, JsonElement?> {
        ["tenant_id"] = !string.IsNullOrEmpty(firstHop.SecurityContext.TenantId)
          ? JsonDocument.Parse(JsonSerializer.Serialize(firstHop.SecurityContext.TenantId, stringTypeInfo)).RootElement.Clone()
          : (JsonElement?)null,
        ["user_id"] = !string.IsNullOrEmpty(firstHop.SecurityContext.UserId)
          ? JsonDocument.Parse(JsonSerializer.Serialize(firstHop.SecurityContext.UserId, stringTypeInfo)).RootElement.Clone()
          : (JsonElement?)null
      };
      var scopeDictTypeInfo = _jsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement?>)) ?? throw new InvalidOperationException("No JsonTypeInfo found for Dictionary<string, JsonElement?>. Ensure the type is registered in WhizbangJsonContext.");
      scopeJson = JsonSerializer.Serialize(scopeDict, scopeDictTypeInfo);
    }

    return new JsonbPersistenceModel {
      DataJson = eventDataJson,
      MetadataJson = metadataJson,
      ScopeJson = scopeJson
    };
  }

  /// <summary>
  /// Non-generic FromJsonb is not supported for AOT compatibility.
  /// Use the generic FromJsonb&lt;TMessage&gt; method instead.
  /// </summary>
  public IMessageEnvelope FromJsonb(JsonbPersistenceModel jsonb) {
    throw new NotSupportedException(
      "Non-generic FromJsonb is not supported for event envelopes in AOT scenarios. " +
      "Use FromJsonb<TMessage> with the concrete message type instead.");
  }

  public MessageEnvelope<TMessage> FromJsonb<TMessage>(JsonbPersistenceModel jsonb) {
    ArgumentNullException.ThrowIfNull(jsonb);

    // Deserialize metadata to extract envelope properties (AOT-compatible)
    var metadataDictTypeInfo = _jsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>)) ?? throw new InvalidOperationException("No JsonTypeInfo found for Dictionary<string, JsonElement>. Ensure the type is registered in WhizbangJsonContext.");
    var metadataDict = JsonSerializer.Deserialize(jsonb.MetadataJson, metadataDictTypeInfo) as Dictionary<string, JsonElement>
                       ?? throw new InvalidOperationException("Failed to deserialize metadata JSON");

    // Extract envelope properties from metadata
    var messageId = metadataDict.TryGetValue("message_id", out var msgIdElem)
      ? Guid.Parse(msgIdElem.GetString()!)
      : throw new InvalidOperationException("MessageId not found in metadata");

    var correlationId = metadataDict.TryGetValue("correlation_id", out var corrIdElem) && !string.IsNullOrEmpty(corrIdElem.GetString())
      ? Guid.Parse(corrIdElem.GetString()!)
      : (Guid?)null;

    var causationId = metadataDict.TryGetValue("causation_id", out var causIdElem) && !string.IsNullOrEmpty(causIdElem.GetString())
      ? Guid.Parse(causIdElem.GetString()!)
      : (Guid?)null;

    // Deserialize hops (AOT-compatible)
    List<MessageHop> hops;
    if (metadataDict.TryGetValue("hops", out var hopsElem)) {
      var hopsTypeInfo = _jsonOptions.GetTypeInfo(typeof(List<MessageHop>)) ?? throw new InvalidOperationException("No JsonTypeInfo found for List<MessageHop>. Ensure the type is registered in WhizbangJsonContext.");
      hops = JsonSerializer.Deserialize(hopsElem.GetRawText(), hopsTypeInfo) as List<MessageHop> ?? [];
    } else {
      hops = [];
    }

    // Deserialize payload (event data) with concrete type - AOT-compatible
    var payloadTypeInfo = _jsonOptions.GetTypeInfo(typeof(TMessage)) ?? throw new InvalidOperationException($"No JsonTypeInfo found for {typeof(TMessage).FullName}. Ensure the type is registered in WhizbangJsonContext.");
    var payload = JsonSerializer.Deserialize(jsonb.DataJson, payloadTypeInfo)
                  ?? throw new InvalidOperationException("Failed to deserialize event data");

    // Reconstruct envelope with strongly-typed payload
    return new MessageEnvelope<TMessage> {
      MessageId = Core.ValueObjects.MessageId.From(messageId),
      Payload = (TMessage)payload,
      Hops = hops
    };
  }
}
