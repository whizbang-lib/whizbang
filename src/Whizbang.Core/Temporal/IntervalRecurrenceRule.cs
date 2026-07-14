namespace Whizbang.Core.Temporal;

/// <summary>
/// A fixed-interval recurrence: fires every <see cref="Interval"/> after the prior fire. The next fire
/// is <c>after + interval</c> — the caller passes the last (or seed) fire time as the query point.
/// The trivial half of the dual C#/DB recurrence engine; the DB side advances the same way in SQL.
/// </summary>
/// <docs>fundamentals/temporal/recurrence</docs>
public sealed class IntervalRecurrenceRule : IRecurrenceRule {
  /// <summary>The fixed spacing between fires. Must be strictly positive.</summary>
  public TimeSpan Interval { get; }

  /// <summary>Creates an interval rule. Throws if <paramref name="interval"/> is not strictly positive.</summary>
  public IntervalRecurrenceRule(TimeSpan interval) {
    if (interval <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(interval), interval, "Recurrence interval must be strictly positive.");
    }
    Interval = interval;
  }

  /// <inheritdoc />
  public DateTimeOffset? NextFireAfter(DateTimeOffset after) => after + Interval;
}
