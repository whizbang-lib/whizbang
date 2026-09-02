namespace Whizbang.Core.Workers;

/// <summary>
/// Distinguishes a dead-letter backlog that recovery is draining from one that recovery is
/// creating.
/// </summary>
/// <remarks>
/// <para>
/// Recovery re-drives a dead-lettered message by republishing it. If that message fails again it is
/// dead-lettered again, and the framework records the failure as a NEW row with a NEW source id
/// rather than updating the row it came from. The per-row exhaustion check
/// (<c>RecoveryAttempts &gt;= MaxRecoveryAttempts</c>) is therefore structurally unable to see the
/// cycle: every pass presents a row on its first attempt, so every pass is allowed, forever.
/// </para>
/// <para>
/// Nothing in the row identifies it as the descendant of an earlier one, so the loop cannot be
/// detected per message. It can be detected in aggregate, because the two situations differ in the
/// one dimension the rows do carry: WHEN they were dead-lettered. A genuine backlog is made of rows
/// that already existed before recovery last ran, and it shrinks. A self-inflicted loop is made of
/// rows that appeared AFTER the previous scan began, because the previous scan's own recoveries
/// produced them.
/// </para>
/// <para>
/// Observed consequence of not measuring this: a deployment ran the cycle for thirty-nine hours,
/// adding dead-letter rows at roughly three per second, until the table reached the low millions and
/// the same buffer pool serving the inbox was thrashing. Recovery reported success the entire time,
/// because each individual recovery genuinely did succeed. What no one could see was that the work
/// it succeeded at was work it had just created.
/// </para>
/// <para>
/// This measures one cycle. Tripping on a single cycle would be wrong: a burst of unrelated failures
/// arriving during a scan looks identical for one tick. Callers require the condition to persist
/// across consecutive cycles before acting.
/// </para>
/// </remarks>
/// <docs>operations/dead-letter-queue/recovery</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterRecoveryLoopSignalTests.cs</tests>
public static class DeadLetterRecoveryLoopSignal {

  /// <summary>One cycle's evidence about where this batch came from.</summary>
  /// <param name="Considered">Rows examined this cycle.</param>
  /// <param name="Fresh">
  /// Rows dead-lettered at or after the previous scan began, and therefore attributable to that
  /// scan's own recoveries rather than to pre-existing backlog.
  /// </param>
  /// <param name="IsSelfInflicted">
  /// True when the fresh share reached the caller's threshold. One cycle is not proof; it is the
  /// per-cycle input to a consecutive-cycle decision.
  /// </param>
  public readonly record struct Measurement(int Considered, int Fresh, bool IsSelfInflicted);

  /// <summary>
  /// Measures what share of this cycle's batch postdates the previous scan.
  /// </summary>
  /// <param name="deadLetteredAt">When each row in this cycle's batch was dead-lettered.</param>
  /// <param name="previousScanStartedAt">
  /// When the previous scan began, or null on the first scan of the process.
  /// </param>
  /// <param name="freshFraction">
  /// Share of the batch that must postdate the previous scan for the cycle to read as self-inflicted.
  /// At 0.5 the cycle is flagged once recovery is merely breaking even.
  /// </param>
  /// <returns>The cycle's measurement.</returns>
  public static Measurement Measure(
      IReadOnlyList<DateTimeOffset> deadLetteredAt,
      DateTimeOffset? previousScanStartedAt,
      double freshFraction) {
    ArgumentNullException.ThrowIfNull(deadLetteredAt);

    var considered = deadLetteredAt.Count;

    // No batch is the healthy steady state, and the first scan of a process has nothing to compare
    // against. Reading either as a loop would trip the breaker exactly when a real backlog most
    // needs draining: on a cold start after an outage.
    if (considered == 0 || previousScanStartedAt is not { } baseline) {
      return new Measurement(considered, Fresh: 0, IsSelfInflicted: false);
    }

    var fresh = 0;
    for (var i = 0; i < considered; i++) {
      if (deadLetteredAt[i] >= baseline) {
        fresh++;
      }
    }

    // Inclusive: at exactly the threshold recovery is already only breaking even, and breaking even
    // forever is the condition this exists to stop.
    var selfInflicted = (double)fresh / considered >= freshFraction;
    return new Measurement(considered, fresh, selfInflicted);
  }
}
