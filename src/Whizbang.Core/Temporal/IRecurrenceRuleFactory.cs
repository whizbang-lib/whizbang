namespace Whizbang.Core.Temporal;

/// <summary>
/// Builds an <see cref="IRecurrenceRule"/> from a schedule's stored recurrence configuration
/// (<c>recurrence_kind</c> + <c>cron</c> / <c>interval_ms</c> + <c>timezone</c>). This is the C#
/// <b>override hook</b>: the default implementation uses the home-grown interval + cron engine, but a
/// developer may register their own factory (e.g. delegating cron parsing to another tool) and it wins
/// via <c>TryAdd</c>. Kept separate from the DB-side next-fire so both halves of the dual engine stay
/// swappable together.
/// </summary>
/// <docs>fundamentals/temporal/recurrence</docs>
public interface IRecurrenceRuleFactory {
  /// <summary>
  /// Creates the rule for a schedule. <paramref name="interval"/> is required for
  /// <see cref="RecurrenceKind.Interval"/>; <paramref name="cronExpression"/> for
  /// <see cref="RecurrenceKind.Cron"/> (evaluated in <paramref name="timeZone"/>, UTC when null).
  /// </summary>
  IRecurrenceRule Create(RecurrenceKind kind, string? cronExpression, TimeSpan? interval, TimeZoneInfo? timeZone);
}
