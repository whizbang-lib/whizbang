namespace Whizbang.Core.Attributes;

/// <summary>
/// Where an <see cref="EphemeralAttribute">ephemeral</see> event's authoritative <em>state</em> lives —
/// orthogonal to <see cref="Destruction"/>. The read side is unaffected either way (lenses read
/// <c>wh_per_*</c> rows); this only chooses the write/store strategy for the transient state.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public enum TransientStorage {
  /// <summary>
  /// In-process only (reuses the in-memory upsert strategy) plus realtime push. Does not survive
  /// restart — the right choice for presence / typing / cursor, where losing it is fine. Emitted via
  /// <c>Route.LocalNoPersist</c> (dispatch-only, no event-store append).
  /// </summary>
  InMemory = 0,

  /// <summary>
  /// A normal <c>wh_per_*</c> perspective row carrying an <c>expires_at</c> marker. Survives restart and
  /// is lens-queryable — the right choice for chat thread state or session layout.
  /// </summary>
  TtlRow = 1
}
