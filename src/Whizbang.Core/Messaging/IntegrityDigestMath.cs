// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Stream-integrity A1c: shared digest arithmetic for the hierarchical exchange. Lives in Core so
/// the coordinator's default type-level recompute and the driver-side receptors share ONE roll-up
/// definition — two implementations of "XOR the lanes" would drift into false divergence.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTests.cs</tests>
public static class IntegrityDigestMath {
  /// <summary>Rolls stream-level digests up to per-(tenant, type) rows — XOR the lanes, sum the
  /// counts. Valid because stream buckets partition the type's events. Recomputed inputs carry no
  /// update times, so the roll-ups don't either.</summary>
  public static IReadOnlyList<StreamDigest> RollUpToTypes(IReadOnlyList<StreamDigest> streamDigests) =>
    [.. streamDigests
      .GroupBy(d => (d.TenantScope, d.EventType))
      .Select(g => new StreamDigest {
        TenantScope = g.Key.TenantScope,
        EventType = g.Key.EventType,
        StreamId = Guid.Empty,
        DigestLo = g.Aggregate(0L, (acc, d) => acc ^ d.DigestLo),
        DigestHi = g.Aggregate(0L, (acc, d) => acc ^ d.DigestHi),
        EventCount = g.Sum(d => d.EventCount),
      })
      .OrderBy(d => d.TenantScope, StringComparer.Ordinal).ThenBy(d => d.EventType, StringComparer.Ordinal)];

  /// <summary>True when either side's bucket changed inside the settle window — the table-driven
  /// equivalent of the recompute's created-at settle filter: an in-flight delivery must never
  /// read as divergence. Null update times (recomputed rows) never skip.</summary>
  public static bool IsInsideSettle(DateTimeOffset? originUpdatedAt, DateTimeOffset? localUpdatedAt, TimeSpan settle) {
    var floor = DateTimeOffset.UtcNow - settle;
    return originUpdatedAt > floor || localUpdatedAt > floor;
  }

  /// <summary>
  /// #2102: places the events a consumer holds from an origin without knowing the origin's commit sequence for them
  /// (re-attributed history: the origin is recorded, the sequence is not). A fold over an origin-sequence window leaves
  /// them out, so the window's bucket read short and was recorded as a deficit for events the consumer holds.
  /// <list type="bullet">
  /// <item><see cref="HeldWindowVerdict.Matches"/>: the placed fold equals the origin's, or the placed fold XOR the
  /// sequence-less fold does (every event of the window is held; the sequence-less events are the window's rest).</item>
  /// <item><see cref="HeldWindowVerdict.Deficit"/>: the origin counts more events than the consumer holds placed and
  /// sequence-less together, so at least that many are missing.</item>
  /// <item><see cref="HeldWindowVerdict.Differs"/>: nothing is held without a sequence and the folds differ without a
  /// shortfall: the identity-mismatch and local-surplus taxonomy applies as before.</item>
  /// <item><see cref="HeldWindowVerdict.Undecided"/>: sequence-less events are held and the counts fit, but the folds
  /// differ. The window cannot say whether a sequence-less event is the missing one or belongs to another window, so
  /// it is not a deficit; the full-history comparison, whose folds include every held event, decides the stream.</item>
  /// </list>
  /// <see cref="HeldWindowPlacement.Held"/> is the fold the consumer reports: the placed fold, combined with the
  /// sequence-less one when there is one, so the digest the ledger records keeps every held event.
  /// </summary>
  /// <param name="origin">The origin's bucket for the window.</param>
  /// <param name="placed">The consumer's fold of the window by origin sequence, or null when it holds none.</param>
  /// <param name="sequenceless">The consumer's fold of the same bucket's events held without an origin sequence, or
  /// null when it holds none.</param>
  /// <returns>The verdict and the held fold.</returns>
  /// <docs>resilience/stream-integrity#events-held-without-an-origin-sequence</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/HeldWithoutOriginSequenceTests.cs</tests>
  public static HeldWindowPlacement PlaceHeld(StreamDigest origin, StreamDigest? placed, StreamDigest? sequenceless) {
    ArgumentNullException.ThrowIfNull(origin);
    if (_sameFold(origin, placed)) {
      return new(HeldWindowVerdict.Matches, placed);
    }
    var placedCount = placed?.EventCount ?? 0;
    if (sequenceless is not { EventCount: > 0 }) {
      return new(placedCount < origin.EventCount ? HeldWindowVerdict.Deficit : HeldWindowVerdict.Differs, placed);
    }
    var held = new StreamDigest {
      TenantScope = origin.TenantScope,
      EventType = origin.EventType,
      StreamId = origin.StreamId,
      DigestLo = (placed?.DigestLo ?? 0) ^ sequenceless.DigestLo,
      DigestHi = (placed?.DigestHi ?? 0) ^ sequenceless.DigestHi,
      EventCount = placedCount + sequenceless.EventCount,
      UpdatedAt = placed?.UpdatedAt,
      IdLo = _xorLane(placed, placed?.IdLo, sequenceless.IdLo),
      IdHi = _xorLane(placed, placed?.IdHi, sequenceless.IdHi),
    };
    if (_sameFold(origin, held)) {
      return new(HeldWindowVerdict.Matches, held);
    }
    return new(held.EventCount < origin.EventCount ? HeldWindowVerdict.Deficit : HeldWindowVerdict.Undecided, held);
  }

