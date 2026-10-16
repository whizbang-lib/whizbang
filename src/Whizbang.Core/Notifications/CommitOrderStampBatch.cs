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
/// index and stops at its batch, so a call costs what it stamps (#1062) and the number of calls no longer
/// multiplies a scan; since 205 the walk reads only rows past the visibility horizon, so rows an open
/// transaction holds back cost a call nothing either (#1217).
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
  /// The most a stamp call should take when no role elector says otherwise: half the default renewal interval.
  /// </summary>
  /// <remarks>
  /// A role holder renews its lease only between calls, and a fenced call holds the role row shared for as long as it
  /// runs, which every other instance's vote waits behind. Calls kept within half the renewal interval keep each gap
  /// between renewals well inside the lease, and keep those waits short (#1327).
  /// </remarks>
  public static readonly TimeSpan DefaultCallBudget = TimeSpan.FromMilliseconds(2_500);

  /// <summary>
  /// The size for the next call.
  /// </summary>
  /// <param name="current">The size the last call asked for.</param>
  /// <param name="stamped">How many rows that call stamped.</param>
  /// <param name="configured">The steady-state size, which a drained set returns to.</param>
  /// <param name="ceiling">The largest size to grow to.</param>
  /// <returns>The size for the next call, never below <paramref name="configured"/>.</returns>
  public static int Next(int current, int stamped, int configured, int ceiling) =>
    Next(current, stamped, configured, ceiling, timeoutCap: null);

  /// <summary>
  /// The size for the next call, with growth held at or below the size a timeout backed off to.
  /// </summary>
  /// <param name="current">The size the last call asked for.</param>
  /// <param name="stamped">How many rows that call stamped.</param>
  /// <param name="configured">The steady-state size, which a drained set returns to.</param>
  /// <param name="ceiling">The largest size to grow to.</param>
  /// <param name="timeoutCap">
  /// The size the last timeout backed off to (<see cref="AfterTimeout"/>), or null when no call has timed out since the
  /// pending set last drained. The caller drops it when a short batch returns the size to <paramref name="configured"/>.
  /// </param>
  /// <returns>The size for the next call.</returns>
  public static int Next(int current, int stamped, int configured, int ceiling, int? timeoutCap) {
    // A short batch means the pending set is drained to the fence, so latency matters again rather
    // than throughput. Reset rather than decay: the next burst should be served immediately at the
    // size the operator configured, not somewhere on the way back down to it.
    if (stamped < current) {
      return configured;
    }

    // The ceiling can be configured below the steady-state size; honor the larger so that raising
    // the steady size is never silently capped by a ceiling nobody revisited.
    var top = Math.Max(configured, ceiling);
    // A size that timed out cannot complete, and doubling back to it costs another full timeout on every climb, so
    // growth stops at the size the backoff found. This holds below the configured size too, when even that timed out.
    if (timeoutCap is int cap && cap < top) {
      top = cap;
    }
    if (current >= top) {
      return top;
    }

    var grown = (long)current * GROWTH;
    return grown >= top ? top : (int)grown;
  }

  /// <summary>
  /// The size for the next call, sized by how long the last one took as well as by what it returned (#1327).
  /// </summary>
  /// <remarks>
  /// A full batch that ran past <paramref name="budget"/> halves, down to one row; one that took more than half of it
  /// holds its size, because doubling would take about twice as long; only one well inside it grows. Row counts alone
  /// cannot bound a call: on a store where a few thousand rows took longer than the role lease, a drain grew its batch
  /// until each call outlasted the lease, and the holder lost the role to its own lapse between calls. A short batch
  /// returns to <paramref name="configured"/> whatever it cost, as in
  /// <see cref="Next(int, int, int, int, int?)"/>. The caller sets <paramref name="timeoutCap"/> to the halved size
  /// when a call ran past the budget, so growth stops there until the backlog drains.
  /// </remarks>
  /// <param name="current">The size the last call asked for.</param>
  /// <param name="stamped">How many rows that call stamped.</param>
  /// <param name="configured">The steady-state size, which a drained set returns to.</param>
  /// <param name="ceiling">The largest size to grow to.</param>
  /// <param name="timeoutCap">The size a timeout or a slow call last backed off to, or null.</param>
  /// <param name="took">How long the last call took.</param>
  /// <param name="budget">The most a call should take.</param>
  /// <returns>The size for the next call.</returns>
  public static int Next(int current, int stamped, int configured, int ceiling, int? timeoutCap, TimeSpan took, TimeSpan budget) {
    if (stamped < current) {
      return configured;
    }
    if (took > budget) {
      return AfterTimeout(current);
    }
    if (took * GROWTH > budget) {
      return current;
    }
    return Next(current, stamped, configured, ceiling, timeoutCap);
  }

  /// <summary>
  /// The size for the next call after one timed out (#1217): half, and never below one.
  /// </summary>
  /// <remarks>
  /// A call that times out is canceled and rolls back everything it did, so the same size can never succeed. On a
  /// deployed environment a stamper whose batch an import had grown repeated a 30 s timeout for ten hours, because
  /// the size was changed only after a call returned. Halving finds a size that completes in a few steps; it may go
  /// below the configured size, because a call that cannot finish stamps nothing at any size. One row is the smallest
  /// call that still makes progress.
  /// </remarks>
  /// <param name="current">The size the call that timed out asked for.</param>
  /// <returns>The size for the next call.</returns>
  public static int AfterTimeout(int current) => Math.Max(1, current / GROWTH);
}
