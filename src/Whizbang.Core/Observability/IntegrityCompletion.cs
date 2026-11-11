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
  /// <summary>One origin's verified percentage.</summary>
  /// <param name="sealedThrough">The seal: the exclusive end of the highest window audited clean and complete.</param>
  /// <param name="latestCommitSequence">The highest commit sequence the origin announced (inclusive), or null.</param>
  /// <returns>The percentage, 0 to 100; null when the latest is not known.</returns>
  public static double? VerifiedPercent(long sealedThrough, long? latestCommitSequence) =>
    throw new NotImplementedException();

  /// <summary>The verified reading for a set of seals: each known origin, and all of them weighted.</summary>
  /// <param name="seals">The seals the ledger gauges read.</param>
  /// <returns>The reading, reported.</returns>
  public static IntegrityVerifiedReading Verified(IReadOnlyList<OriginSeal> seals) =>
    throw new NotImplementedException();

  /// <summary>Buckets closed as a percentage of buckets opened over a window.</summary>
  /// <param name="opened">Buckets opened in the window.</param>
  /// <param name="closed">Buckets closed in the window.</param>
  /// <returns>The percentage; null when nothing opened.</returns>
  public static double? RepairProgressPercent(long opened, long closed) =>
    throw new NotImplementedException();

  /// <summary>Seconds until the repairable backlog empties at the window's net close rate.</summary>
  /// <param name="openBuckets">The repairable open buckets.</param>
  /// <param name="opened">Buckets opened in the window.</param>
  /// <param name="closed">Buckets closed in the window.</param>
  /// <param name="window">The window's length.</param>
  /// <returns>The seconds; null while the backlog is flat or growing.</returns>
  public static double? BacklogEtaSeconds(long openBuckets, long opened, long closed, TimeSpan window) =>
    throw new NotImplementedException();

  /// <summary>The open buckets repair may still close: every state but unhealable.</summary>
  /// <param name="backlog">The backlog reading.</param>
  /// <returns>The buckets; null when the backlog is not reported.</returns>
  public static long? RepairableOpenBuckets(LedgerBacklogSnapshot backlog) =>
    throw new NotImplementedException();
}
