using System.Text.Json;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Outcome of evaluating an inbound ASB message before any broker action is taken.
/// Splitting the decision out of the broker-coupled receive method makes the policy
/// testable in isolation — the caller (the receive method) is just glue that maps
/// each action onto <c>CompleteMessageAsync</c> / <c>DeadLetterMessageAsync</c>.
/// </summary>
internal enum AsbReceiveAction {
  /// <summary>Envelope deserialized successfully; hand to the local handler.</summary>
  Process,

  /// <summary>
  /// Envelope cannot be bound to a CLR type in this service (unknown JsonTypeInfo or shape
  /// rejection), OR the deserialized payload has no local consumer (slice 2). Ack the broker
  /// — do NOT dead-letter — so the message exits the topic without accumulating in ASB DLQ
  /// or the inbox table.
  /// </summary>
  /// <remarks>
  /// Slice 1 hotfix triggers this on type-resolution failures. Slice 2 extends it to
  /// payloads that deserialize fine but match no local receptor / perspective Apply target.
  /// </remarks>
  AckAndDrop,

  /// <summary>
  /// Genuine broker-routing failure (no <c>EnvelopeType</c> ApplicationProperty). The
  /// message has no metadata to route on, so dead-lettering at the broker is correct —
  /// these aren't expected to ever appear in steady-state production traffic.
  /// </summary>
  DeadLetter,

  /// <summary>
  /// Slice 5 — the typed CLR envelope can't bind locally, but an opt-in
  /// <see cref="Whizbang.Core.Messaging.IRawReceptor"/> is registered for the inner
  /// message type. The transport invokes the raw receptor with the envelope's
  /// <c>"p"</c> payload as a <see cref="System.Text.Json.JsonElement"/>, then acks the
  /// broker. The decision carries the matched receptor and the extracted payload.
  /// </summary>
  InvokeRawReceptor,
}

/// <summary>
/// Result of <c>AsbReceiveDecisionMaker.Decide</c>. Carries the action plus context
/// the caller needs to log and (when applicable) hand to the downstream handler.
/// </summary>
internal sealed record AsbReceiveDecision {
  /// <summary>The broker action to take.</summary>
  public required AsbReceiveAction Action { get; init; }

  /// <summary>The deserialized envelope when <see cref="Action"/> is Process; null otherwise.</summary>
  public IMessageEnvelope? Envelope { get; init; }

  /// <summary>The envelope's CLR type name when known; null otherwise.</summary>
  public string? EnvelopeTypeName { get; init; }

  /// <summary>Short reason code for logs/metrics. See <see cref="AsbReceiveReason"/> constants.</summary>
  public required string Reason { get; init; }

  /// <summary>Human-readable description for DLQ payloads or warning logs.</summary>
  public required string Description { get; init; }

  /// <summary>
  /// When <see cref="Action"/> is <see cref="AsbReceiveAction.InvokeRawReceptor"/>, the
  /// matched raw receptor that should handle the message; null for other actions.
  /// </summary>
  public IRawReceptor? RawReceptor { get; init; }

  /// <summary>
  /// When <see cref="Action"/> is <see cref="AsbReceiveAction.InvokeRawReceptor"/>, the
  /// envelope's payload as an unparsed <see cref="JsonElement"/> for the receptor; null
  /// otherwise.
  /// </summary>
  public JsonElement? RawPayload { get; init; }
}

/// <summary>
/// Standard reason codes attached to <see cref="AsbReceiveDecision.Reason"/>. String constants
/// rather than an enum so structured logs and metrics can use them as tag values without
/// extra mapping.
/// </summary>
internal static class AsbReceiveReason {
  internal const string OK = "Ok";
  internal const string MISSING_ENVELOPE_TYPE = "MissingEnvelopeType";
  internal const string MISSING_JSON_TYPE_INFO = "MissingJsonTypeInfo";
  internal const string DESERIALIZATION_FAILED = "DeserializationFailed";
  internal const string NO_LOCAL_CONSUMER = "NoLocalConsumer";
  internal const string RAW_RECEPTOR_MATCH = "RawReceptorMatch";
}
