using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Data;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Security;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Adapts IMessageEnvelope to the 3-column JSONB pattern for Event Store persistence.
/// Splits envelope into: event_data (payload), metadata (correlation/causation/hops), scope (tenant/user).
/// </summary>
/// <remarks>
/// Constructor that accepts JsonSerializerOptions with a source-generated context.
/// This is the AOT-compatible way to use this adapter.
/// </remarks>
/// <tests>No tests found</tests>
public class EventEnvelopeJsonbAdapter(JsonSerializerOptions jsonOptions) : IJsonbPersistenceAdapter<IMessageEnvelope> {
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));

  /// <summary>
  /// Converts a message envelope to the 3-column JSONB persistence model (event_data, metadata, scope).
  /// </summary>
  /// <tests>No tests found</tests>
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
    var hopsJson = JsonSerializer.Serialize(source.Hops?.ToList() ?? [], hopsTypeInfo);

    var metadataDict = new Dictionary<string, JsonElement> {
      ["message_id"] = JsonDocument.Parse(messageIdJson).RootElement.Clone(),
      ["correlation_id"] = JsonDocument.Parse(correlationIdJson).RootElement.Clone(),
      ["causation_id"] = JsonDocument.Parse(causationIdJson).RootElement.Clone(),
      ["hops"] = JsonDocument.Parse(hopsJson).RootElement.Clone()
    };

    var metadataDictTypeInfo = _jsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>)) ?? throw new InvalidOperationException("No JsonTypeInfo found for Dictionary<string, JsonElement>. Ensure the type is registered in WhizbangJsonContext.");
    var metadataJson = JsonSerializer.Serialize(metadataDict, metadataDictTypeInfo);

    // 3. Scope: Extract from envelope's current scope (walks hops and merges deltas)
    // Serialized as PerspectiveScope with short keys: {"t":"...","u":"...","c":"...","o":"...","ap":[...],"ex":[...]}
    string? scopeJson = null;
    var currentScope = source.GetCurrentScope();
    if (currentScope?.Scope != null) {
      var perspectiveScopeTypeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveScope))
        ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveScope. Ensure the type is registered in WhizbangJsonContext.");
      scopeJson = JsonSerializer.Serialize(currentScope.Scope, perspectiveScopeTypeInfo);
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
  /// <tests>No tests found</tests>
  public IMessageEnvelope FromJsonb(JsonbPersistenceModel jsonb) {
    throw new NotSupportedException(
      "Non-generic FromJsonb is not supported for event envelopes in AOT scenarios. " +
      "Use FromJsonb<TMessage> with the concrete message type instead.");
  }

  /// <summary>
  /// Reconstructs a strongly-typed message envelope from the 3-column JSONB persistence model.
  /// </summary>
  /// <tests>No tests found</tests>
  public MessageEnvelope<TMessage> FromJsonb<TMessage>(JsonbPersistenceModel jsonb) {
    ArgumentNullException.ThrowIfNull(jsonb);

    // Deserialize metadata to extract envelope properties (AOT-compatible)
    var metadataDictTypeInfo = _jsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>)) ?? throw new InvalidOperationException("No JsonTypeInfo found for Dictionary<string, JsonElement>. Ensure the type is registered in WhizbangJsonContext.");
    var metadataDict = JsonSerializer.Deserialize(jsonb.MetadataJson, metadataDictTypeInfo) as Dictionary<string, JsonElement>
                       ?? throw new InvalidOperationException("Failed to deserialize metadata JSON");

    // Extract envelope properties from metadata (snake_case keys to match ToJsonb)
    var messageId = metadataDict.TryGetValue("message_id", out var msgIdElem)
      ? Guid.Parse(msgIdElem.GetString()!)
      : throw new InvalidOperationException("message_id not found in metadata");

    // Deserialize Hops (AOT-compatible, using snake_case key)
    List<MessageHop> hops;
    if (metadataDict.TryGetValue("hops", out var hopsElem)) {
      var hopsTypeInfo = _jsonOptions.GetTypeInfo(typeof(List<MessageHop>)) ?? throw new InvalidOperationException("No JsonTypeInfo found for List<MessageHop>. Ensure the type is registered in WhizbangJsonContext.");
      hops = JsonSerializer.Deserialize(hopsElem.GetRawText(), hopsTypeInfo) as List<MessageHop> ?? [];
    } else {
      hops = [];
    }

    // Restore ScopeDelta from Scope column if present
    // Supports both new PerspectiveScope short keys {"t":"...","u":"..."} and legacy snake_case {"tenant_id":"...","user_id":"..."}
    if (!string.IsNullOrEmpty(jsonb.ScopeJson) && hops.Count > 0) {
      string? tenantId = null;
      string? userId = null;

      // Try new PerspectiveScope format first (short keys: t, u, c, o, ap, ex)
      var perspectiveScopeTypeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveScope));
      if (perspectiveScopeTypeInfo != null) {
        try {
          var perspectiveScope = JsonSerializer.Deserialize(jsonb.ScopeJson, perspectiveScopeTypeInfo) as PerspectiveScope;
          if (perspectiveScope != null) {
            tenantId = perspectiveScope.TenantId;
            userId = perspectiveScope.UserId;
          }
        } catch (JsonException) {
          // Fall back to legacy format below
        }
      }

      // Fallback: legacy snake_case format {"tenant_id":"...","user_id":"..."}
      if (string.IsNullOrEmpty(tenantId) && string.IsNullOrEmpty(userId)) {
        var scopeDictTypeInfo = _jsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement?>))
                                ?? throw new InvalidOperationException("No JsonTypeInfo found for Dictionary<string, JsonElement?>.");
        var scopeDict = JsonSerializer.Deserialize(jsonb.ScopeJson, scopeDictTypeInfo) as Dictionary<string, JsonElement?>;
        if (scopeDict != null) {
          if (scopeDict.TryGetValue("tenant_id", out var tenantElem) && tenantElem.HasValue && tenantElem.Value.ValueKind != JsonValueKind.Null) {
            tenantId = tenantElem.Value.GetString();
          }
          if (scopeDict.TryGetValue("user_id", out var userElem) && userElem.HasValue && userElem.Value.ValueKind != JsonValueKind.Null) {
            userId = userElem.Value.GetString();
          }
        }
      }

      if (!string.IsNullOrEmpty(tenantId) || !string.IsNullOrEmpty(userId)) {
        // Update first hop with ScopeDelta
        var firstHop = hops[0];
        hops[0] = firstHop with { Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = tenantId, UserId = userId }) };
      }
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
