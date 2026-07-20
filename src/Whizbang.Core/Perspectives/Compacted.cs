using System.Text.Json;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// E3 (Carry-forward / Tier-2) — the authoritative <em>permanent</em> carry-forward event a compaction writes
/// at a state-based stream's head: the designated perspective's model folded into a single origin, à la Marten's
/// <c>Compacted&lt;T&gt;</c>. The folded detail below it is then truncated; the compacted stream replays only
/// back to this event. It is <strong>StateBased but not ephemeral</strong> (<see cref="ICompactedEvent"/>): not
/// replayed (the rebuild/rewind guards refuse it, honouring the E1 no-laundering invariant — a compaction never
/// promotes to a durable Sourced event), yet <strong>never reaped</strong> — permanent by mode, so it needs no
/// "hold it forever" protection; the reaper (keyed on the ephemeral/self-destruct flag) simply never targets it.
/// </summary>
/// <remarks>
/// The model rides as raw JSON (<see cref="Model"/>) with a <see cref="SchemaVersion"/> stamp so a compacted
/// record can be upgraded document-style (there is no event log to rebuild it from). The concrete carrier is a
/// single non-generic event rather than <c>Compacted&lt;T&gt;</c> so it serializes uniformly (the perspective
/// type is a string, the model is JSON).
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events</docs>
[PinnedId("d4e8c1a6-9b03-4f27-8a5e-1c7b2d9e6f04")]
public sealed record Compacted : ICompactedEvent {
  /// <summary>The state-based stream being compacted (its new origin lives here).</summary>
  [StreamId]
  public required Guid StreamId { get; init; }

  /// <summary>The authoritative perspective whose model this carries.</summary>
  public required string PerspectiveName { get; init; }

  /// <summary>The folded authoritative model, as raw JSON.</summary>
  public required JsonElement Model { get; init; }

  /// <summary>The model's serialization/schema version, for document-style per-record upgrades.</summary>
  public int SchemaVersion { get; init; }

  /// <summary>The inclusive per-stream version this compaction folded through — everything at or below it is
  /// truncated, so this event survives as the head origin.</summary>
  public long ThroughVersion { get; init; }
}
