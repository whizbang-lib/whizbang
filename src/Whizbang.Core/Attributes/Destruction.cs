// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Attributes;

/// <summary>
/// How (and when) an <see cref="EphemeralAttribute">ephemeral</see> event self-destructs. This is the
/// *destruction* axis — named to match the Pre/PostDestruction hooks, not framed as retention: an
/// ephemeral event is emphatically <em>not</em> retained. <see cref="WhenConsumed"/> and <see cref="AfterTtl"/>
/// are acted on at run time; <see cref="OnCompaction"/> and <see cref="Archived"/> are planned and do nothing
/// by themselves yet (#1538).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public enum Destruction {
  /// <summary>
  /// Self-destruct once <em>every</em> perspective that cares has consumed it (consumption-gated
  /// deletion). The novel, safe-by-construction default — physical delete is withheld until each
  /// registered perspective's cursor has checkpointed past the event. (E1.)
  /// </summary>
  WhenConsumed = 0,

  /// <summary>
  /// Self-destruct at a logical expiry (<c>expires_at</c>), then a two-phase reap, once the event is older than
  /// <see cref="EphemeralAttribute.TtlSeconds"/>. Without <c>TtlSeconds</c> there is no expiry to wait for, so
  /// the event is reaped as soon as it is consumed, exactly as <see cref="WhenConsumed"/>. (E2.)
  /// </summary>
  AfterTtl = 1,

  /// <summary>
  /// Fold the detail into an authoritative ephemeral summary (a carry-forward <c>Compacted&lt;T&gt;</c>),
  /// then truncate the detail. (E3.) <strong>Planned</strong>, with the storage work on archive policies and
  /// closing the books: nothing acts on it yet, so the event is reaped once consumed, as <see cref="WhenConsumed"/>.
  /// </summary>
  OnCompaction = 2,

  /// <summary>
  /// Move the detail to cold storage (preserved, not erased), then drop it from the hot store. (A1.)
  /// <strong>Planned</strong>, with the storage work on archive policies: nothing acts on it yet, so the event is
  /// reaped once consumed, as <see cref="WhenConsumed"/>.
  /// </summary>
  Archived = 3
}
