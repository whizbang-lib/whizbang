namespace Whizbang.Core.Signals;

/// <summary>
/// How a pull source slows down while the condition it polls for stays absent.
/// </summary>
/// <param name="AfterEmptyTicks">
/// Empty ticks tolerated at the base interval before the source starts stretching. A few empty
/// ticks are ordinary between bursts; stretching on the first one would add latency to every lull.
/// </param>
/// <param name="Ceiling">
/// The longest interval the source may reach. Bounds how long a store can hold work the push
/// transport failed to announce before a poll finds it.
/// </param>
/// <remarks>
/// Past the threshold the interval doubles per empty tick up to the ceiling. A tick that finds work,
/// or a reschedule from outside (the push transport becoming unavailable tightens the cadence),
/// returns the source to its base interval and restarts the count. With every queue empty, poll
/// loops alone were committing over a hundred transactions a second per busy database; this is
/// what lets an idle service cost an idle amount.
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
/// <tests>tests/Whizbang.Core.Tests/Signals/PollSignalSourceIdleBackoffTests.cs</tests>
public sealed record PollIdleBackoff(int AfterEmptyTicks, TimeSpan Ceiling) {
  /// <summary>Empty ticks tolerated before the interval stretches; at least one.</summary>
  public int AfterEmptyTicks { get; } = AfterEmptyTicks >= 1
    ? AfterEmptyTicks
    : throw new ArgumentOutOfRangeException(nameof(AfterEmptyTicks), "At least one empty tick must pass before the interval stretches.");

  /// <summary>The longest interval the source may reach; positive.</summary>
  public TimeSpan Ceiling { get; } = Ceiling > TimeSpan.Zero
    ? Ceiling
    : throw new ArgumentOutOfRangeException(nameof(Ceiling), "The ceiling must be positive.");

  /// <summary>The interval after <paramref name="emptyStreak"/> consecutive empty ticks from <paramref name="baseInterval"/>.</summary>
  /// <param name="baseInterval">The interval the source polls at while work is flowing.</param>
  /// <param name="emptyStreak">Consecutive ticks that found nothing.</param>
  /// <returns>The base interval until the threshold, then doubled per tick and capped at the ceiling, never below the base.</returns>
  public TimeSpan IntervalAfter(TimeSpan baseInterval, int emptyStreak) {
    if (emptyStreak <= AfterEmptyTicks || Ceiling <= baseInterval) {
      return baseInterval;
    }
    var doublings = Math.Min(emptyStreak - AfterEmptyTicks, 30);
    var stretched = baseInterval.Ticks * (1L << doublings);
    return stretched >= Ceiling.Ticks || stretched < baseInterval.Ticks ? Ceiling : TimeSpan.FromTicks(stretched);
  }
}
