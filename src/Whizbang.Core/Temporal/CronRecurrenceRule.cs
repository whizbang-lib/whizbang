namespace Whizbang.Core.Temporal;

/// <summary>
/// A cron-based recurrence: wraps a home-grown <see cref="CronExpression"/> evaluated in a fixed
/// timezone (UTC when none is supplied). The C# half of the dual engine; the DB side computes the same
/// next-fire in SQL for the atomic claim+advance.
/// </summary>
/// <docs>fundamentals/temporal/recurrence</docs>
/// <remarks>
/// Creates a cron rule. Throws <see cref="FormatException"/> if <paramref name="expression"/> is
/// malformed. <paramref name="timeZone"/> defaults to UTC.
/// </remarks>
public sealed class CronRecurrenceRule(string expression, TimeZoneInfo? timeZone = null) : IRecurrenceRule {
  private readonly CronExpression _cron = CronExpression.Parse(expression);

  /// <summary>The cron text this rule was built from (for diagnostics / round-tripping to the DB).</summary>
  public string Expression { get; } = expression;

  /// <summary>The timezone cron fields are evaluated in.</summary>
  public TimeZoneInfo TimeZone { get; } = timeZone ?? TimeZoneInfo.Utc;

  /// <inheritdoc />
  public DateTimeOffset? NextFireAfter(DateTimeOffset after) => _cron.NextFireAfter(after, TimeZone);
}
