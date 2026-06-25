namespace Whizbang.Sagas.Services;

/// <summary>
/// Outcome of <see cref="BaseSagaService{T1,T2,T3,T4,T5,T6,T7,T8,T9}.TryRecoverViaWatchdogTickAsync"/>.
/// </summary>
public enum WatchdogTickOutcome {
  /// <summary>
  /// The slow path observed completion and emitted <c>SagaCompletedEvent</c>
  /// via <c>PublishOnceAsync</c>. No further ticks are scheduled.
  /// </summary>
  Recovered = 1,

  /// <summary>
  /// The slow path found the saga still in progress; the next watchdog tick
  /// was published with <c>scheduledFor</c> set to the configured
  /// <see cref="SagaOptions.WatchdogBackoff"/> delay.
  /// </summary>
  ReArmed = 2,

  /// <summary>
  /// The slow path found the saga still in progress AND the configured
  /// <see cref="SagaOptions.WatchdogBackoff"/> schedule is exhausted. The
  /// framework published <see cref="SagaCompletionAbandonedEvent"/> instead
  /// of a next tick — the saga is operationally stuck and needs triage.
  /// </summary>
  Abandoned = 3,
}
