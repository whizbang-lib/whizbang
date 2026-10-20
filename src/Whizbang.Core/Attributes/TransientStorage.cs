// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Attributes;

/// <summary>
/// Where an <see cref="EphemeralAttribute">ephemeral</see> perspective's authoritative read-model
/// <em>state</em> lives — orthogonal to <see cref="Destruction"/>. This chooses only the
/// <em>perspective store</em> strategy; it does NOT change how the event is delivered.
/// <para>
/// <strong>The event is always DB-persisted and routed.</strong> Every instance writes its inbox rows to
/// the database, which assigns each to the stream's owning instance (<c>wh_active_streams</c>, stream
/// affinity); that DB-mediated hand-off IS the delivery mechanism, so an event that skipped the store
/// could never reach the right instance. There is no "local, no-append" ephemeral event for anything that
/// feeds a stream-routed perspective. The event-store side is the consumption-gated ephemeral substrate
/// (offloaded body, reaped once consumed + aged); this axis is purely about the read model built from it.
/// </para>
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public enum TransientStorage {
  /// <summary>
  /// The <strong>default</strong>: the perspective's authoritative read model is a normal, persisted
  /// <c>wh_per_*</c> row that survives restart and rebalance, with <strong>no expiry</strong> — the row IS
  /// the source of truth for a <see cref="Destruction.WhenConsumed"/> ephemeral stream (its events
  /// self-destruct; the row is what remains). Lens-queryable like any perspective. This is the safe,
  /// general choice; pick <see cref="TtlRow"/> only for a read model that should age out.
  /// </summary>
  PersistedRow = 0,

  // 1 was InMemory, removed by #1538: never acted on, and a model held in one instance's memory is lost when its
  // stream moves. A local, in-memory ephemeral event is a separate question (#1549). The value is not reused.

  /// <summary>
  /// A <c>wh_per_*</c> perspective row carrying an <c>expires_at</c> marker — like <see cref="PersistedRow"/>
  /// but the row itself expires and is reaped, for a read model that should age out (chat thread state,
  /// session layout). The expiry <em>duration</em> comes from the TTL machinery
  /// (<see cref="Destruction.AfterTtl"/>, phase E2): the row's expiry is stamped on upsert, lens reads filter
  /// expired rows, and the reaper removes them.
  /// </summary>
  TtlRow = 2
}
