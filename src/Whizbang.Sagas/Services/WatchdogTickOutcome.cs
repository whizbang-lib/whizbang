namespace Whizbang.Sagas.Services;

/// <summary>
/// Outcome of <see cref="BaseSagaService{T1,T2,T3,T4,T5,T6,T7,T8,T9}.TryRecoverViaWatchdogTickAsync"/>.
/// </summary>
/// <docs>fundamentals/sagas/completion-orchestration#watchdog-tick-outcomes</docs>
/// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs</tests>
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

  /// <summary>
  /// The saga projection shows its completion was already dispatched — typically by the per-item
  /// fast path before this tick fired, which is the normal, healthy case. The chain ends here: no
  /// next tick, no stall counted, no item resolved and no abandon event.
  /// </summary>
  AlreadyComplete = 4,

  /// <summary>
  /// The projection loader found no saga for the tick (deleted, or never known). The chain ends
  /// here, as for <see cref="AlreadyComplete"/>. A service that wires no projection loader never
  /// sees this outcome: an absent loader is not evidence that the saga is missing, so its ticks keep
  /// re-arming.
  /// </summary>
  SagaNotFound = 5,
}
