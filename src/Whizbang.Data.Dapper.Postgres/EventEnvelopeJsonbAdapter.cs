// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
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
    var payloadTypeInfo = _jsonOptions.GetTypeInfo(payloadType);
    var eventDataJson = JsonSerializer.Serialize(payload, payloadTypeInfo);

    // 2. Metadata: Correlation, Causation, Hops, Message ID (AOT-compatible with JsonElement)
    var correlationId = source.GetCorrelationId();
    var causationId = source.GetCausationId();

    // Serialize individual values using JsonTypeInfo
    var stringTypeInfo = _jsonOptions.GetTypeInfo(typeof(string));
    var hopsTypeInfo = _jsonOptions.GetTypeInfo(typeof(List<MessageHop>));

    var messageIdJson = JsonSerializer.Serialize(source.MessageId.Value.ToString(), stringTypeInfo);
    var correlationIdJson = JsonSerializer.Serialize(correlationId?.Value.ToString() ?? string.Empty, stringTypeInfo);
    var causationIdJson = JsonSerializer.Serialize(causationId?.Value.ToString() ?? string.Empty, stringTypeInfo);
    var hopsJson = JsonSerializer.Serialize(source.Hops?.ToList() ?? [], hopsTypeInfo);

    var metadataDict = new Dictionary<string, JsonElement> {
      ["message_id"] = JsonElement.Parse(messageIdJson),
      ["correlation_id"] = JsonElement.Parse(correlationIdJson),
      ["causation_id"] = JsonElement.Parse(causationIdJson),
      ["hops"] = JsonElement.Parse(hopsJson)
    };
    // Priority step 1: the number lives in the metadata column, omitted when undeclared (the wire's convention).
    if (Whizbang.Core.Priority.WorkPriority.IsDeclared(source.Priority)) {
      metadataDict["pri"] = JsonElement.Parse(source.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    var metadataDictTypeInfo = _jsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>));
    var metadataJson = JsonSerializer.Serialize(metadataDict, metadataDictTypeInfo);

    // 3. Scope: Extract from envelope's current scope (walks hops and merges deltas)
    // Serialized as PerspectiveScope with short keys: {"t":"...","u":"...","c":"...","o":"...","ap":[...],"ex":[...]}
    string? scopeJson = null;
    var currentScope = source.GetCurrentScope();
    if (currentScope?.Scope != null) {
      var perspectiveScopeTypeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveScope));
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
    var metadataDictTypeInfo = _jsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>));
    var metadataDict = JsonSerializer.Deserialize(jsonb.MetadataJson, metadataDictTypeInfo) as Dictionary<string, JsonElement>
                       ?? throw new InvalidOperationException("Failed to deserialize metadata JSON");

    // Extract envelope properties from metadata (snake_case keys to match ToJsonb)
    var messageId = metadataDict.TryGetValue("message_id", out var msgIdElem)
      ? Guid.Parse(msgIdElem.GetString()!)
      : throw new InvalidOperationException("message_id not found in metadata");

    // Deserialize Hops (AOT-compatible, using snake_case key)
    List<MessageHop> hops;
    if (metadataDict.TryGetValue("hops", out var hopsElem)) {
      var hopsTypeInfo = _jsonOptions.GetTypeInfo(typeof(List<MessageHop>));
      hops = JsonSerializer.Deserialize(hopsElem.GetRawText(), hopsTypeInfo) as List<MessageHop> ?? [];
    } else {
      hops = [];
    }

    // Restore the whole stored scope from the Scope column when the row recorded no scoped hop (#1855)
    StoredScope.RestoreInto(hops, StoredScope.Read(jsonb.ScopeJson, _jsonOptions));

    // Deserialize payload (event data) with concrete type - AOT-compatible
    var payloadTypeInfo = _jsonOptions.GetTypeInfo(typeof(TMessage));
    var payload = JsonSerializer.Deserialize(jsonb.DataJson, payloadTypeInfo)
                  ?? throw new InvalidOperationException("Failed to deserialize event data");

    // Reconstruct envelope with strongly-typed payload
    return new MessageEnvelope<TMessage> {
      MessageId = Core.ValueObjects.MessageId.From(messageId),
      Payload = (TMessage)payload,
      Hops = hops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
      Priority = metadataDict.TryGetValue("pri", out var pri) && pri.ValueKind == JsonValueKind.Number
        ? pri.GetInt32()
        : Whizbang.Core.Priority.WorkPriority.UNDECLARED,
    };
  }
}
