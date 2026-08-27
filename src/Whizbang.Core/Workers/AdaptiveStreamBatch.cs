namespace Whizbang.Core.Workers;

/// <summary>
/// Sizes the per-stream fetch page from observed stream DEPTH, rather than fixing it for one
/// workload shape.
/// </summary>
/// <remarks>
/// <para>
/// The batched-fetch drain amortizes per-call setup (parse, plan, window-sort) across many streams,
/// which is the right trade when each stream holds a row or two. A fixed cap turns pathological
/// when the workload inverts: a stream holding thousands of rows is drained one capped page at a
/// time, by a single drainer task, each page its own round-trip. Effective parallelism collapses to
/// the stream COUNT, so additional replicas sit idle while one instance walks a deep stream
/// serially — visible as a worker pinned well below its CPU limit with a large backlog behind it.
/// </para>
/// <para>
/// Depth needs no new plumbing to measure. A fetch that comes back full is evidence the stream held
/// at least a page, so saturation earns growth: deep streams converge on fewer, larger round-trips
/// while shallow ones stay at the floor and keep the amortization the fixed cap was chosen for.
/// Neither shape has to be configured, and a deployment carrying both gets both.
/// </para>
/// <para>
/// Harm is measured with the signal the other controls already use — rows arriving with more than
/// one attempt. A wider page holds its lease longer, so width the drain cannot cash inside the
/// lease window surfaces as re-claims, and the cap backs off for exactly the reason the claim
/// window does.
/// </para>
/// <para>
/// The asymmetries here are deliberate and match <see cref="AdaptiveClaimWindow"/>: start at the
/// FLOOR because a restart onto a deep backlog has no feedback yet; gate GROWTH on drain having
/// been measured; never gate SHRINKING, because backing off is always safe.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/AdaptiveStreamBatchTests.cs</tests>
public sealed class AdaptiveStreamBatch {
  private readonly int _ceiling;
  private readonly int _floor;
  private readonly int _additiveStep;
  private readonly double _churnThreshold;
  private int _current;

  /// <summary>Initializes a new instance of the <see cref="AdaptiveStreamBatch"/> class.</summary>
  /// <param name="ceiling">Widest page to fetch for one stream.</param>
  /// <param name="floor">Narrowest page; must still make forward progress.</param>
  /// <param name="additiveStep">Rows added per clean, saturated cycle.</param>
  /// <param name="churnThreshold">Re-claim ratio above which the page halves.</param>
  public AdaptiveStreamBatch(
      int ceiling, int floor = 100, int additiveStep = 100, double churnThreshold = 0.5) {
    ArgumentOutOfRangeException.ThrowIfLessThan(ceiling, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(floor, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(additiveStep, 1);
    _ceiling = ceiling;
    // A floor above the ceiling would make the page meaningless; clamp rather than throw so a
    // careless configuration degrades to "fixed size" instead of refusing to start.
    _floor = Math.Min(floor, ceiling);
    _additiveStep = additiveStep;
    _churnThreshold = churnThreshold;
    _current = _floor;
  }

  /// <summary>Rows to request per stream on the next fetch.</summary>
  public int Current => _current;

  /// <summary>Folds one stream's fetch outcome into the page size.</summary>
  /// <param name="rowsReturned">Rows the fetch actually returned for this stream.</param>
  /// <param name="capRequested">The cap that fetch was issued with.</param>
  /// <param name="reclaimedRows">Of those rows, how many arrived with <c>attempts &gt; 1</c>.</param>
  /// <param name="drainMeasured">
  /// Whether drain has actually been measured yet. Growth is gated on it; shrinking never is.
  /// </param>
  public void Observe(
      int rowsReturned, int capRequested, int reclaimedRows, bool drainMeasured = true) {
    // An empty fetch is not a clean cycle, it is no information. Folding it in either direction
    // would make the page track idleness instead of depth.
    if (rowsReturned <= 0) {
      return;
    }

    var churn = (double)reclaimedRows / rowsReturned;
    if (churn > _churnThreshold) {
      _current = Math.Max(_floor, _current / 2);
      return;
    }

    // Saturation is the only available evidence that the stream is DEEP. Growing on an unsaturated
    // fetch would widen the page for streams that never fill it — pure lease cost, no benefit, and
    // it would erode the amortization the batched fetch exists for.
    var saturated = capRequested > 0 && rowsReturned >= capRequested;

    // Only a completely clean cycle earns growth. Creeping up while any churn persists is how a
    // control loop settles into permanent low-grade overload.
    if (saturated && reclaimedRows == 0 && drainMeasured) {
      _current = Math.Min(_ceiling, _current + _additiveStep);
    }
  }
}
