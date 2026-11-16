// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.Observability;

/// <summary>How one attempted table rewrite ended, as the <c>outcome</c> tag of the rewrite meters reports it (#2319).</summary>
/// <docs>operations/observability/metrics#table-rewrites</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/TableRewriteMetricsTests.cs</tests>
public enum TableRewriteMetricOutcome {
  /// <summary>The rewrite ran, measured better (or applied a lowered fillfactor), and its request was cleared (<c>rewritten</c>).</summary>
  Rewritten = 0,

  /// <summary>The rewrite ran but the table measures no better; the request stays queued (<c>no_gain</c>).</summary>
  NoGain = 1,

  /// <summary>The rewrite threw, or the coordinator rewrote nothing (a prerequisite unmet, a role that does not own the table) (<c>failed</c>).</summary>
  Failed = 2,
}

/// <summary>One attempted table rewrite, as the rewrite meters record it (#2319).</summary>
/// <param name="TableName">The bare table name.</param>
/// <param name="Strategy">The <see cref="Whizbang.Core.Workers.ITableRewriter.Strategy"/> that ran it.</param>
/// <param name="Reason">Why the table was offered.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Duration">How long it took, timed by the host's <see cref="TimeProvider"/>.</param>
/// <param name="ReclaimedBytes">
/// The table's whole size before minus after (heap, TOAST and indexes), at least 0, for a rewrite that ended
/// <see cref="TableRewriteMetricOutcome.Rewritten"/> and whose size was read both times; otherwise null.
/// </param>
/// <docs>operations/observability/metrics#table-rewrites</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/TableRewriteMetricsTests.cs</tests>
public sealed record TableRewriteAttempt(
  string TableName, string Strategy, TableRewriteReason Reason, TableRewriteMetricOutcome Outcome, TimeSpan Duration,
  long? ReclaimedBytes);