  /// <summary>
  /// #2103: the one event a bucket is missing, when it can be named. Id lanes are the XOR of each event id's two 64-bit
  /// halves, so when the origin's bucket holds exactly one event more than the consumer's, origin XOR local IS that
  /// event's id. Null when the origin holds any other number more, when either side carries no id lanes (an older peer,
  /// or a stored bucket not yet backfilled), or when the XOR is the empty id; the stream then syncs whole.
  /// </summary>
  /// <param name="origin">The origin's bucket.</param>
  /// <param name="held">The consumer's bucket (placed and held events, see <see cref="PlaceHeld"/>), or null when it
  /// holds none.</param>
  /// <returns>The missing event's id, or null.</returns>
  /// <docs>resilience/stream-integrity#one-missing-event-is-named</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/SingleMissingEventTests.cs</tests>
  public static Guid? SingleMissingEventId(StreamDigest origin, StreamDigest? held) {
    ArgumentNullException.ThrowIfNull(origin);
    if (origin.IdLo is not long originLo || origin.IdHi is not long originHi
        || origin.EventCount - (held?.EventCount ?? 0) != 1) {
      return null;
    }
    long heldLo = 0, heldHi = 0;
    if (held is not null) {
      if (held.IdLo is not long lo || held.IdHi is not long hi) {
        return null;
      }
      (heldLo, heldHi) = (lo, hi);
    }
    Span<byte> bytes = stackalloc byte[16];
    System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes[..8], originLo ^ heldLo);
    System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes[8..], originHi ^ heldHi);
    var id = new Guid(bytes, bigEndian: true);
    return id == Guid.Empty ? null : id;
  }

  /// <summary>XOR of two id lanes, null when either is unknown; a side that holds no events folds as zero.</summary>
  private static long? _xorLane(StreamDigest? placed, long? placedLane, long? sequencelessLane) =>
    (placed is null ? 0L : placedLane) ^ sequencelessLane;

  private static bool _sameFold(StreamDigest origin, StreamDigest? local) =>
    local is not null && local.DigestLo == origin.DigestLo && local.DigestHi == origin.DigestHi;
}

/// <summary>#2102: how a windowed bucket compares once the events held without an origin sequence are placed.</summary>
/// <docs>resilience/stream-integrity#events-held-without-an-origin-sequence</docs>
public enum HeldWindowVerdict {
  /// <summary>Every event of the origin's window is held.</summary>
  Matches = 0,

  /// <summary>The origin counts more events than are held, placed or not: a deficit, repairable.</summary>
  Deficit = 1,

  /// <summary>Nothing is held without a sequence and the folds differ without a shortfall: identity mismatch or local
  /// surplus, as before.</summary>
  Differs = 2,

  /// <summary>Events are held without a sequence, the counts fit and the folds differ: the window cannot decide, so it
  /// is no deficit; the full-history comparison decides the stream.</summary>
  Undecided = 3,
}

/// <summary>#2102: the verdict of <see cref="IntegrityDigestMath.PlaceHeld"/> and the fold the consumer reports.</summary>
/// <param name="Verdict">How the bucket compares.</param>
/// <param name="Held">The placed fold, combined with the sequence-less fold when one is held; null when nothing is.</param>
/// <docs>resilience/stream-integrity#events-held-without-an-origin-sequence</docs>
public readonly record struct HeldWindowPlacement(HeldWindowVerdict Verdict, StreamDigest? Held);
