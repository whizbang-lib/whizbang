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
    streamDigests
      .GroupBy(d => (d.TenantScope, d.EventType))
      .Select(g => new StreamDigest {
        TenantScope = g.Key.TenantScope,
        EventType = g.Key.EventType,
        StreamId = Guid.Empty,
        DigestLo = g.Aggregate(0L, (acc, d) => acc ^ d.DigestLo),
        DigestHi = g.Aggregate(0L, (acc, d) => acc ^ d.DigestHi),
        EventCount = g.Sum(d => d.EventCount),
      })
      .OrderBy(d => d.TenantScope, StringComparer.Ordinal).ThenBy(d => d.EventType, StringComparer.Ordinal)
      .ToList();

  /// <summary>True when either side's bucket changed inside the settle window — the table-driven
  /// equivalent of the recompute's created-at settle filter: an in-flight delivery must never
  /// read as divergence. Null update times (recomputed rows) never skip.</summary>
  public static bool IsInsideSettle(DateTimeOffset? originUpdatedAt, DateTimeOffset? localUpdatedAt, TimeSpan settle) {
    var floor = DateTimeOffset.UtcNow - settle;
    return originUpdatedAt > floor || localUpdatedAt > floor;
  }
}
