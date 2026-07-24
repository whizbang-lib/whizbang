namespace Whizbang.Core.Temporal;

/// <summary>
/// How a schedule recurs. Matches the <c>recurrence_kind</c> column on <c>wh_schedules</c> (SMALLINT):
/// <see cref="OneShot"/> = 0, <see cref="Interval"/> = 1, <see cref="Cron"/> = 2.
/// </summary>
/// <docs>fundamentals/temporal/recurrence</docs>
public enum RecurrenceKind {
  /// <summary>Fires exactly once at its scheduled time; never recurs.</summary>
  OneShot = 0,

  /// <summary>Fires every fixed <see cref="System.TimeSpan"/> after the prior fire.</summary>
  Interval = 1,

  /// <summary>Fires on a cron schedule (see <see cref="CronExpression"/>), evaluated in a timezone.</summary>
  Cron = 2,
}
