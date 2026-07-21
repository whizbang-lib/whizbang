namespace Whizbang.Core.Temporal;

/// <summary>
/// The built-in <see cref="IRecurrenceRuleFactory"/> using the home-grown interval + cron engine.
/// Registered via <c>TryAdd</c> so a developer-supplied factory (the override hook) takes precedence.
/// </summary>
/// <docs>fundamentals/temporal/recurrence</docs>
public sealed class DefaultRecurrenceRuleFactory : IRecurrenceRuleFactory {
  /// <inheritdoc />
  public IRecurrenceRule Create(RecurrenceKind kind, string? cronExpression, TimeSpan? interval, TimeZoneInfo? timeZone) =>
    kind switch {
      RecurrenceKind.OneShot => OneShotRecurrenceRule.Instance,
      RecurrenceKind.Interval => new IntervalRecurrenceRule(
        interval ?? throw new ArgumentNullException(nameof(interval), "Interval recurrence requires an interval.")),
      RecurrenceKind.Cron => new CronRecurrenceRule(
        cronExpression ?? throw new ArgumentNullException(nameof(cronExpression), "Cron recurrence requires an expression."),
        timeZone),
      _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown recurrence kind."),
    };
}
