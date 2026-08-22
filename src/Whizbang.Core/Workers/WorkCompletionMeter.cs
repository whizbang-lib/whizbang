namespace Whizbang.Core.Workers;

/// <summary>
/// Counts rows that finished processing, so a claim loop can size its outstanding budget from a
/// <b>measured</b> drain rate rather than an inferred one.
/// </summary>
/// <remarks>
/// <para>
/// The obvious alternative — infer drain from the fall in outstanding work between polls — is both
/// inaccurate and untestable. Rows arriving inside the same interval mask completions, so the rate
/// reads low; and any test of the control loop would then depend on wall-clock timing rather than on
/// an observable event.
/// </para>
/// <para>
/// <b>Deliberately advisory.</b> This only sizes the budget. The authoritative outstanding figure
/// comes from the store, which re-derives it on every poll. If this meter stalls, is never fed, or
/// loses counts, the drain rate reads low, the budget falls toward its floor, and the claim loop
/// keeps polling — degraded throughput, never a stuck worker.
/// </para>
/// <para>
/// That distinction is the whole point. In-memory state that <i>gates</i> claiming has already
/// proved unrecoverable in this codebase: a stranded flag made the claim loop discard work
/// indefinitely until the process was restarted. In-memory state that only <i>tunes</i> a bound
/// cannot do that, because the bound has a floor and the poll never stops.
/// </para>
/// <para>AOT-safe: interlocked arithmetic over a long, no reflection.</para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCompletionMeterTests.cs</tests>
public sealed class WorkCompletionMeter {
  private long _completed;

  /// <summary>Records that <paramref name="count"/> rows finished processing.</summary>
  /// <remarks>
  /// "Finished" means the row stopped being this instance's responsibility for this attempt —
  /// success and failure alike. Both free capacity; counting only success would understate drain on
  /// a service that is working hard on failing messages.
  /// </remarks>
  public void Record(int count = 1) {
    if (count <= 0) {
      return;
    }
    Interlocked.Add(ref _completed, count);
  }

  /// <summary>Reads the count for this interval and clears it.</summary>
  /// <remarks>
  /// Read-and-clear rather than read: each sample covers one interval, and leaving the count in
  /// place would carry old completions forward and inflate the measured rate without bound.
  /// </remarks>
  public long ReadAndReset() => Interlocked.Exchange(ref _completed, 0);
}
