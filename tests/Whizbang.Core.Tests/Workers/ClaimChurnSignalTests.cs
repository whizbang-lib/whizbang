using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The adaptive claim window's churn signal must survive the stream-id claim path.
/// </summary>
/// <remarks>
/// <para>
/// <c>AdaptiveClaimWindow</c> shrinks the claim batch when work arrives that this instance already
/// claimed and failed to finish — rows carrying more than one attempt. That re-claim count is the
/// only input telling it the batch is larger than throughput, and the whole AIMD response hangs off
/// it.
/// </para>
/// <para>
/// A <c>WorkBatch</c> can express claimed inbox work two ways. <c>InboxWork</c> carries materialized
/// rows. <c>InboxStreamIds</c> carries stream ids only, with the rows fetched separately — the
/// work-pump path taken when stream parallelism is enabled. The churn count was computed by
/// iterating <c>InboxWork</c> alone, so on the stream-id path it reads zero no matter how badly the
/// instance is thrashing.
/// </para>
/// <para>
/// Observed consequence: services holding roughly nine thousand live leases with rows at attempt
/// fifteen and twenty-one — churn at maximum intensity — logged not one window resize, while
/// services that happened to land on a small window stayed healthy. The control was not overwhelmed;
/// it was blind. Sizing reduces to luck, and an unlucky service dead-letters six figures of messages
/// that never reached a receptor.
/// </para>
/// <para>
/// Zero re-claims and zero-because-unmeasured must never be the same value. A control fed a
/// hard-coded "everything is fine" is worse than an absent one: it reports healthy while the
/// condition it exists to catch runs unchecked.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/ClaimChurnSignal.cs</code-under-test>
[Category("Workers")]
public class ClaimChurnSignalTests {

  [Test]
  public async Task CountsReclaimsFromMaterializedRowsAsync() {
    var measurement = ClaimChurnSignal.Measure(
      materializedAttempts: [1, 1, 4, 1, 9],
      streamIdCount: 0,
      fetchedAttempts: null);

    await Assert.That(measurement.Reclaimed).IsEqualTo(2);
    await Assert.That(measurement.ClaimedItems).IsEqualTo(5);
    await Assert.That(measurement.IsMeasurable).IsTrue();
  }

  [Test]
  public async Task CountsReclaimsOnTheStreamIdPathAsync() {
    // Rows arrived as stream ids; their attempts were read when the rows were fetched.
    var measurement = ClaimChurnSignal.Measure(
      materializedAttempts: [],
      streamIdCount: 3,
      fetchedAttempts: [1, 15, 21, 1, 7]);

    await Assert.That(measurement.Reclaimed).IsEqualTo(3)
      .Because("attempts 15, 21 and 7 are rows this instance already claimed and failed to finish — "
             + "the single strongest signal that the batch exceeds throughput, and the input the "
             + "window halves on");
    await Assert.That(measurement.ClaimedItems).IsEqualTo(5);
    await Assert.That(measurement.IsMeasurable).IsTrue();
  }

  [Test]
  public async Task StreamIdWorkWithNoFetchedAttemptsIsUnmeasurableNotCleanAsync() {
    // The exact production shape: stream ids claimed, materialized list empty, nothing read back.
    var measurement = ClaimChurnSignal.Measure(
      materializedAttempts: [],
      streamIdCount: 240,
      fetchedAttempts: null);

    await Assert.That(measurement.IsMeasurable).IsFalse()
      .Because("work WAS claimed — 240 streams of it — so reporting zero churn asserts a fact "
             + "nobody established; that is how a window stays wide while rows climb to attempt 21");
    await Assert.That(measurement.Reclaimed).IsEqualTo(0);
    await Assert.That(measurement.ClaimedItems).IsGreaterThan(0)
      .Because("claimed-count must reflect that streams were taken, or the window also believes "
             + "the instance is idle and grows on top of an unmeasured thrash");
  }

  [Test]
  public async Task AnEmptyClaimIsMeasurableAndCleanAsync() {
    var measurement = ClaimChurnSignal.Measure(
      materializedAttempts: [],
      streamIdCount: 0,
      fetchedAttempts: null);

    await Assert.That(measurement.IsMeasurable).IsTrue()
      .Because("claiming nothing is a real observation — an idle service must still be able to "
             + "grow its window back up, or a quiet period pins it at the floor permanently");
    await Assert.That(measurement.Reclaimed).IsEqualTo(0);
    await Assert.That(measurement.ClaimedItems).IsEqualTo(0);
  }

  [Test]
  public async Task BothRepresentationsCombineRatherThanOverrideAsync() {
    var measurement = ClaimChurnSignal.Measure(
      materializedAttempts: [1, 6],
      streamIdCount: 2,
      fetchedAttempts: [1, 3]);

    await Assert.That(measurement.Reclaimed).IsEqualTo(2)
      .Because("a batch can carry both shapes at once; counting only one silently discards half "
             + "the evidence and biases the window toward growing");
    await Assert.That(measurement.ClaimedItems).IsEqualTo(4);
  }

  [Test]
  public async Task AttemptOfOneIsAFirstDeliveryNotChurnAsync() {
    var measurement = ClaimChurnSignal.Measure(
      materializedAttempts: [1, 1, 1, 1],
      streamIdCount: 0,
      fetchedAttempts: null);

    await Assert.That(measurement.Reclaimed).IsEqualTo(0)
      .Because("every row is charged one attempt on its first claim, so treating attempts >= 1 as "
             + "churn would collapse the window to its floor on a perfectly healthy service");
  }
}
