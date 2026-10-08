// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Notifications;

/// <summary>
/// How many rows the next stamp call should ask for, given what the last one returned.
/// </summary>
/// <remarks>
/// <para>
/// Each call to <c>stamp_pending_commit_sequences</c> used to cost a scan and a sort of the unstamped set,
/// because the column that ordered it was a system column no index can cover, so draining a backlog of N
/// rows in fixed steps of B cost about N/B of those scans (#1060). Since migration 197 the stamp walks an
/// insertion-order index and stops at its batch, so a call costs what it stamps (#1062) and the number of
/// calls no longer multiplies a scan.
/// </para>
/// <para>
/// The batch still grows while there is clearly more work and returns to the configured size once there
/// is not, because a call has a fixed cost of its own (a round trip, the visibility horizon, a transaction
/// under the role fence) that a large backlog should pay as few times as it can. A full batch is the
/// signal: the function returns how many it stamped, and a call that filled its limit means rows were left
/// behind. Nothing has to be counted to learn this.
/// </para>
/// <para>
/// Growth is geometric rather than a jump to the maximum so that a short burst is served by a small
/// batch and only a real backlog reaches the large ones. A batch holds its rows locked, the sequence
/// advanced and, under role assignment, the role row shared for the length of its call, so the ceiling is
/// a bound on that, not a target: a holder's own vote waits for the call to commit.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
/// <tests>tests/Whizbang.Core.Tests/Notifications/CommitOrderStampBatchTests.cs</tests>
public static class CommitOrderStampBatch {
  /// <summary>The factor a batch grows by while calls keep filling.</summary>
  public const int GROWTH = 2;

  /// <summary>
  /// The size for the next call.
  /// </summary>
  /// <param name="current">The size the last call asked for.</param>
  /// <param name="stamped">How many rows that call stamped.</param>
  /// <param name="configured">The steady-state size, which a drained set returns to.</param>
  /// <param name="ceiling">The largest size to grow to.</param>
  /// <returns>The size for the next call, never below <paramref name="configured"/>.</returns>
  public static int Next(int current, int stamped, int configured, int ceiling) {
    // A short batch means the pending set is drained to the fence, so latency matters again rather
    // than throughput. Reset rather than decay: the next burst should be served immediately at the
    // size the operator configured, not somewhere on the way back down to it.
    if (stamped < current) {
      return configured;
    }

    // The ceiling can be configured below the steady-state size; honor the larger so that raising
    // the steady size is never silently capped by a ceiling nobody revisited.
    var top = Math.Max(configured, ceiling);
    if (current >= top) {
      return top;
    }

    var grown = (long)current * GROWTH;
    return grown >= top ? top : (int)grown;
  }
}
