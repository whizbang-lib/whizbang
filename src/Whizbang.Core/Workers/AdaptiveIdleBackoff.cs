namespace Whizbang.Core.Workers;

/// <summary>
/// Cadence controller for a periodic probe that must be quick when there is work and quiet when
/// there is none. Each call to <see cref="Next(bool)"/> returns the delay to wait before the next
/// probe: the floor whenever the last probe found work, otherwise a delay that doubles from the
/// floor up to the ceiling. Any finding of work snaps the cadence back to the floor.
/// </summary>
/// <remarks>
/// An idle service with several instances was measured issuing tens of statements per second
/// against a shared database from fixed-cadence probes alone (durable signal tail, depth counts,
/// lock retries). Every one of those probes has a doorbell or a signal as its fast path; the poll is
/// the safety net, and a safety net can afford to relax while nothing is happening. The controller
/// has no knobs of its own: floor and ceiling come from options the worker already has.
/// </remarks>
/// <docs>fundamentals/workers/idle-footprint</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/AdaptiveIdleBackoffTests.cs</tests>
public sealed class AdaptiveIdleBackoff {
  private readonly TimeSpan _floor;
  private readonly TimeSpan _ceiling;
  private readonly double _multiplier;

  /// <summary>Creates the controller.</summary>
  /// <param name="floor">The cadence while work is being found; also the first idle delay.</param>
  /// <param name="ceiling">The longest idle delay; the cadence converges here while idle.</param>
  /// <param name="multiplier">Growth per idle probe (default 2).</param>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when the floor is not positive, the ceiling is below the floor, or the multiplier is not above 1.
  /// </exception>
  public AdaptiveIdleBackoff(TimeSpan floor, TimeSpan ceiling, double multiplier = 2.0) {
    if (floor <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(floor), floor, "The floor must be positive.");
    }
    if (ceiling < floor) {
      throw new ArgumentOutOfRangeException(nameof(ceiling), ceiling, "The ceiling must be at least the floor.");
    }
    if (multiplier <= 1.0) {
      throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, "The multiplier must be above 1.");
    }
    _floor = floor;
    _ceiling = ceiling;
    _multiplier = multiplier;
    Current = floor;
  }

  /// <summary>The delay the next idle probe will wait.</summary>
  public TimeSpan Current { get; private set; }

  /// <summary>The floor cadence.</summary>
  public TimeSpan Floor => _floor;

  /// <summary>The ceiling cadence.</summary>
  public TimeSpan Ceiling => _ceiling;

  /// <summary>
  /// Records the outcome of the probe that just ran and returns the delay to wait before the next.
  /// </summary>
  /// <param name="foundWork">True when the probe found something to do.</param>
  /// <returns>The floor when work was found; otherwise the current idle delay, which then grows.</returns>
  public TimeSpan Next(bool foundWork) {
    if (foundWork) {
      Current = _floor;
      return _floor;
    }
    var wait = Current;
    var grown = TimeSpan.FromTicks((long)Math.Min(Current.Ticks * _multiplier, _ceiling.Ticks));
    Current = grown > _ceiling ? _ceiling : grown;
    return wait;
  }

  /// <summary>Snaps the cadence back to the floor, for an external wake (a signal, a doorbell).</summary>
  public void Reset() => Current = _floor;
}
