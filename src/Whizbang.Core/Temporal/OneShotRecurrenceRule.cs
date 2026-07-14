namespace Whizbang.Core.Temporal;

/// <summary>
/// A non-recurring rule: a one-shot schedule fires once at its scheduled time and never again, so
/// <see cref="NextFireAfter"/> always returns <c>null</c>. The worker sets the single fire time at
/// creation; after firing, a <c>null</c> next-fire marks the schedule complete.
/// </summary>
/// <docs>fundamentals/temporal/recurrence</docs>
public sealed class OneShotRecurrenceRule : IRecurrenceRule {
  /// <summary>The shared stateless instance.</summary>
  public static OneShotRecurrenceRule Instance { get; } = new();

  private OneShotRecurrenceRule() { }

  /// <inheritdoc />
  public DateTimeOffset? NextFireAfter(DateTimeOffset after) => null;
}
