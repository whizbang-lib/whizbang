using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Policy that decides what to do with an inbound ASB message based purely on the data it
/// carries — without touching the broker's ack/dlq APIs. Extracted from the legacy
/// <c>_deserializeReceivedMessageAsync</c> so unit tests can drive behavior without
/// needing real <c>ProcessMessageEventArgs</c> instances.
/// </summary>
/// <remarks>
/// Slice 1 hotfix scope: type-resolution failures (<c>MissingJsonTypeInfo</c>,
/// <c>DeserializationFailed</c>) become <see cref="AsbReceiveAction.AckAndDrop"/> instead of
/// the broker-coupled DLQ. Operators see a structured warning log; messages exit the topic
/// without accumulating in ASB DLQ. Genuine broker-metadata failures
/// (<c>MissingEnvelopeType</c>) still route to <see cref="AsbReceiveAction.DeadLetter"/>
/// because the broker has nothing to redeliver against.
/// </remarks>
[SuppressMessage("Performance", "CA1822:Mark members as static",
  Justification = "Instance method enables DI registration as singleton; future revisions may inject ILogger / counters.")]
internal sealed class AsbReceiveDecisionMaker {
  /// <summary>
  /// Evaluates the inbound message and returns the action + envelope (when applicable).
  /// </summary>
  /// <param name="applicationProperties">The ASB message's ApplicationProperties dictionary.</param>
  /// <param name="bodyJson">The ASB message's body as a JSON string (already
  /// <c>BinaryData.ToString()</c>'d by the caller).</param>
  /// <param name="getTypeInfoByName">Strategy that resolves a CLR type name to a
  /// <see cref="JsonTypeInfo"/> from the local <c>JsonContextRegistry</c>. Returns null when
  /// the type isn't registered in this service.</param>
  /// <param name="jsonOptions">Options to thread through to the JsonTypeInfo resolver.</param>
  /// <param name="isHandledLocally">Optional predicate (slice 2) — given the deserialized
  /// envelope's payload runtime type, returns true if this service has a receptor or
  /// perspective that consumes it. When non-null and returns false, the message is
  /// <see cref="AsbReceiveAction.AckAndDrop"/>'d with reason
  /// <see cref="AsbReceiveReason.NoLocalConsumer"/>. When null, no filtering happens
  /// (legacy / pre-slice-2 callers).</param>
  public AsbReceiveDecision Decide(
      IReadOnlyDictionary<string, object> applicationProperties,
      string bodyJson,
      Func<string, JsonSerializerOptions, JsonTypeInfo?> getTypeInfoByName,
      JsonSerializerOptions jsonOptions,
      Func<Type, bool>? isHandledLocally = null,
      IRawReceptorRegistry? rawReceptorRegistry = null,
      IMessageTypeBinder? typeBinder = null,
      IReadOnlySet<string>? absorbedNamespaces = null) {
    ArgumentNullException.ThrowIfNull(applicationProperties);
    ArgumentNullException.ThrowIfNull(getTypeInfoByName);

    if (!applicationProperties.TryGetValue(AsbMessageHeaderReader.ENVELOPE_TYPE_PROPERTY_KEY, out var envelopeTypeObj)
        || envelopeTypeObj is not string envelopeTypeName
        || string.IsNullOrEmpty(envelopeTypeName)) {
      return new AsbReceiveDecision {
        Action = AsbReceiveAction.DeadLetter,
        Reason = AsbReceiveReason.MISSING_ENVELOPE_TYPE,
        Description = "Message does not contain EnvelopeType metadata",
      };
    }

    // Body-offload claim detection: when the producer's post-serialize hook
    // substituted the wire body, the bytes are MessageEnvelope<BodyClaimEnvelopePayload>
    // while ENVELOPE_TYPE_PROPERTY still names the original type (so transport-level
    // SqlFilter / routing keeps working). The receive-side worker rehydrates from
    // the claim by downloading the original body via the registered IMessageBodyStore.
    var isClaimHeader = applicationProperties.TryGetValue(
      Whizbang.Core.Offloads.BodyOffloadPostSerializeHook.IS_CLAIM_METADATA_KEY,
      out var isClaimObj) ? isClaimObj?.ToString() : null;

    // Claim-detection + registry resolve via the shared BodyClaimWireHelper (same helper +
    // flow point RabbitMQ uses), threading ASB's injected resolver through for testability.
    var typeInfo = Whizbang.Core.Offloads.BodyClaimWireHelper.ResolveDeserializeTypeInfo(
      envelopeTypeName, isClaimHeader, jsonOptions, getTypeInfoByName);

    // Slice 4 (ASB-specific) — on a registry miss for a non-claim message, fall back to the
    // multi-pass type binder. STJ's resolver can produce a JsonTypeInfo for any loadable type,
    // even if no [ModuleInitializer] registered it explicitly.
    if (typeInfo == null
        && !Whizbang.Core.Offloads.BodyClaimWireHelper.IsClaimHeader(isClaimHeader)
        && typeBinder != null) {
      var bound = typeBinder.Bind(envelopeTypeName);
      if (bound != null) {
        typeInfo = jsonOptions.GetTypeInfo(bound);
      }
    }

    if (typeInfo == null) {
      // Slice 5 — try raw receptor before giving up on this message.
      var rawDecision = _tryRawReceptor(envelopeTypeName, bodyJson, rawReceptorRegistry);
      if (rawDecision != null) {
        return rawDecision;
      }
      return new AsbReceiveDecision {
        Action = AsbReceiveAction.AckAndDrop,
        EnvelopeTypeName = envelopeTypeName,
        Reason = AsbReceiveReason.MISSING_JSON_TYPE_INFO,
        Description = $"No JsonTypeInfo registered locally for envelope type '{envelopeTypeName}' — broker ack + drop",
      };
    }

    IMessageEnvelope? envelope = null;
    try {
      envelope = JsonSerializer.Deserialize(bodyJson, typeInfo) as IMessageEnvelope;
    } catch (JsonException) {
      // fall through to AckAndDrop
    }

    if (envelope is null) {
      return new AsbReceiveDecision {
        Action = AsbReceiveAction.AckAndDrop,
        EnvelopeTypeName = envelopeTypeName,
        Reason = AsbReceiveReason.DESERIALIZATION_FAILED,
        Description = $"Could not deserialize envelope as '{envelopeTypeName}' — broker ack + drop",
      };
    }

    // Slice 2 — receptor-registry filter at receive. If this service has no consumer
    // for the payload type, drop the message instead of storing it in the inbox.
    //
    // EXCEPTION — body-offload (claim-check): an offloaded message carries the internal
    // BodyClaimEnvelopePayload on the wire; the ORIGINAL message is rehydrated downstream by
    // TransportConsumerWorker/BodyClaimRehydrator (keyed off the payload type). No service registers
    // a receptor for BodyClaimEnvelopePayload, so running this filter against the claim type would
    // silently AckAndDrop EVERY offloaded message before it can rehydrate. Skip the filter for claims
    // — the transport SqlFilter already matched this service's owned namespace, and the rehydrated
    // original type flows through normal dispatch. (Regression: production "bulk → Approved" no-op.)
    var payloadType = envelope.Payload?.GetType();
    if (isHandledLocally != null && payloadType != null
        && envelope.Payload is not Whizbang.Core.Offloads.BodyClaimEnvelopePayload
        && !isHandledLocally(payloadType)
        && !_isAbsorbedNamespace(payloadType, absorbedNamespaces)) {
      return new AsbReceiveDecision {
        Action = AsbReceiveAction.AckAndDrop,
        Envelope = envelope,
        EnvelopeTypeName = envelopeTypeName,
        Reason = AsbReceiveReason.NO_LOCAL_CONSUMER,
        Description = $"No local receptor or perspective consumes payload type '{payloadType.FullName}' — broker ack + drop",
      };
    }

    return new AsbReceiveDecision {
      Action = AsbReceiveAction.Process,
      Envelope = envelope,
      EnvelopeTypeName = envelopeTypeName,
      Reason = AsbReceiveReason.OK,
      Description = "Envelope deserialized; ready for handler dispatch",
    };
  }

