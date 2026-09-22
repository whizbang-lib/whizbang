namespace Whizbang.Core.Workers;

/// <summary>
/// Measures re-claim churn from a claim batch, whichever shape the batch arrived in.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AdaptiveClaimWindow"/> narrows the claim batch when work returns that this instance
/// already claimed and did not finish — rows carrying more than one attempt. That count is the only
/// evidence the window has that the batch outruns throughput, and the entire AIMD response depends
/// on it.
/// </para>
/// <para>
/// A claim batch can express inbox work two ways. One carries materialized rows. The other carries
/// stream ids only, with rows fetched separately — the path taken when stream parallelism is
/// enabled. Counting attempts from the materialized list alone therefore reports zero churn on the
/// stream-id path regardless of how badly the instance is thrashing.
/// </para>
/// <para>
/// Observed consequence: services holding thousands of live leases, with rows at attempts fifteen
/// and twenty-one, logged not one window resize. The control was not overwhelmed, it was blind, and
/// batch sizing reduced to whether a service happened to start in a workable state. Unlucky ones
/// dead-lettered six figures of messages that never reached a receptor.
/// </para>
/// <para>
/// The distinction this type preserves is between "no churn" and "churn not measured". They are the
/// same number and opposite facts. A control fed a hard-coded reassurance is worse than one that is
/// absent: it reports healthy while the condition it exists to catch runs unchecked. When work was
/// claimed but no attempt counts were read, the measurement says so rather than reporting zero.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ClaimChurnSignalTests.cs</tests>
public static class ClaimChurnSignal {

  /// <summary>One cycle's churn evidence.</summary>
  /// <param name="ClaimedItems">Units of work claimed, across both representations.</param>
  /// <param name="Reclaimed">
  /// Units carrying more than one attempt — already claimed once and not completed.
  /// </param>
  /// <param name="IsMeasurable">
  /// False when work was claimed but no attempt counts were available to read. Callers must not
  /// treat this as a clean cycle; it is an absence of evidence, and feeding it to a governor as
  /// zero churn is the defect this type exists to prevent.
  /// </param>
  public readonly record struct Measurement(int ClaimedItems, int Reclaimed, bool IsMeasurable);

  /// <summary>
  /// Measures churn across both claim representations.
  /// </summary>
  /// <param name="materializedAttempts">Attempt counts of rows carried directly in the batch.</param>
  /// <param name="streamIdCount">Streams claimed by id, whose rows are fetched separately.</param>
  /// <param name="fetchedAttempts">
  /// Attempt counts read back for the stream-id claims, or null when they were not read.
  /// </param>
  /// <returns>The combined measurement, flagged unmeasurable when evidence is missing.</returns>
  public static Measurement Measure(
      IReadOnlyList<int> materializedAttempts,
      int streamIdCount,
      IReadOnlyList<int>? fetchedAttempts) {
    ArgumentNullException.ThrowIfNull(materializedAttempts);

    var reclaimed = 0;
    var claimed = 0;

    for (var i = 0; i < materializedAttempts.Count; i++) {
      claimed++;
      // Attempt 1 is the first delivery. Only a SECOND attempt means this instance held the row
      // once and did not finish it; counting >= 1 would read every healthy row as churn and pin
      // the window at its floor.
      if (materializedAttempts[i] > 1) {
        reclaimed++;
      }
    }

    if (fetchedAttempts is not null) {
      for (var i = 0; i < fetchedAttempts.Count; i++) {
        claimed++;
        if (fetchedAttempts[i] > 1) {
          reclaimed++;
        }
      }
      return new Measurement(claimed, reclaimed, IsMeasurable: true);
    }

    // Streams were claimed but nothing was read back. Report the work as claimed — so the window
    // does not also conclude the instance is idle and grow on top of an unmeasured thrash — and
    // mark the churn unmeasured rather than zero.
    if (streamIdCount > 0) {
      return new Measurement(claimed + streamIdCount, reclaimed, IsMeasurable: false);
    }

    // Nothing claimed at all: a real, clean observation. An idle service must stay able to grow its
    // window back, or a quiet period would pin it at the floor for good.
    return new Measurement(claimed, reclaimed, IsMeasurable: true);
  }
}
