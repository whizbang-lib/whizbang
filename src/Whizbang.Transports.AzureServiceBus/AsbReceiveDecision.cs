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
  /// rejection). Ack the broker — do NOT dead-letter — so the message exits the topic without
  /// accumulating in ASB DLQ. The message is dropped from this consumer; an upstream
  /// republish or a contracts-aligned consumer is responsible for any re-processing.
  /// </summary>
  /// <remarks>
  /// This is the slice 1 hotfix's core behavior change. Today these cases dead-letter at the
  /// broker; that produced unbounded ASB DLQ accumulation on JDX services that received
  /// events from contracts assemblies they didn't reference. The full plan stores opaque
  /// payload bytes in <c>wh_inbox</c> for forensic preservation; the hotfix scope drops them
  /// silently with a warning log + metric, matching the slice-2 receptor-registry-filter
  /// behavior on receive (no local handler → ack + drop).
  /// </remarks>
  AckAndDrop,

  /// <summary>
  /// Genuine broker-routing failure (no <c>EnvelopeType</c> ApplicationProperty). The
  /// message has no metadata to route on, so dead-lettering at the broker is correct —
  /// these aren't expected to ever appear in steady-state production traffic.
  /// </summary>
  DeadLetter
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

  /// <summary>The envelope's CLR type name when known (Process or AckAndDrop after a JsonTypeInfo miss); null otherwise.</summary>
  public string? EnvelopeTypeName { get; init; }

  /// <summary>Short reason code for logs/metrics ("MissingJsonTypeInfo", "DeserializationFailed", "MissingEnvelopeType", "Ok").</summary>
  public required string Reason { get; init; }

  /// <summary>Human-readable description for DLQ payloads or warning logs.</summary>
  public required string Description { get; init; }
}
