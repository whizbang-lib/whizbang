using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Reports when the claim loop is holding work without completing it.
/// </summary>
/// <remarks>
/// <para>
/// Exists because a starved claim loop is indistinguishable from an idle one from outside the
/// process. A service can hold a small leased working set, leave the majority of a large backlog
/// unclaimed, consume almost no CPU, log nothing and stay up — so crash alerting, error-rate
/// alerting and liveness probes all report health while the backlog does not move. Widening
/// dispatch concurrency, enabling stream parallelism, adding replicas and tuning the commit path
/// are all downstream of claim; when claim is the constraint none of them move the number.
/// </para>
/// <para>
/// The distinguishing signal is already computed by the claim loop and then discarded: whether a
/// claim returned the same work set as the previous claim. <c>claim_work</c>'s eligible CTEs match
/// every leased-but-uncompleted row, so a row awaiting its completion flush is re-offered on the
/// next poll by design. A short run of repeats is therefore normal. A SUSTAINED run means rows are
/// leased to this instance and are not completing, which is the starvation case — while a genuinely
/// empty claim against an empty store is just an idle service.
/// </para>
/// <para>
/// Reporting is deliberately client-side: it needs no schema change, no extra query against a table
/// that is already the contended one, and no additional load on the path being diagnosed.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-worker</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ClaimCycleReportTests.cs</tests>
public sealed partial class ClaimCycleReport {

  private readonly int _threshold;
  private int _repeatStreak;
  private int _nextWarnAt;

  /// <summary>Consecutive claims that re-offered the previous work set.</summary>
  public int CurrentRepeatStreak => _repeatStreak;

  /// <summary>Total claim cycles observed.</summary>
  public long TotalCycles { get; private set; }

  /// <summary>Cycles that returned no work at all.</summary>
  public long EmptyCycles { get; private set; }

  /// <summary>Cycles that returned the same work set as the previous cycle.</summary>
  public long RepeatCycles { get; private set; }

  /// <summary>Cycles that returned work not seen in the previous cycle.</summary>
  public long ProductiveCycles { get; private set; }

  /// <summary>
  /// Initializes a new instance of the <see cref="ClaimCycleReport"/> class.
  /// </summary>
  /// <param name="repeatStreakThreshold">
  /// Consecutive repeats before the loop is reported as stalled. Must be at least one; a
  /// non-positive value would either warn on the first ordinary re-offer or never warn at all.
  /// </param>
  public ClaimCycleReport(int repeatStreakThreshold) {
    ArgumentOutOfRangeException.ThrowIfLessThan(repeatStreakThreshold, 1);
    _threshold = repeatStreakThreshold;
    _nextWarnAt = repeatStreakThreshold;
  }

  /// <summary>
  /// Records one claim cycle and reports a stall or a recovery when the state changes.
  /// </summary>
  /// <param name="claimedAnything">Whether the cycle returned any work.</param>
  /// <param name="wasRepeat">Whether the returned work set matched the previous cycle's.</param>
  /// <param name="logger">Logger for the stall and recovery lines.</param>
  public void Record(bool claimedAnything, bool wasRepeat, ILogger logger) {
    ArgumentNullException.ThrowIfNull(logger);

    TotalCycles++;
    if (!claimedAnything) {
      EmptyCycles++;
    } else if (wasRepeat) {
      RepeatCycles++;
    } else {
      ProductiveCycles++;
    }

    if (claimedAnything && wasRepeat) {
      _repeatStreak++;
      if (_repeatStreak >= _nextWarnAt) {
        LogClaimStalled(logger, _repeatStreak, RepeatCycles, ProductiveCycles);
        // Back off geometrically. A stalled loop keeps polling, so one line per cycle would bury
        // the incident in its own alert volume; going silent instead would hide how long it lasted.
        _nextWarnAt = _repeatStreak * 4;
      }
      return;
    }

    // An empty claim does not clear the streak on its own — an empty store and a stalled loop can
    // interleave — but genuine progress does, and is worth reporting so the incident has an end.
    if (claimedAnything) {
      if (_repeatStreak >= _threshold) {
        LogClaimRecovered(logger, _repeatStreak);
      }
      _repeatStreak = 0;
      _nextWarnAt = _threshold;
    }
  }

  [LoggerMessage(
    EventId = 92,
    Level = LogLevel.Warning,
    Message = "Claim loop has re-offered the SAME work set {RepeatStreak} consecutive times: rows are "
            + "leased to this instance and are not completing, so the backlog cannot drain even though "
            + "the process is healthy and polling. This is not an idle service. Totals so far: "
            + "{RepeatCycles} repeat cycles, {ProductiveCycles} productive cycles.")]
  static partial void LogClaimStalled(ILogger logger, int repeatStreak, long repeatCycles, long productiveCycles);

  [LoggerMessage(
    EventId = 93,
    Level = LogLevel.Information,
    Message = "Claim loop recovered after {RepeatStreak} repeated cycles — fresh work claimed.")]
  static partial void LogClaimRecovered(ILogger logger, int repeatStreak);
}
