using System;
using System.Collections.Generic;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Routing metadata extracted from a transport-delivered message envelope WITHOUT deserializing
/// the typed payload. Carries everything the inbox layer needs to durably store the message and
/// route it later, but stops short of binding the payload to a CLR type.
/// </summary>
/// <remarks>
/// <para>
/// The transport's job is to transfer bytes from one service's outbox to another's inbox. Today
/// the ASB consumer deserializes the typed envelope before storage — any throw on that path
/// dead-letters at the broker, leaving operators no DB-side visibility. Slice 1 of the
/// resilient-transport plan inverts that contract: read headers + opaque payload bytes, store,
/// ack the broker. Deserialization moves to dispatch time where failures land in <c>wh_inbox</c>
/// with a structured <c>failure_reason</c>.
/// </para>
/// <para>
/// <see cref="EnvelopeTypeName"/> and <see cref="MessageId"/> are the only required fields —
/// the broker can't route or dedupe without them. Other fields are best-effort; they may be
/// populated from ASB ApplicationProperties (cheap) or from a shallow JSON parse of the
/// envelope body (slightly more expensive but still no typed deserialize). Consumers MUST NOT
/// assume the payload bytes have ever been parsed; they're delivered byte-for-byte from the
/// transport.
/// </para>
/// </remarks>
/// <docs>fundamentals/transport/message-headers</docs>
public sealed record MessageHeaders {
  /// <summary>
  /// Globally-unique message identifier. Required — used for inbox dedup and lease tracking.
  /// </summary>
  public required MessageId MessageId { get; init; }

  /// <summary>
  /// Full CLR type name of the envelope as set by the publisher (e.g.,
  /// <c>Whizbang.Core.Observability.MessageEnvelope`1[[MyEvent, MyContracts]]</c>). Required —
  /// the dispatcher uses this to bind the payload at receive time. The type may not resolve in
  /// the consuming process; that's the binder cascade's problem (slice 4), not a transport
  /// concern.
  /// </summary>
  public required string EnvelopeTypeName { get; init; }

  /// <summary>
  /// Full CLR type name of the inner payload (the message inside the envelope). Optional in
  /// theory — the envelope JSON also carries this — but typically lifted to a header by the
  /// publisher so receivers can filter without parsing.
  /// </summary>
  public string? MessageTypeName { get; init; }

  /// <summary>
  /// Stream identity for ordered processing. Optional; absent for non-stream-bound messages.
  /// </summary>
  public Guid? StreamId { get; init; }

  /// <summary>
  /// Correlation identifier for distributed tracing. Optional.
  /// </summary>
  public string? CorrelationId { get; init; }

  /// <summary>
  /// Causation identifier — the message that triggered this one. Optional.
  /// </summary>
  public string? CausationId { get; init; }

  /// <summary>
  /// Raw envelope payload as delivered by the transport — opaque at this layer, deserialized
  /// at handler-invoke time. JSON today; if a binary transport ever lands, add a sibling field
  /// rather than re-shaping this one.
  /// </summary>
  public required string PayloadJson { get; init; }

  /// <summary>
  /// Custom metadata propagated from the publisher's <c>TransportDestination.Metadata</c>.
  /// Typically AMQP-primitive values (string, int, bool, byte[]). Empty when none.
  /// </summary>
  public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
    new Dictionary<string, object?>();
}
