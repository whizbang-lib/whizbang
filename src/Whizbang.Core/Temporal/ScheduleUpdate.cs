namespace Whizbang.Core.Temporal;

/// <summary>
/// The mutable configuration of an existing schedule, for <see cref="IScheduleManager.UpdateAsync"/>.
/// Carries exactly the fields <c>wh_update_schedule</c> can change — the schedule's identity
/// (id / key), its routing (stream), and its event type are <b>immutable</b>; to change those, call
/// <see cref="IScheduleManager.CreateAsync"/> again with the same <see cref="ScheduleDefinition.Key"/>
/// (the idempotent create-or-update path rewrites the whole row).
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public sealed record ScheduleUpdate {
  /// <summary>How the schedule recurs after the update.</summary>
  public RecurrenceKind Kind { get; init; }

  /// <summary>Spacing for <see cref="RecurrenceKind.Interval"/>. Required for that kind.</summary>
  public TimeSpan? Interval { get; init; }

  /// <summary>Cron expression for <see cref="RecurrenceKind.Cron"/>. Required for that kind.</summary>
  public string? Cron { get; init; }

  /// <summary>Timezone for cron evaluation (defaults to UTC).</summary>
  public string? TimeZone { get; init; }

  /// <summary>Anchor the recomputed next fire (defaults to now).</summary>
  public DateTimeOffset? StartAt { get; init; }

  /// <summary>Optional upper bound.</summary>
  public DateTimeOffset? UntilAt { get; init; }

  /// <summary>Optional cap on total occurrences.</summary>
  public long? MaxOccurrences { get; init; }

  /// <summary>Missed-fire handling.</summary>
  public MisfirePolicy MisfirePolicy { get; init; } = MisfirePolicy.Coalesce;

  /// <summary>
  /// Burst bound for <see cref="Temporal.MisfirePolicy.CatchUp"/> — never replay occurrences older than
  /// this. <c>null</c> = unbounded.
  /// </summary>
  public TimeSpan? CatchUpLookback { get; init; }

  /// <summary>Per-schedule delivery guarantee.</summary>
  public ScheduleDeliveryGuarantee DeliveryGuarantee { get; init; } = ScheduleDeliveryGuarantee.AtLeastOnce;

  /// <summary>JSON payload carried by each occurrence (null =&gt; none).</summary>
  public string? EventDataJson { get; init; }

  /// <summary>JSON scope carried by each occurrence (null =&gt; none).</summary>
  public string? ScopeJson { get; init; }
}

/// <summary>The outcome of a successful update: the recomputed next fire and the new row version.</summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public readonly record struct ScheduleUpdateResult(DateTimeOffset NextFireAt, long Version);
