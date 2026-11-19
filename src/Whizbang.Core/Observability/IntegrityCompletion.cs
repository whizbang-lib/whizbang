// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Observability;

/// <summary>
/// The formulas behind the derived completion gauges (#2261): <c>verified_percent</c>, <c>repair_progress_percent</c>
/// and <c>backlog_eta_seconds</c>. Each is a calculation over base meters, documented with the same formula, so a team
/// that drops the derived gauges from its telemetry feed computes the same numbers from the base meters itself.
/// </summary>
/// <docs>resilience/stream-integrity#derived-completion-meters</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/IntegrityCompletionTests.cs</tests>
public static class IntegrityCompletion {
  private const double HUNDRED = 100.0;

  /// <summary>
  /// One origin's verified percentage: <c>100 * min(max(sealed_through - 1, 0), latest) / latest</c>. The seal is
  /// exclusive, so a seal of 51 verified sequences up to 50; a seal past the latest is complete.
  /// </summary>
  /// <param name="sealedThrough">The seal: the exclusive end of the highest window audited clean and complete.</param>
  /// <param name="latestCommitSequence">The highest commit sequence the origin announced (inclusive), or null.</param>
  /// <returns>The percentage, 0 to 100; null when the latest is not known, and 100 for an origin that committed
  /// nothing.</returns>
  public static double? VerifiedPercent(long sealedThrough, long? latestCommitSequence) {
    if (latestCommitSequence is not { } latest) {
      return null;
    }
    if (latest <= 0) {
      return HUNDRED;
    }
    return HUNDRED * _verifiedThrough(sealedThrough, latest) / latest;
  }

  /// <summary>
  /// The verified reading for a set of seals: each origin whose latest is known, and all of them weighted by what each
  /// committed: <c>100 * sum(min(max(sealed_through - 1, 0), latest)) / sum(latest)</c>.
  /// </summary>
  /// <param name="seals">The seals the ledger gauges read.</param>
  /// <returns>The reading, reported; its roll-up is null when no origin's latest is known.</returns>
  public static IntegrityVerifiedReading Verified(IReadOnlyList<OriginSeal> seals) {
    ArgumentNullException.ThrowIfNull(seals);
    var origins = new List<OriginVerifiedPercent>(seals.Count);
    long verified = 0;
    long committed = 0;
    foreach (var seal in seals) {
      if (VerifiedPercent(seal.SealedThrough, seal.LatestCommitSequence) is not { } percent) {
        continue;
      }
      origins.Add(new OriginVerifiedPercent(seal.OriginServiceId, percent));
      var latest = Math.Max(seal.LatestCommitSequence!.Value, 0);
      verified += _verifiedThrough(seal.SealedThrough, latest);
      committed += latest;
    }
    return new IntegrityVerifiedReading { Reported = true, Origins = origins, AllOrigins = _weighted(origins.Count, verified, committed) };
  }

  /// <summary>Buckets closed as a percentage of buckets opened over a window: <c>100 * closed / opened</c>. Above 100
  /// means the backlog shrank.</summary>
  /// <param name="opened">Buckets opened in the window.</param>
  /// <param name="closed">Buckets closed in the window.</param>
  /// <returns>The percentage; null when nothing opened.</returns>
  public static double? RepairProgressPercent(long opened, long closed) =>
    opened <= 0 ? null : HUNDRED * closed / opened;

  /// <summary>
  /// Seconds until the repairable backlog empties at the window's net close rate:
  /// <c>open / ((closed - opened) / window_seconds)</c>; 0 when nothing is open.
  /// </summary>
  /// <param name="openBuckets">The repairable open buckets.</param>
  /// <param name="opened">Buckets opened in the window.</param>
  /// <param name="closed">Buckets closed in the window.</param>
  /// <param name="window">The window's length.</param>
  /// <returns>The seconds; null while the backlog is flat or growing.</returns>
  public static double? BacklogEtaSeconds(long openBuckets, long opened, long closed, TimeSpan window) {
    if (openBuckets <= 0) {
      return 0.0;
    }
    var net = closed - opened;
    return net <= 0 ? null : openBuckets * window.TotalSeconds / net;
  }

  /// <summary>The open buckets repair may still close: every state of <c>ledger_open_buckets</c> but unhealable, whose
  /// buckets were closed when they were marked.</summary>
  /// <param name="backlog">The backlog reading.</param>
  /// <returns>The buckets; null when the backlog is not reported.</returns>
  public static long? RepairableOpenBuckets(LedgerBacklogSnapshot backlog) {
    ArgumentNullException.ThrowIfNull(backlog);
    if (!backlog.Reported) {
      return null;
    }
    long open = 0;
    foreach (var count in backlog.Buckets) {
      if (count.State != LedgerBacklogStates.UNHEALABLE) {
        open += count.Buckets;
      }
    }
    return open;
  }

  // All origins' verified sequences over all their committed ones; null when no origin is known, 100 when none committed.
  private static double? _weighted(int known, long verified, long committed) {
    if (known == 0) {
      return null;
    }
    return committed == 0 ? HUNDRED : HUNDRED * verified / committed;
  }

  private static long _verifiedThrough(long sealedThrough, long latest) => Math.Min(Math.Max(sealedThrough - 1, 0), latest);
}
