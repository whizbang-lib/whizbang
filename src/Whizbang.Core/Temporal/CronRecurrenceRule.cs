namespace Whizbang.Core.Temporal;

/// <summary>
/// A cron-based recurrence: wraps a home-grown <see cref="CronExpression"/> evaluated in a fixed
/// timezone (UTC when none is supplied). The C# half of the dual engine; the DB side computes the same
/// next-fire in SQL for the atomic claim+advance.
/// </summary>
/// <docs>fundamentals/temporal/recurrence</docs>
public sealed class CronRecurrenceRule : IRecurrenceRule {
  private readonly CronExpression _cron;
  private readonly TimeZoneInfo _timeZone;

  /// <summary>The cron text this rule was built from (for diagnostics / round-tripping to the DB).</summary>
  public string Expression { get; }

  /// <summary>The timezone cron fields are evaluated in.</summary>
  public TimeZoneInfo TimeZone => _timeZone;

  /// <summary>
  /// Creates a cron rule. Throws <see cref="FormatException"/> if <paramref name="expression"/> is
  /// malformed. <paramref name="timeZone"/> defaults to UTC.
  /// </summary>
  public CronRecurrenceRule(string expression, TimeZoneInfo? timeZone = null) {
    _cron = CronExpression.Parse(expression);
    Expression = expression;
    _timeZone = timeZone ?? TimeZoneInfo.Utc;
  }

  /// <inheritdoc />
  public DateTimeOffset? NextFireAfter(DateTimeOffset after) => _cron.NextFireAfter(after, _timeZone);
}
