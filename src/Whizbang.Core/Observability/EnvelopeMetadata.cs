using System.Collections.Generic;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metadata structure for serializing envelope metadata to JSONB.
/// Contains MessageId and Hops - serialized directly by System.Text.Json.
/// Public for AOT source generation, but not intended for external use.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Generated/InfrastructureJsonContextTests.cs:InfrastructureJsonContext_SerializesEnvelopeMetadata_Async</tests>
public sealed class EnvelopeMetadata {
  /// <summary>The unique identifier for the message this metadata belongs to.</summary>
  public required MessageId MessageId { get; init; }
  /// <summary>The ordered list of hops recording the message's journey through the system.</summary>
  public required List<MessageHop> Hops { get; init; }
  /// <summary>
  /// Dispatch context describing how the message was dispatched (mode + source).
  /// Nullable for backward compatibility with v1 events stored before this field existed.
  /// </summary>
  [System.Text.Json.Serialization.JsonPropertyName("dc")]
  public MessageDispatchContext? DispatchContext { get; init; }

  /// <summary>
  /// Per-receptor invocation records captured after each receptor fires. Parallel to
  /// <see cref="Hops"/>: hops describe the message's journey across services; these records
  /// describe which receptors ran against this specific message. Nullable for backward
  /// compatibility with envelopes persisted before this field existed.
  /// </summary>
  /// <remarks>
  /// NOT consulted by security, scope, source-service, or trace-context extraction —
  /// those walk <see cref="Hops"/> only. This list exists solely for the
  /// <see cref="Messaging.IReceptorDedupStore">receptor deduplication</see> guardrail.
  /// </remarks>
  [System.Text.Json.Serialization.JsonPropertyName("rin")]
  public List<ReceptorInvocationRecord>? ReceptorInvocations { get; set; }

  /// <summary>
  /// TTL in seconds for a <see cref="Attributes.Destruction.AfterTtl"/> ephemeral event, stamped at dispatch
  /// (the same seam that derives the ephemeral flag). Rides the event into <c>wh_event_body.metadata</c>,
  /// where the emit chain materialises the absolute expiry as <c>created_at + ttl</c> (anchored to the event's
  /// authoritative DB creation timestamp + DB clock, NOT the C# dispatch moment) and the reaper reads that as
  /// the age-based reap floor. Null for Sourced and WhenConsumed events (no TTL).
  /// </summary>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  [System.Text.Json.Serialization.JsonPropertyName("ett")]
  public int? EphemeralTtlSeconds { get; init; }
}
