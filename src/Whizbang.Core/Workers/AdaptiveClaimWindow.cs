namespace Whizbang.Core.Workers;

/// <summary>
/// Sizes the claim batch from observed churn, so a worker stops claiming more work than it can
/// dispatch inside the lease window.
/// </summary>
/// <remarks>
/// <para>
/// A claim charges an attempt per row. Rows a worker claims but never reaches sit until their lease
/// lapses, get re-claimed at another attempt, and after the cap are dead-lettered as
/// <c>MaxAttemptsExceeded</c> having never reached a receptor. A fixed batch size cannot avoid that,
/// because the right size depends on handler latency, lease duration and how work fans across
/// streams — none of which are known up front.
/// </para>
/// <para>
/// The feedback signal is re-claims. A row arriving with <c>attempts &gt; 1</c> is work this instance
/// (or a peer) already claimed and did not finish, so a high re-claim share means the batch is
/// larger than throughput. That is observable at claim time, needs no coordination with the dispatch
/// path, and is exactly the quantity the fix aims to drive to zero.
/// </para>
/// <para>
/// The response is AIMD — halve on churn, creep back up when clean. Multiplicative decrease sheds an
/// overload quickly; additive increase reclaims throughput slowly enough not to re-enter it. A
/// symmetric rule would oscillate.
/// </para>
/// <para>
/// This bounds STREAMS, not rows, because the pump is stream-oriented: the poller returns stream ids
/// and a per-stream drainer processes each in order. Bounding rows alone would still let a worker
/// hold leases across thousands of streams, which is the shape of the original failure.
/// </para>
/// </remarks>
/// <remarks>
/// Bounds the size of an individual claim. It does <b>not</b> bound how much work the instance holds
/// in total — a loop that claims and immediately claims again accumulates outstanding work across
/// cycles at any batch size. <see cref="AdaptiveOutstandingBudget"/> is the control for that, and the
/// two are complementary rather than alternatives. See <c>operations/workers/claim-backpressure</c>.
/// </remarks>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/AdaptiveClaimWindowTests.cs</tests>
public sealed class AdaptiveClaimWindow {
  private readonly int _ceiling;
  private readonly int _floor;
  private readonly int _additiveStep;
  private readonly double _churnThreshold;
  private int _current;

  /// <summary>Creates a window that starts wide open and narrows only if churn appears.</summary>
  /// <param name="ceiling">Upper bound — the configured batch size.</param>
  /// <param name="floor">Lower bound. The window never shrinks below this, so a worker always makes progress.</param>
  /// <param name="additiveStep">Streams added per clean cycle.</param>
  /// <param name="churnThreshold">Re-claim share (0..1) above which the window halves.</param>
  public AdaptiveClaimWindow(int ceiling, int floor = 25, int additiveStep = 25, double churnThreshold = 0.5) {
    ArgumentOutOfRangeException.ThrowIfLessThan(ceiling, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(floor, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(additiveStep, 1);
    _ceiling = ceiling;
    // A floor above the ceiling would make the window meaningless; clamp rather than throw so a
    // careless configuration degrades to "fixed size" instead of refusing to start.
    _floor = Math.Min(floor, ceiling);
    _additiveStep = additiveStep;
    _churnThreshold = churnThreshold;
    // Start at the FLOOR, not the ceiling.
    //
    // This previously started wide, reasoning that an unloaded service should not pay a warm-up
    // penalty. Production disproved it: cold start is the most dangerous moment, not the safest.
    // A process that restarts carrying a large backlog has no churn history yet, so a
    // ceiling-width first claim grabs the maximum before any feedback exists to shrink it — and a
    // restart-with-backlog is exactly the situation that produces one.
    //
    // The warm-up cost is small and self-correcting (a genuinely idle service ramps back to the
    // ceiling within a few clean cycles); the overshoot cost is leases held on work that cannot be
    // drained inside the lease window, which converts a backlog into spent retry budget.
    _current = _floor;
  }

  /// <summary>Streams to request on the next claim.</summary>
  public int Current => _current;

  /// <summary>
  /// Folds one cycle's outcome into the window.
  /// </summary>
  /// <param name="claimedRows">Rows returned by the claim.</param>
  /// <param name="reclaimedRows">Of those, how many arrived with <c>attempts &gt; 1</c>.</param>
  /// <param name="drainMeasured">
  /// Whether the outstanding budget has actually MEASURED drain yet. Growth is gated on it;
  /// shrinking never is.
  /// </param>
  public void Observe(int claimedRows, int reclaimedRows, bool drainMeasured = true) {
    // An empty claim says nothing about capacity — the queue was simply empty. Treating it as a
    // clean cycle would inflate the window during idle periods and guarantee an overshoot the
    // moment work arrived.
    if (claimedRows <= 0) {
      return;
    }

    var churn = (double)reclaimedRows / claimedRows;

    if (churn > _churnThreshold) {
      _current = Math.Max(_floor, _current / 2);
      return;
    }

    // Only a completely clean cycle earns growth. Creeping up while any churn persists is how a
    // control loop settles into permanent low-grade overload.
    //
    // Growth is ALSO gated on drain having been measured at all. At cold start the outstanding
    // budget sits at its floor because it has no history — but this window ramps from its own
    // feedback, so during that blind period two adaptive controls ramp independently while only one
    // of them measures anything. A restart onto a large backlog then commits to more than it can
    // drain inside one lease, and the excess lapses and spends a retry attempt it never used.
    //
    // Note the asymmetry, which is deliberate: SHRINKING above is never gated. Backing off is always
    // safe, and a guard that blocked it would deepen the very over-commit it exists to prevent.
    if (reclaimedRows == 0 && drainMeasured) {
      _current = Math.Min(_ceiling, _current + _additiveStep);
    }
  }
}
