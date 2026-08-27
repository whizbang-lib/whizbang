namespace Whizbang.Core.Workers;

/// <summary>One stream's outstanding work, as far as the caller can see it.</summary>
/// <param name="StreamId">The stream.</param>
/// <param name="KnownDepth">Rows known to be queued. Zero means nothing to do.</param>
public readonly record struct StreamDemand(Guid StreamId, int KnownDepth);

/// <summary>
/// Divides one global row budget across streams, guarding starvation from both directions.
/// </summary>
/// <remarks>
/// <para>
/// Throughput is a property of TOTAL rows moved, so the budget is denominated globally and then
/// divided. A per-stream cap fixes the wrong quantity: the total then swings with however many
/// streams happen to be active, and no single value suits both a thousand one-row streams and one
/// stream holding thousands.
/// </para>
/// <para>
/// Dividing a global budget invites starvation from two opposite directions, and guarding only one
/// relocates the problem rather than solving it:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Breadth starvation</b> — one deep stream absorbs the budget and unrelated work queues behind a
/// single large aggregate that happens to be mid-drain. The service looks wedged while it is busy.
/// </description></item>
/// <item><description>
/// <b>Depth starvation</b> — the budget is spread evenly, every stream creeps forward a few rows per
/// cycle, and a stream holding thousands never finishes. Even division looks the fairest and is the
/// worst outcome for the work that most needs to complete.
/// </description></item>
/// </list>
/// <para>
/// So a floor per admitted stream buys breadth, the remainder is weighted by residual depth to buy
/// completion, and when the floor alone cannot cover every stream the admitted SET rotates. Serving
/// a subset in one cycle is correct; serving the SAME subset every cycle is permanent starvation
/// that looks identical to healthy throughput in every aggregate metric.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/StreamFairShareAllocatorTests.cs</tests>
public sealed class StreamFairShareAllocator {

  /// <summary>Tuning for <see cref="StreamFairShareAllocator"/>.</summary>
  public sealed class Settings {
    /// <summary>
    /// Rows guaranteed to each admitted stream (default 10). This is the breadth guarantee: a
    /// stream admitted for less than a useful amount has been admitted in name only.
    /// </summary>
    public int MinRowsPerStream { get; set; } = 10;

    /// <summary>
    /// Ceiling on any single stream's allocation. Zero (default) means unbounded — depth is
    /// limited only by the global budget. A wider page holds its lease longer, so a deployment
    /// whose lease window is tight bounds it here.
    /// </summary>
    public int MaxRowsPerStream { get; set; }
  }

  /// <summary>Rows granted to one stream for this cycle.</summary>
  /// <param name="StreamId">The stream.</param>
  /// <param name="Rows">Rows to fetch.</param>
  public readonly record struct Allocation(Guid StreamId, int Rows);

  private readonly Settings _settings;
  private int _cursor;

  /// <summary>Initializes a new instance of the <see cref="StreamFairShareAllocator"/> class.</summary>
  /// <param name="settings">Tuning; defaults are production-safe.</param>
  public StreamFairShareAllocator(Settings settings) {
    ArgumentNullException.ThrowIfNull(settings);
    _settings = settings;
  }

  /// <summary>Divides <paramref name="totalBudget"/> rows across <paramref name="demands"/>.</summary>
  /// <param name="totalBudget">Rows this cycle may fetch in total, across all streams.</param>
  /// <param name="demands">Streams and their known depth.</param>
  /// <returns>Per-stream allocations; streams granted nothing are omitted.</returns>
  public IReadOnlyList<Allocation> Allocate(int totalBudget, IReadOnlyList<StreamDemand> demands) {
    ArgumentNullException.ThrowIfNull(demands);
    if (totalBudget <= 0 || demands.Count == 0) {
      return [];
    }

    // Spending a floor on an empty stream takes it from one that has work, and on an idle service
    // empty streams are the common case.
    var eligible = new List<StreamDemand>(demands.Count);
    for (var i = 0; i < demands.Count; i++) {
      if (demands[i].KnownDepth > 0) {
        eligible.Add(demands[i]);
      }
    }
    if (eligible.Count == 0) {
      return [];
    }

    var ceiling = _settings.MaxRowsPerStream > 0 ? _settings.MaxRowsPerStream : int.MaxValue;
    var floor = Math.Max(1, _settings.MinRowsPerStream);
    var remaining = totalBudget;
    var granted = new List<Allocation>(eligible.Count);
    var residual = new List<int>(eligible.Count);

    // Pass 1 — breadth. Walk from the rotating cursor so the admitted SET moves between cycles
    // when the budget cannot seat everyone.
    var start = _cursor % eligible.Count;
    var admitted = 0;
    for (var n = 0; n < eligible.Count && remaining > 0; n++) {
      var d = eligible[(start + n) % eligible.Count];
      // Never more than the stream holds: handing a 3-row stream the full floor wastes budget a
      // deep stream could have used. The floor is a guarantee, not an allotment to burn.
      var want = Math.Min(Math.Min(floor, d.KnownDepth), ceiling);
      if (want > remaining) {
        // Slicing below the floor is how every stream advances and none completes. Stop admitting
        // rather than seat someone for a useless amount.
        break;
      }
      granted.Add(new Allocation(d.StreamId, want));
      residual.Add(Math.Min(d.KnownDepth, ceiling) - want);
      remaining -= want;
      admitted++;
    }

    if (admitted == 0) {
      return [];
    }

    // Advance past the streams served, so the next cycle starts with whoever was skipped.
    _cursor = (start + admitted) % eligible.Count;

    // Pass 2 — depth. Weight the remainder by what each admitted stream still holds, so a deep
    // stream actually completes instead of creeping forward one floor per cycle.
    if (remaining > 0) {
      long residualTotal = 0;
      for (var i = 0; i < residual.Count; i++) {
        residualTotal += residual[i];
      }

      if (residualTotal > 0) {
        for (var i = 0; i < granted.Count && remaining > 0; i++) {
          if (residual[i] <= 0) {
            continue;
          }
          var share = (int)Math.Min(residual[i], (long)remaining * residual[i] / residualTotal);
          if (share <= 0) {
            continue;
          }
          granted[i] = granted[i] with { Rows = granted[i].Rows + share };
          residual[i] -= share;
          remaining -= share;
        }

        // Integer division leaves a tail. Hand it to whoever still has depth, largest first, so it
        // lands where it shortens a queue rather than being dropped.
        while (remaining > 0) {
          var best = -1;
          for (var i = 0; i < residual.Count; i++) {
            if (residual[i] > 0 && (best < 0 || residual[i] > residual[best])) {
              best = i;
            }
          }
          if (best < 0) {
            break;
          }
          var take = Math.Min(residual[best], remaining);
          granted[best] = granted[best] with { Rows = granted[best].Rows + take };
          residual[best] -= take;
          remaining -= take;
        }
      }
    }

    return granted;
  }
}
