// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  /// <summary>
  /// The body is a valid envelope (typed, or as received when this service cannot read its type): hand it to the
  /// handler, which stores it before the broker is told anything (#1720).
  /// </summary>
  Process,

  /// <summary>
  /// The body is not a readable envelope (#1720), or the poison detector quarantined the message: dead-letter it at the
  /// broker, where the dead-letter drain gives it custody. Never acked and dropped.
  /// </summary>
  DeadLetter,
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
  /// Slice 5: the opt-in raw receptor registered for a message this service cannot read as its typed envelope. The
  /// transport invokes it once the handler has stored the message (#1720); null otherwise.
  /// </summary>
  public IRawReceptor? RawReceptor { get; init; }

  /// <summary>
  /// The envelope's payload as an unparsed <see cref="JsonElement"/> for <see cref="RawReceptor"/>; null when there is
  /// no raw receptor.
  /// </summary>
  public JsonElement? RawPayload { get; init; }

  /// <summary>
  /// Topology arc phase 8.5 — set when the <c>DeadLetter</c> action was chosen by the Core poison
  /// detector rather than by a broker-metadata failure. The transport carries it to
  /// <see cref="Whizbang.Core.Routing.IPoisonMessageDetector.RecordQuarantine"/> so the quarantine
  /// telemetry names the layer that fired. Also set, on whatever action the message otherwise
  /// gets, when the detector accepted an aged message
  /// (<see cref="Whizbang.Core.Routing.PoisonQuarantineReason.AgedOnReceive"/>, issue #1663), so the
  /// transport can report it with
  /// <see cref="Whizbang.Core.Routing.IPoisonMessageDetector.RecordAgedOnReceive"/>. Null otherwise.
  /// </summary>
  public Whizbang.Core.Routing.PoisonVerdict? PoisonVerdict { get; init; }
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
  internal const string RAW_RECEPTOR_MATCH = "RawReceptorMatch";

  /// <summary>
  /// Topology arc phase 8.5 — the Core poison detector quarantined the message (age, or durable
  /// redelivery observations). Distinct from <see cref="MISSING_ENVELOPE_TYPE"/> so the DLQ reason
  /// names the actual defect: this is a message the delivery-count valve could never have caught.
  /// </summary>
  internal const string POISON_QUARANTINE = "PoisonQuarantine";
}
