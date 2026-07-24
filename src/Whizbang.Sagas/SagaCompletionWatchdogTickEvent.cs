namespace Whizbang.Sagas;

/// <summary>
/// Concrete watchdog-tick event the framework emits from
/// <c>BaseSagaService.InitiateSagaAsync</c> so every saga has a guaranteed
/// completion wake-up the orchestrator can route to even when per-item terminal
/// events are dropped or strand the projection.
/// </summary>
/// <remarks>
/// <para>
/// Concrete (not abstract) so the framework can construct it directly without
/// requiring consumers to supply a per-saga subclass. Implements
/// <see cref="ISagaCompletionWatchdogTickEvent"/> so receptors discovered by
/// type pick it up; extends <see cref="SagaEventBase"/> to inherit
/// <see cref="Whizbang.Core.IEvent"/> implementation. Carrying
/// <see cref="SagaName"/> + <see cref="EntityId"/> on every instance keeps it
/// shape-compatible with the rest of the saga lifecycle, which means the
/// existing message-registry routing handles it without a special case.
/// </para>
/// <para>
/// Emitted with <see cref="Whizbang.Core.Dispatch.DispatchOptions.ScheduledFor"/>
/// set to "expected completion + slack" so it lands in <c>wh_outbox</c> with
/// <c>scheduled_for</c> populated; the pickup query (mig 040) and the
/// NOTIFY-based wake (mig 049) handle the timing without a polling tax.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration</docs>
public class SagaCompletionWatchdogTickEvent : SagaEventBase, ISagaCompletionWatchdogTickEvent {

  /// <summary>The name of the saga whose completion this tick is checking.</summary>
  public string SagaName { get; set; } = string.Empty;

  /// <summary>The saga's entity id — the business entity the saga was initiated for.</summary>
  public Guid EntityId { get; set; }

  /// <summary>Stream id this tick is bound to (the saga's stream).</summary>
  /// <remarks>
  /// Used by <c>notify_instance_owners</c> (mig 049) to route the elapsed-schedule
  /// wake-up to the saga's owning instance. The mig 049 SQL filters NULL stream_ids
  /// because the NOTIFY path can't deliver to a row with no owning stream, so this
  /// field MUST be populated when the framework arms the tick.
  /// </remarks>
  public Guid StreamId { get; set; }

  /// <summary>
  /// How many times this watchdog has re-armed for the saga without finding a
  /// terminal state. Retained primarily for observability — the adaptive
  /// scheduler keys abandon on <see cref="ConsecutiveStallCount"/>, not this
  /// counter.
  /// </summary>
  public int RescheduleCount { get; set; }

  /// <summary>
  /// Wall-clock time when the producing receptor observed the
  /// <see cref="LastObservedCompleted"/> / <see cref="LastObservedFailed"/>
  /// counts. <c>null</c> on the very first watchdog tick (no prior measurement
  /// has been taken yet — the framework treats it as the baseline). The
  /// adaptive scheduler uses <c>now - LastObservedAt</c> as the denominator
  /// for the items-per-second rate that drives the next-tick ETA.
  /// </summary>
  public DateTimeOffset? LastObservedAt { get; set; }

  /// <summary>
  /// Per-item aggregate <c>Completed</c> count captured by the previous tick.
  /// Used by the next tick to compute progress delta (current − last) and
  /// derive the items-per-second completion rate. Zero on the very first tick.
  /// </summary>
  public int LastObservedCompleted { get; set; }

  /// <summary>
  /// Per-item aggregate <c>Failed</c> count captured by the previous tick.
  /// Combined with <see cref="LastObservedCompleted"/> the framework computes
  /// the terminal-event delta between ticks; positive delta resets the stall
  /// counter, zero delta increments it.
  /// </summary>
  public int LastObservedFailed { get; set; }

  /// <summary>
  /// Number of consecutive watchdog ticks that have observed zero progress
  /// (no new terminal events) since the previous tick. Once this reaches
  /// <see cref="Whizbang.Sagas.SagaOptions.MaxConsecutiveStalls"/> the
  /// framework emits <see cref="Whizbang.Sagas.SagaCompletionAbandonedEvent"/>
  /// instead of re-arming. Progress resets it to zero — slow sagas don't
  /// trigger abandon, only stuck ones.
  /// </summary>
  public int ConsecutiveStallCount { get; set; }
}
