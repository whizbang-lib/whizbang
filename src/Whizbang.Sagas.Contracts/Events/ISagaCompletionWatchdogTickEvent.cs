namespace Whizbang.Sagas;

/// <summary>
/// Framework-armed wake-up event the orchestrator emits when a saga is initiated
/// (via <c>BaseSagaService.InitiateSagaAsync</c>) so the saga has a guaranteed
/// completion check even if every per-item terminal event is silently dropped
/// or strands the projection.
/// </summary>
/// <remarks>
/// <para>
/// Carries the same <see cref="ISagaEvent.SagaName"/> + <see cref="ISagaEvent.EntityId"/>
/// fields as the rest of the lifecycle so existing receptor-discovery
/// infrastructure picks it up. Adds <see cref="RescheduleCount"/> so the receptor can
/// implement an exponential backoff and eventually give up cleanly (with an alert
/// the operator can act on) rather than re-arming forever.
/// </para>
/// <para>
/// The event is published via <c>DispatchOptions.WithScheduledFor(...)</c> so it
/// lands in <c>wh_outbox</c> with <c>scheduled_for</c> populated; mig 040
/// FetchOutboxInboxBatch and mig 049 NotifyScheduledRetryDue handle the wake-up.
/// </para>
/// <para>
/// Stream-routing context: emitted with the saga's own stream id so
/// <c>notify_instance_owners</c> can deliver the wake to the saga's owning instance.
/// Per the comment in mig 049, NULL stream_id rows are skipped.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration</docs>
/// <tests>tests/Whizbang.Sagas.Tests/CompletionOrchestrationGapTests.cs:SagaCompletionWatchdogTickEvent_ContractTypeExistsAsync</tests>
public interface ISagaCompletionWatchdogTickEvent : ISagaEvent {

  /// <summary>
  /// Number of times this watchdog has already re-armed for the saga without finding
  /// a terminal state. Starts at 0 on initial arm; the receptor increments on each
  /// re-arm. Drives the exponential backoff schedule (30s → 2m → 8m → 30m → abandon).
  /// </summary>
  int RescheduleCount { get; }
}
