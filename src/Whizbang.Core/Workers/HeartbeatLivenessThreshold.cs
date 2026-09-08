namespace Whizbang.Core.Workers;

/// <summary>
/// The one derivation of "how stale may a heartbeat row get before an instance reads as dead",
/// shared by the writer (<see cref="HeartbeatWorker"/>) and every reader that judges liveness from
/// the row (the lifecycle monitor, the SQL reap). Both sides compute it from the same
/// <see cref="HeartbeatWorkerOptions"/>, so the threshold can never sit below the cadence again:
/// the row is refreshed every fast interval, or every slow interval while the session alive-lock is
/// the primary liveness signal, and a single delayed beat must never look like a death.
/// </summary>
/// <remarks>
/// <para>
/// The threshold is two of the slowest configured cadence plus one fast interval as a latency
/// allowance. With the defaults (30 s fast, 60 s slow) that is 150 s; in
/// <see cref="HeartbeatLivenessSourceMode.HeartbeatTableOnly"/> mode (30 s only) it is 90 s. A
/// beat that arrives late by the whole fast interval, or one whole slow beat that is lost, still
/// leaves the row fresh. Previously the monitor used a 30 s constant against a 30 s cadence: zero
/// margin, so any heartbeat latency at all could announce a live instance dead.
/// </para>
/// <para>
/// <see cref="WatchdogLead(HeartbeatWorkerOptions)"/> is how far ahead of the threshold the writer
/// forces an out-of-cadence beat when its regular beat is late (belt and suspenders on the writer
/// side, so a stalled tick never reaches the reader's threshold).
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/HeartbeatLivenessThresholdTests.cs</tests>
public static class HeartbeatLivenessThreshold {
  /// <summary>How stale <c>last_heartbeat_at</c> may be before the instance counts as dead.</summary>
  /// <param name="options">The heartbeat cadence the writer runs.</param>
  /// <returns>Two of the slowest cadence plus one fast interval.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
  public static TimeSpan StaleThreshold(HeartbeatWorkerOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var fast = _atLeastOneSecond(options.IntervalSeconds);
    var slowest = options.LivenessSourceMode == HeartbeatLivenessSourceMode.HeartbeatTableOnly
      ? fast
      : Math.Max(fast, _atLeastOneSecond(options.SlowIntervalSeconds));
    return TimeSpan.FromSeconds((2L * slowest) + fast);
  }

  /// <summary>
  /// The threshold in whole seconds, for callers that hand it to SQL
  /// (<c>is_instance_alive(p_instance_id, p_heartbeat_threshold_seconds)</c>).
  /// </summary>
  /// <param name="options">The heartbeat cadence the writer runs.</param>
  /// <returns>The stale threshold rounded up to whole seconds.</returns>
  public static int StaleThresholdSeconds(HeartbeatWorkerOptions options)
    => (int)Math.Ceiling(StaleThreshold(options).TotalSeconds);

  /// <summary>
  /// How far before the threshold the writer forces a beat regardless of cadence: one fast
  /// interval, which is also the latency allowance folded into <see cref="StaleThreshold"/>.
  /// </summary>
  /// <param name="options">The heartbeat cadence the writer runs.</param>
  /// <returns>One fast interval.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
  public static TimeSpan WatchdogLead(HeartbeatWorkerOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return TimeSpan.FromSeconds(_atLeastOneSecond(options.IntervalSeconds));
  }

  private static int _atLeastOneSecond(int seconds) => Math.Max(1, seconds);
}
