using System.Text.Json;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// E3 (Carry-forward / Tier-2) — the authoritative <em>ephemeral</em> carry-forward event a compaction writes
/// at an ephemeral stream's head: the designated perspective's model folded into a single origin, à la Marten's
/// <c>Compacted&lt;T&gt;</c>. The folded detail below it is then truncated; the compacted stream replays only
/// back to this event. It stays <see cref="EphemeralAttribute">ephemeral</see> (the E1 no-laundering invariant —
/// a compaction never promotes to a durable Sourced event), and is protected from the tier-1 reaper (held at
/// <c>infinity</c>) because it is the authority, not disposable detail.
/// </summary>
/// <remarks>
/// The model rides as raw JSON (<see cref="Model"/>) with a <see cref="SchemaVersion"/> stamp so a compacted
/// record can be upgraded document-style (there is no event log to rebuild it from). The concrete carrier is a
/// single non-generic event rather than <c>Compacted&lt;T&gt;</c> so it serializes uniformly (the perspective
/// type is a string, the model is JSON).
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events</docs>
[Ephemeral]
[PinnedId("d4e8c1a6-9b03-4f27-8a5e-1c7b2d9e6f04")]
public sealed record Compacted : IEvent {
  /// <summary>The ephemeral stream being compacted (its new origin lives here).</summary>
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
