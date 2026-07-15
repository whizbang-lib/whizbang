namespace Whizbang.Core.Attributes;

/// <summary>
/// How (and when) an <see cref="EphemeralAttribute">ephemeral</see> event self-destructs. This is the
/// *destruction* axis — named to match the Pre/PostDestruction hooks, not framed as retention: an
/// ephemeral event is emphatically <em>not</em> retained. Each value lands with its roadmap phase; the
/// analyzer rejects a value whose phase has not shipped, so the enum never lies about what works today.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public enum Destruction {
  /// <summary>
  /// Self-destruct once <em>every</em> perspective that cares has consumed it (consumption-gated
  /// deletion). The novel, safe-by-construction default — physical delete is withheld until each
  /// registered perspective's cursor has checkpointed past the event. (E1.)
  /// </summary>
  WhenConsumed = 0,

  /// <summary>Self-destruct at a logical expiry (<c>expires_at</c>), then a two-phase reap. (E2.)</summary>
  AfterTtl = 1,

  /// <summary>
  /// Fold the detail into an authoritative ephemeral summary (a carry-forward <c>Compacted&lt;T&gt;</c>),
  /// then truncate the detail. (E3.)
  /// </summary>
  OnCompaction = 2,

  /// <summary>
  /// Move the detail to cold storage (preserved, not erased), then drop it from the hot store. (A1.)
  /// </summary>
  Archived = 3
}
