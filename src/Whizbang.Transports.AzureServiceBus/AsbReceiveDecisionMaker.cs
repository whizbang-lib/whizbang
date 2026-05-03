using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
      Func<Type, bool>? isHandledLocally = null) {
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

    var typeInfo = getTypeInfoByName(envelopeTypeName, jsonOptions);
    if (typeInfo == null) {
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
    var payloadType = envelope.Payload?.GetType();
    if (isHandledLocally != null && payloadType != null && !isHandledLocally(payloadType)) {
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
}
