namespace Whizbang.Core.Signals;

/// <summary>
/// Doorbell signal that one or more schedules owned by the receiving instance are due
/// (<c>next_fire_at &lt;= NOW()</c>). Wire-name <c>"schedule"</c> matches the payload emitted by
/// <c>notify_schedules_due()</c> and the arm-on-mutation NOTIFY. Doorbell-not-data — on receipt the
/// instance queries <c>wh_schedules</c> for its actual due schedules and fires them via the temporal
/// worker's leased claim. Targeted + best-effort: the correctness backstop is the schedule pull-source
/// plus the authoritative DB claim, so a missed NOTIFY only costs a little latency.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
[WireName("schedule")]
public readonly record struct ScheduleDueSignal : ISignal {
  /// <inheritdoc />
  public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
  /// <inheritdoc />
  public static SignalTargeting Targeting => SignalTargeting.Targeted;
}
