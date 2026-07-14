namespace Whizbang.Core.Temporal;

/// <summary>
/// Per-schedule delivery guarantee for spawned occurrences. Occurrence <em>creation</em> is always
/// exactly-once; this governs <em>redelivery</em> on downstream failure. Matches the
/// <c>delivery_guarantee</c> column on <c>wh_schedules</c> (SMALLINT).
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public enum ScheduleDeliveryGuarantee {
  /// <summary>Retry until handled (default); relies on idempotent handlers.</summary>
  AtLeastOnce = 0,

  /// <summary>Never redeliver — safer for dangerous non-idempotent ops; failures land in wh_schedule_runs.</summary>
  AtMostOnce = 1,
}
