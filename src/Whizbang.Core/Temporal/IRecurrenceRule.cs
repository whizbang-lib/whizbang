namespace Whizbang.Core.Temporal;

/// <summary>
/// Computes the next fire time for a schedule. Implementations are the home-grown interval and cron
/// rules; a developer may supply their own (an override hook) via <see cref="IRecurrenceRuleFactory"/>.
/// The same computation exists DB-side (a Postgres cron function) so the atomic claim+advance can
/// advance <c>next_fire_at</c> without a C# round-trip; this C# side drives the in-memory timer heap
/// and the management API.
/// </summary>
/// <docs>fundamentals/temporal/recurrence</docs>
public interface IRecurrenceRule {
  /// <summary>
  /// The next fire time strictly after <paramref name="after"/> (exclusive), or <c>null</c> if the
  /// rule has no further occurrences.
  /// </summary>
  DateTimeOffset? NextFireAfter(DateTimeOffset after);
}
