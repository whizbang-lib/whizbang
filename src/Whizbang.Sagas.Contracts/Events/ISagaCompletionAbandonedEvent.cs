namespace Whizbang.Sagas;

/// <summary>
/// Framework-emitted terminal event when a saga's watchdog has exhausted
/// its re-arm budget without observing completion. Consumers opt into
/// reacting to this (e.g., page operators, post to a triage channel,
/// flip a UI state to "needs attention").
/// </summary>
/// <remarks>
/// <para>
/// Emitted by <c>BaseSagaService.TryRecoverViaWatchdogTickAsync</c> when
/// the incoming <see cref="ISagaCompletionWatchdogTickEvent.RescheduleCount"/>
/// has reached or exceeded the configured
/// <c>SagaOptions.WatchdogBackoff</c> schedule length. Unlike a normal
/// <c>SagaCompletedEvent</c>, this signals "we tried, but no further
/// watchdog re-arms are planned" — the saga is operationally stuck and
/// human investigation is needed.
/// </para>
/// <para>
/// Carries the same <see cref="ISagaEvent.SagaName"/> + <see cref="ISagaEvent.EntityId"/>
/// fields as the rest of the lifecycle so existing receptor-discovery
/// infrastructure picks it up. Adds <see cref="RescheduleCount"/> so consumers
/// can record how many attempts were made before the abandon.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration</docs>
/// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MaxConsecutiveStalls_AbandonsAsync</tests>
/// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:Complete_RecoversWithoutReArmAsync</tests>
public interface ISagaCompletionAbandonedEvent : ISagaEvent {

  /// <summary>
  /// The reschedule count of the watchdog tick that triggered the
  /// abandon — equals the size of <c>SagaOptions.WatchdogBackoff</c>
  /// at abandon time. Useful for triage telemetry ("we tried N times").
  /// </summary>
  int RescheduleCount { get; }
}
