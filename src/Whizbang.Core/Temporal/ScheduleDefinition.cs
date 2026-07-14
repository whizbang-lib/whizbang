namespace Whizbang.Core.Temporal;

/// <summary>
/// The configuration for creating (or idempotently updating) a schedule via <see cref="IScheduleManager"/>.
/// Maps to <c>wh_create_schedule</c>; the initial <c>next_fire_at</c> is computed DB-side from the
/// recurrence config (one-shot at <see cref="StartAt"/>, interval one <see cref="Interval"/> out or at
/// <see cref="StartAt"/>, cron via the next match after <see cref="StartAt"/>/now).
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public sealed record ScheduleDefinition {
  /// <summary>The occurrence event type to spawn each fire. Required.</summary>
  public required string EventType { get; init; }

  /// <summary>Explicit id; when null a time-ordered id is generated.</summary>
  public Guid? ScheduleId { get; init; }

  /// <summary>Optional developer key — creating again with the same key updates in place (idempotent).</summary>
  public string? Key { get; init; }

  /// <summary>The stream that owns the schedule (partition routing + arm-on-mutation NOTIFY target).</summary>
  public Guid StreamId { get; init; }

  /// <summary>Partition number (defaults to 0).</summary>
  public int PartitionNumber { get; init; }

  /// <summary>How the schedule recurs.</summary>
  public RecurrenceKind Kind { get; init; }

  /// <summary>Spacing for <see cref="RecurrenceKind.Interval"/>. Required for that kind.</summary>
  public TimeSpan? Interval { get; init; }

  /// <summary>Cron expression for <see cref="RecurrenceKind.Cron"/>. Required for that kind.</summary>
  public string? Cron { get; init; }

  /// <summary>Timezone for cron evaluation (defaults to UTC).</summary>
  public string? TimeZone { get; init; }

  /// <summary>Anchor / one-shot fire time; defaults to now for interval/cron.</summary>
  public DateTimeOffset? StartAt { get; init; }

  /// <summary>Optional upper bound — the schedule completes once the next fire would exceed this.</summary>
  public DateTimeOffset? UntilAt { get; init; }

  /// <summary>Optional cap on total occurrences.</summary>
  public long? MaxOccurrences { get; init; }

  /// <summary>Missed-fire handling (default coalesce).</summary>
  public MisfirePolicy MisfirePolicy { get; init; } = MisfirePolicy.Coalesce;

  /// <summary>
  /// Burst bound for <see cref="Temporal.MisfirePolicy.CatchUp"/>: never replay occurrences older than
  /// this. A long outage therefore replays only the recent window instead of an unbounded storm.
  /// <c>null</c> = unbounded. Ignored by the other misfire policies (they fast-forward anyway).
  /// </summary>
  public TimeSpan? CatchUpLookback { get; init; }

  /// <summary>Per-schedule delivery guarantee (default at-least-once).</summary>
  public ScheduleDeliveryGuarantee DeliveryGuarantee { get; init; } = ScheduleDeliveryGuarantee.AtLeastOnce;

  /// <summary>JSON payload carried by each occurrence (null =&gt; none).</summary>
  public string? EventDataJson { get; init; }

  /// <summary>JSON scope (tenant/user) carried by each occurrence (null =&gt; none).</summary>
  public string? ScopeJson { get; init; }
}
