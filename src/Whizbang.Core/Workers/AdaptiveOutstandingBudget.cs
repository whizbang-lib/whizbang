namespace Whizbang.Core.Workers;

/// <summary>
/// Bounds how much claimed-but-unprocessed work a claim loop may hold at once, in <b>rows</b>.
/// </summary>
/// <remarks>
/// <para>
/// The claim loop hands a batch to its channel and immediately claims again, while leases live for
/// the full lease duration regardless of whether the rows are ever reached. Outstanding work
/// therefore accumulates across cycles until the whole backlog is held, at which point leases lapse
/// in bulk, the rows are re-claimed, and each re-claim charges another attempt — so a backlog
/// destroys its own retry budget without a single handler failing.
/// </para>
/// <para>
/// Bounding the <i>batch size</i> cannot fix this. At any batch size a fast loop still accumulates
/// the entire backlog; a smaller batch only changes how long that takes. The quantity that must be
/// bounded is the <i>outstanding</i> total, and it must be counted in <b>rows</b> — the unit leases
/// are held in — not streams, whose rows-per-stream ratio varies by orders of magnitude.
/// </para>
/// <para>
/// The budget is the amount plausibly drainable inside one lease window:
/// <c>drainRate × leaseSeconds × safetyFactor</c>. The safety factor is real headroom rather than
/// timidity: lease expiry is a cliff, not a gradual degradation, so running at the full computed
/// capacity means any slowdown tips straight into mass expiry.
/// </para>
/// <para>
/// <b>Cold start begins at the floor.</b> A restart carrying a large backlog has no drain history
/// and is precisely when unbounded claiming does its damage, so capacity is earned from observed
/// completions rather than assumed. Exponential smoothing supplies the ramp — a single good sample
/// is not evidence of sustained capacity.
/// </para>
/// <para>AOT-safe: plain arithmetic over value types, no reflection.</para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/AdaptiveOutstandingBudgetTests.cs</tests>
public sealed class AdaptiveOutstandingBudget {
  private readonly int _leaseSeconds;
  private readonly int _ceiling;
  private readonly int _floor;
  private readonly double _safetyFactor;
  private readonly double _smoothing;
  private double _drainRatePerSecond;
  private bool _hasSample;
  private int _current;

  /// <summary>Creates a budget that starts at <paramref name="floor"/> and grows with observed drain.</summary>
  /// <param name="leaseSeconds">Lease duration. Work held longer than this lapses and is re-charged.</param>
  /// <param name="ceiling">Hard upper bound on outstanding rows.</param>
  /// <param name="floor">Lower bound, retained even when stalled so the loop can recover.</param>
  /// <param name="safetyFactor">Fraction of the lease window to plan against. Below 1.0 leaves headroom.</param>
  /// <param name="smoothing">Exponential smoothing weight for the drain-rate estimate (0..1].</param>
  public AdaptiveOutstandingBudget(
    int leaseSeconds,
    int ceiling,
    int floor = 100,
    double safetyFactor = 0.5,
    double smoothing = 0.2
  ) {
    ArgumentOutOfRangeException.ThrowIfLessThan(leaseSeconds, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(ceiling, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(floor, 1);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(safetyFactor);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(smoothing);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(smoothing, 1.0);

    _leaseSeconds = leaseSeconds;
    _ceiling = ceiling;
    // A floor above the ceiling would make the budget meaningless; clamp rather than throw so a
    // careless configuration degrades to "fixed size" instead of refusing to start.
    _floor = Math.Min(floor, ceiling);
    _safetyFactor = safetyFactor;
    _smoothing = smoothing;
    _current = _floor;
  }

  /// <summary>The current outstanding-row budget.</summary>
  public int Current => _current;

  /// <summary>The smoothed drain rate, in rows per second.</summary>
  public double DrainRatePerSecond => _drainRatePerSecond;

  /// <summary>Feeds a completion sample and recomputes the budget.</summary>
  /// <param name="completed">Rows that finished processing during the sample.</param>
  /// <param name="elapsed">Wall time the sample covers.</param>
  /// <remarks>
  /// Time is supplied by the caller rather than read from a clock, so the control loop is
  /// deterministic and testable without sleeping.
  /// </remarks>
  public void Observe(int completed, TimeSpan elapsed) {
    if (elapsed <= TimeSpan.Zero) {
      return;
    }

    var sample = completed / elapsed.TotalSeconds;
    // Smoothing is the ramp: from a standing start the estimate approaches the true rate over
    // several samples, so one good reading cannot jump the budget to full capacity and overshoot
    // straight back into the failure this exists to prevent.
    _drainRatePerSecond = (_smoothing * sample) + ((1.0 - _smoothing) * _drainRatePerSecond);
    _hasSample = true;

    var target = _drainRatePerSecond * _leaseSeconds * _safetyFactor;
    _current = (int)Math.Clamp(target, _floor, _ceiling);
  }

  /// <summary>How many further rows may be claimed right now. Zero means claim nothing.</summary>
  /// <param name="outstanding">Rows currently claimed but not yet processed.</param>
  public int Headroom(int outstanding) {
    // Stalled: work is held but nothing is completing. Claiming more cannot help a stuck handler
    // and only burns attempts on rows that will never be reached, so take nothing at all. Recovery
    // comes from work completing, or from the caller ageing out in-flight entries whose leases have
    // already lapsed — either of which lowers `outstanding` and reopens the gate.
    //
    // The `_hasSample` guard matters: a zero rate that has never been MEASURED means "unknown", not
    // "stuck". Without it, a worker that starts with any outstanding work refuses to claim, so it
    // never observes the completion that would prove it is healthy — a deadlock produced purely by
    // conflating no-data-yet with no-progress.
    if (_hasSample && outstanding > 0 && _drainRatePerSecond <= 0.0) {
      return 0;
    }

    // Nothing outstanding and nothing completing is an EMPTY queue, not a stalled one — the loop
    // must still be free to look for work, or an idle service would never pick up again.
    return Math.Max(0, _current - outstanding);
  }
}
