namespace Whizbang.Core.Workers;

/// <summary>
/// Single source of truth for "how long has this pod been idle?" — used by
/// <see cref="BackupTickCoordinator"/> to decide when to engage backup-poll
/// fallback after a configurable quiet period, and by future observability
/// surfaces to expose the last-activity-source per pod.
/// </summary>
/// <remarks>
/// <para>
/// "Activity" is whatever proves the pod is doing real work in the work-coordination
/// sense — a NOTIFY arrival, a successful claim batch, a stamper round, an
/// observed gate-reconnect, a heartbeat fired. Callers each pick a descriptive
/// <c>source</c> string so the most recent activity is attributable in
/// diagnostics and logs.
/// </para>
/// <para>
/// Slice 4 of zero-idle-polling introduces this contract. Slice 4's
/// <see cref="BackupTickCoordinator"/> reads
/// <see cref="TimeSinceLastActivity"/> against a configured idle threshold
/// (default 30 s) to decide between its ASLEEP state (zero application-layer
/// DB calls) and POLLING state (registered backup ticks running on a
/// configured cadence).
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/idle-activity-tracking</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/IdleActivityTrackerTests.cs</tests>
public interface IIdleActivityTracker {
  /// <summary>
  /// Resets the idle timer. Called by hook consumers when real activity has
  /// just occurred (NOTIFY arrival, work batch claimed, stamper round
  /// stamped some events, heartbeat fired, etc.). The <paramref name="source"/>
  /// string is captured into <see cref="LastActivitySource"/> for diagnostic
  /// visibility.
  /// </summary>
  /// <param name="source">
  /// Short identifier of where the activity came from (e.g. <c>"claim"</c>,
  /// <c>"stamp"</c>, <c>"notify"</c>, <c>"heartbeat"</c>). Must be non-null;
  /// empty string is permitted but discouraged.
  /// </param>
  void Touch(string source);

  /// <summary>
  /// Wall-clock duration since the most recent <see cref="Touch"/> call. Reads
  /// from the underlying <see cref="TimeProvider"/> so tests using
  /// <c>FakeTimeProvider</c> can advance the clock deterministically.
  /// </summary>
  TimeSpan TimeSinceLastActivity { get; }

  /// <summary>Timestamp of the most recent <see cref="Touch"/> call.</summary>
  DateTimeOffset LastActivityAt { get; }

  /// <summary>
  /// The <c>source</c> string passed to the most recent <see cref="Touch"/>
  /// call. Empty if no activity has ever been touched (only at startup before
  /// the first hook fires).
  /// </summary>
  string LastActivitySource { get; }
}
