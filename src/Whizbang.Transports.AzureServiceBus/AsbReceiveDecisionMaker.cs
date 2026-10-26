// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core;
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
/// #1720: every message whose body is a valid envelope is handed on to be stored before the broker is told anything:
/// as its typed envelope when this service can read it, otherwise as received (an envelope of <c>JsonElement</c>, with
/// the type the producer named), so a registry miss, a typed read the body does not fit, a payload no local consumer
/// takes, a raw receptor match and a missing <c>EnvelopeType</c> property are all stored, and any skip is decided after
/// storage. Only a body that is not a readable envelope is refused, and it is dead-lettered at the broker, where the
/// dead-letter drain gives it custody, never acked and dropped. A raw receptor runs once the message is stored.
/// </remarks>
[SuppressMessage("Performance", "CA1822:Mark members as static",
  Justification = "Instance method enables DI registration as singleton; future revisions may inject ILogger / counters.")]
internal class AsbReceiveDecisionMaker {
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
  /// <param name="rawReceptorRegistry">Opt-in raw-receptor registry (slice 5).</param>
  /// <param name="typeBinder">Multi-pass type binder fallback (slice 4).</param>
  /// <param name="poisonDetector">Topology arc phase 8.5 — Core's poison policy. Optional; null
  /// means pre-phase-8.5 behavior (the <c>IMessageDiscardPolicy</c> idiom).</param>
  /// <param name="poisonContext">Transport-neutral facts for <paramref name="poisonDetector"/>,
  /// adapted from the broker message by the caller.</param>
  [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "Each parameter is an independent optional receive-side policy seam; bundling them would obscure which are wired at each call site.")]
  public virtual AsbReceiveDecision Decide(
      IReadOnlyDictionary<string, object> applicationProperties,
      string bodyJson,
      Func<string, JsonSerializerOptions, JsonTypeInfo?> getTypeInfoByName,
      JsonSerializerOptions jsonOptions,
      IRawReceptorRegistry? rawReceptorRegistry = null,
      IMessageTypeBinder? typeBinder = null,
      Whizbang.Core.Routing.IPoisonMessageDetector? poisonDetector = null,
      Whizbang.Core.Routing.PoisonEvaluationContext poisonContext = default) {
    ArgumentNullException.ThrowIfNull(applicationProperties);
    ArgumentNullException.ThrowIfNull(getTypeInfoByName);

    // Topology arc phase 8.5 — poison gate FIRST. It reads only broker metadata (first-enqueue
    // timestamp, durable observation count), so it is the cheapest branch here AND the one that
    // must not be gated behind payload concerns: a message stuck in a session lock-loss storm can
    // be undeserializable, unconsumed, or perfectly well-formed, and it is hostage either way.
    // Attributing it to POISON rather than to a downstream branch is what makes the DLQ reason
    // point at the real defect. Routed through the existing DeadLetter action — the transport
    // already maps that onto DeadLetterMessageAsync — never a parallel settlement path.
    Whizbang.Core.Routing.PoisonVerdict? agedVerdict = null;
    if (poisonDetector is not null) {
      var verdict = poisonDetector.Evaluate(poisonContext);
      if (verdict.ShouldQuarantine) {
        return new AsbReceiveDecision {
          Action = AsbReceiveAction.DeadLetter,
          Reason = AsbReceiveReason.POISON_QUARANTINE,
          Description = verdict.Detail ?? verdict.Reason.ToString(),
          PoisonVerdict = verdict,
        };
      }
      // Issue #1663: an aged message is ACCEPTED. It takes whatever action its payload earns, and the
      // decision carries the verdict so the transport can report the backlog.
      if (verdict.Reason == Whizbang.Core.Routing.PoisonQuarantineReason.AgedOnReceive) {
        agedVerdict = verdict;
      }
    }

    var decision = _decidePayload(
      applicationProperties, bodyJson, getTypeInfoByName, jsonOptions, rawReceptorRegistry, typeBinder);
    return agedVerdict is null ? decision : decision with { PoisonVerdict = agedVerdict };
  }

  /// <summary>
  /// Everything <see cref="Decide"/> does after the poison gate: envelope-type resolution, the typed read, and the read
  /// as received that keeps every valid envelope (#1720).
  /// </summary>
  private static AsbReceiveDecision _decidePayload(
      IReadOnlyDictionary<string, object> applicationProperties,
      string bodyJson,
      Func<string, JsonSerializerOptions, JsonTypeInfo?> getTypeInfoByName,
      JsonSerializerOptions jsonOptions,
      IRawReceptorRegistry? rawReceptorRegistry,
      IMessageTypeBinder? typeBinder) {
    if (!applicationProperties.TryGetValue(AsbMessageHeaderReader.ENVELOPE_TYPE_PROPERTY_KEY, out var envelopeTypeObj)
        || envelopeTypeObj is not string envelopeTypeName
        || string.IsNullOrEmpty(envelopeTypeName)) {
      // The consumer cannot store a row without the type, so it gives the message dead-letter custody with its body.
      return _asReceivedOrDeadLetter(bodyJson, jsonOptions, envelopeTypeName: null,
        AsbReceiveReason.MISSING_ENVELOPE_TYPE, "Message does not contain EnvelopeType metadata", rawReceptor: null);
    }

    var typeInfo = _resolveTypeInfo(applicationProperties, envelopeTypeName, getTypeInfoByName, jsonOptions, typeBinder);

    if (typeInfo == null) {
      // No context here describes the type: stored as received; a raw receptor for it runs once it is stored.
      return _asReceivedOrDeadLetter(bodyJson, jsonOptions, envelopeTypeName,
        AsbReceiveReason.MISSING_JSON_TYPE_INFO, $"No JsonTypeInfo registered locally for envelope type '{envelopeTypeName}'",
        _findRawReceptor(envelopeTypeName, rawReceptorRegistry));
    }

    var envelope = _tryReadTyped(bodyJson, typeInfo);
    if (envelope is null) {
      return _asReceivedOrDeadLetter(bodyJson, jsonOptions, envelopeTypeName,
        AsbReceiveReason.DESERIALIZATION_FAILED, $"Could not deserialize envelope as '{envelopeTypeName}'", rawReceptor: null);
    }
    MessageParseDiagnostics.Record(envelope.MessageId.Value, MessageParseSource.TransportReceive, MessageParseKind.Envelope);

    // #1720: no local-consumer filter here. A payload no receptor or perspective on this service takes is stored, and
    // the consumer settles it unprocessed with the reason, so the skip is durable and inspectable.
    return new AsbReceiveDecision {
      Action = AsbReceiveAction.Process,
      Envelope = envelope,
      EnvelopeTypeName = envelopeTypeName,
      Reason = AsbReceiveReason.OK,
      Description = "Envelope deserialized; ready for handler dispatch",
    };
  }

  /// <summary>
  /// The metadata to read the body with: the registry's for the named envelope type (or the claim envelope's, for a
  /// body-offload claim), falling back to the multi-pass type binder on a registry miss for a non-claim message.
  /// <see langword="null"/> when nothing here describes the type.
  /// </summary>
  private static JsonTypeInfo? _resolveTypeInfo(
      IReadOnlyDictionary<string, object> applicationProperties,
      string envelopeTypeName,
      Func<string, JsonSerializerOptions, JsonTypeInfo?> getTypeInfoByName,
      JsonSerializerOptions jsonOptions,
      IMessageTypeBinder? typeBinder) {
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

    // Slice 4 (ASB-specific): on a registry miss for a non-claim message, fall back to the
    // multi-pass type binder, which finds the type under a renamed assembly or a different version.
    // Binding never widens what can be read: only a type some registered JSON context describes yields
    // a JsonTypeInfo. A binder-bound type is LOADABLE but not necessarily SERVABLE: when no registered
    // context holds metadata for it, GetTypeInfo throws, and an unguarded throw would escape the decision
    // entirely and the broker would redeliver the message forever, so it reads as a miss instead.
    if (typeInfo == null
        && !Whizbang.Core.Offloads.BodyClaimWireHelper.IsClaimHeader(isClaimHeader)
        && typeBinder != null) {
      var bound = typeBinder.Bind(envelopeTypeName);
      if (bound != null) {
        try {
          typeInfo = jsonOptions.GetTypeInfo(bound);
        } catch (NotSupportedException) {
          typeInfo = null;
        } catch (InvalidOperationException) {
          typeInfo = null;
        }
      }
    }

    return typeInfo;
  }

  /// <summary>
  /// The body read as the typed envelope, or <see langword="null"/> when the typed read rejects it: not the type's JSON,
  /// or a message id that is not a UUIDv7 (an <see cref="ArgumentException"/> as it is read). The caller then reads it
  /// as received (#1720).
  /// </summary>
  private static IMessageEnvelope? _tryReadTyped(string bodyJson, JsonTypeInfo typeInfo) {
    try {
      return JsonSerializer.Deserialize(bodyJson, typeInfo) as IMessageEnvelope;
    } catch (Exception ex) when (ex is JsonException or ArgumentException) {
      return null;
    }
  }

  /// <summary>
  /// The decision for a message this service cannot read as its typed envelope (#1720): handed on as received when the
  /// body is a valid envelope, so it is stored first; otherwise dead-lettered at the broker, the one refusal, which the
  /// dead-letter drain turns into custody.
  /// </summary>
  private static AsbReceiveDecision _asReceivedOrDeadLetter(
      string bodyJson, JsonSerializerOptions jsonOptions, string? envelopeTypeName, string reason, string description,
      IRawReceptor? rawReceptor) {
    var received = Whizbang.Core.Transports.ReceivedEnvelopeReader.TryRead(bodyJson, jsonOptions);
    if (received is null) {
      return new AsbReceiveDecision {
        Action = AsbReceiveAction.DeadLetter,
        EnvelopeTypeName = envelopeTypeName,
        Reason = reason,
        Description = description + "; the body is not a readable envelope, so it cannot be stored",
      };
    }
    MessageParseDiagnostics.Record(received.MessageId.Value, MessageParseSource.TransportReceive, MessageParseKind.Envelope);
    return new AsbReceiveDecision {
      Action = AsbReceiveAction.Process,
      Envelope = received,
      EnvelopeTypeName = envelopeTypeName,
      Reason = rawReceptor is null ? reason : AsbReceiveReason.RAW_RECEPTOR_MATCH,
      Description = description + "; stored as received",
      RawReceptor = rawReceptor,
      RawPayload = rawReceptor is null ? null : received.Payload,
    };
  }

  /// <summary>Slice 5: the opt-in raw receptor registered for the envelope's inner message type, if any.</summary>
  private static IRawReceptor? _findRawReceptor(string envelopeTypeName, IRawReceptorRegistry? rawReceptorRegistry) {
    if (rawReceptorRegistry is null) {
      return null;
    }
    var innerTypeName = EnvelopeTypeNameHelper.ExtractInnerTypeName(envelopeTypeName);
    return string.IsNullOrEmpty(innerTypeName) ? null : rawReceptorRegistry.FindByTypeName(innerTypeName);
  }
}
