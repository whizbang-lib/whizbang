namespace Whizbang.Core.Notifications;

/// <summary>
/// How many rows the next stamp call should ask for, given what the last one returned.
/// </summary>
/// <remarks>
/// <para>
/// Each call to <c>stamp_pending_commit_sequences</c> costs a scan and a sort of the unstamped set,
/// because the only column that orders it is a system column no index can cover. The cost is therefore
/// paid per call rather than per row, and draining a backlog of N rows in fixed steps of B costs about
/// N/B of those scans. A store adopting commit sequences for the first time has its whole history
/// pending, which is where that arithmetic stops being academic: at a thousand rows a call, four million
/// rows is four thousand scans.
/// </para>
/// <para>
/// So the batch grows while there is clearly more work and returns to the configured size once there is
/// not. A full batch is the signal: the function returns how many it stamped, and a call that filled its
/// limit means rows were left behind. Nothing has to be counted to learn this, which matters — a probe
/// for the backlog size would be one more scan of the thing that is already too expensive to scan.
/// </para>
/// <para>
/// Growth is geometric rather than a jump to the maximum so that a short burst is served by a small
/// batch and only a real backlog reaches the large ones. A batch holds its rows locked and the sequence
/// advanced for the length of its call, so the ceiling is a bound on that, not a target.
/// </para>
/// <para>
/// This does not make the drain proportional to the work: each call still scans the whole pending set.
/// It makes the number of those scans small. The proportional fix needs an ordering key an index can
/// cover, which changes what the stamp considers "order" and is tracked separately.
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