  /// <summary>
  /// True when the payload's namespace is in the caller's absorbed-namespace set. An unconsumed event on an
  /// absorbed namespace is KEPT (persisted for a later rebuild) instead of dropped at the Slice-2 no-consumer
  /// filter — mirrors the core <c>MessageDiscardPolicy</c> absorb bypass for the ASB receive path.
  /// </summary>
  private static bool _isAbsorbedNamespace(Type payloadType, IReadOnlySet<string>? absorbedNamespaces) {
    if (absorbedNamespaces is null || absorbedNamespaces.Count == 0) {
      return false;
    }
    var ns = payloadType.Namespace;
    return ns != null && absorbedNamespaces.Contains(ns);
  }

  /// <summary>
  /// Slice 5 — when the typed JsonTypeInfo lookup misses, look for an
  /// <see cref="IRawReceptor"/> registered for the envelope's inner message type. If found,
  /// extract the envelope's <c>"p"</c> payload as a JsonElement and return an
  /// <see cref="AsbReceiveAction.InvokeRawReceptor"/> decision. Returns null when no raw
  /// receptor matches (caller falls through to the standard AckAndDrop path).
  /// </summary>
  private static AsbReceiveDecision? _tryRawReceptor(
      string envelopeTypeName,
      string bodyJson,
      IRawReceptorRegistry? rawReceptorRegistry) {
    if (rawReceptorRegistry is null) {
      return null;
    }

    var innerTypeName = EnvelopeTypeNameHelper.ExtractInnerTypeName(envelopeTypeName);
    if (string.IsNullOrEmpty(innerTypeName)) {
      return null;
    }

    var receptor = rawReceptorRegistry.FindByTypeName(innerTypeName);
    if (receptor is null) {
      return null;
    }

    JsonElement? payload = null;
    try {
      using var doc = JsonDocument.Parse(bodyJson);
      if (doc.RootElement.TryGetProperty("p", out var p)) {
        payload = p.Clone();
      }
    } catch (JsonException) {
      // Malformed envelope JSON — can't extract payload; fall through to AckAndDrop.
      return null;
    }

    if (payload is null) {
      return null;
    }

    return new AsbReceiveDecision {
      Action = AsbReceiveAction.InvokeRawReceptor,
      EnvelopeTypeName = envelopeTypeName,
      Reason = AsbReceiveReason.RAW_RECEPTOR_MATCH,
      Description = $"Inner type '{innerTypeName}' matched IRawReceptor '{receptor.GetType().FullName}'",
      RawReceptor = receptor,
      RawPayload = payload,
    };
  }
}
