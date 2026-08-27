using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The seam that carries re-claim churn from the worker that FETCHES rows to the one that SIZES
/// the claim.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AdaptiveClaimWindow"/> narrows on re-claim churn, but on the stream-id path the claim
/// returns stream ids and never sees a row. Only the drain worker, which fetches the rows, knows
/// their attempt counts. Without this seam the window observed zero churn forever and, because
/// <c>Observe</c> short-circuits on a zero claimed-count, never adapted for the life of the
/// process — a deployment using stream parallelism logged not one window resize while rows in the
/// same inboxes reached attempt twenty-one.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/ClaimChurnFeedback.cs</code-under-test>
[Category("Workers")]
public class ClaimChurnFeedbackTests {

  private static readonly int[] _fresh = [1, 1, 1];
  private static readonly int[] _mixed = [1, 4, 1, 9];
  private static readonly int[] _allChurn = [7, 12, 21];

  [Test]
  public async Task ReportsFreshRowsAsObservedButNotChurnedAsync() {
    var f = new ClaimChurnFeedback();
    f.Report(_fresh);

    var (observed, reclaimed) = f.Take();
    await Assert.That(observed).IsEqualTo(3);
    await Assert.That(reclaimed).IsEqualTo(0)
      .Because("attempt 1 is a first delivery; counting it as churn would pin a healthy service's "
             + "window at its floor");
  }

  [Test]
  public async Task CountsOnlySecondAndLaterAttemptsAsChurnAsync() {
    var f = new ClaimChurnFeedback();
    f.Report(_mixed);

    var (observed, reclaimed) = f.Take();
    await Assert.That(observed).IsEqualTo(4);
    await Assert.That(reclaimed).IsEqualTo(2)
      .Because("attempts 4 and 9 are rows this instance already held and did not finish — exactly "
             + "the evidence the window halves on");
  }

  [Test]
  public async Task AccumulatesAcrossReportsWithinACycleAsync() {
    var f = new ClaimChurnFeedback();
    f.Report(_fresh);
    f.Report(_allChurn);

    var (observed, reclaimed) = f.Take();
    await Assert.That(observed).IsEqualTo(6);
    await Assert.That(reclaimed).IsEqualTo(3)
      .Because("many streams are drained per claim cycle, so the window needs the CYCLE's total "
             + "rather than whichever stream reported last");
  }

  [Test]
  public async Task TakeIsDestructiveSoStaleChurnCannotHoldTheWindowNarrowAsync() {
    var f = new ClaimChurnFeedback();
    f.Report(_allChurn);

    var first = f.Take();
    var second = f.Take();

    await Assert.That(first.Reclaimed).IsEqualTo(3);
    await Assert.That(second.Observed).IsEqualTo(0)
      .Because("leaving it cumulative would let one early burst of churn hold the window narrow "
             + "long after the condition cleared");
    await Assert.That(second.Reclaimed).IsEqualTo(0);
  }

  [Test]
  public async Task ZeroObservedMeansUNMEASUREDNotCleanAsync() {
    var f = new ClaimChurnFeedback();

    var (observed, reclaimed) = f.Take();

    await Assert.That(observed).IsEqualTo(0);
    await Assert.That(reclaimed).IsEqualTo(0)
      .Because("a cycle that gathered no evidence must be distinguishable from one that saw clean "
             + "rows — reporting zero-observed as a clean cycle is how a control grows on top of an "
             + "unobserved thrash");
  }

  [Test]
  public async Task AnEmptyReportDoesNotRegisterACycleAsync() {
    var f = new ClaimChurnFeedback();
    f.Report([]);

    var (observed, _) = f.Take();
    await Assert.That(observed).IsEqualTo(0)
      .Because("a fetch that returned nothing is not evidence the queue is clean; it is no evidence "
             + "at all");
  }

  [Test]
  public async Task ConcurrentReportsAreNotLostAsync() {
    var f = new ClaimChurnFeedback();
    var rows = new int[] { 2 };

    await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => f.Report(rows))));

    var (observed, reclaimed) = f.Take();
    await Assert.That(observed).IsEqualTo(200)
      .Because("streams drain concurrently at up to the governor's width, so every reporter races "
             + "the others — a lost update understates churn and biases the window toward growing");
    await Assert.That(reclaimed).IsEqualTo(200);
  }

  [Test]
  public async Task RejectsNullRatherThanSilentlyReportingNothingAsync() {
    var f = new ClaimChurnFeedback();
    await Assert.That(() => f.Report(null!)).Throws<ArgumentNullException>()
      .Because("silently reporting nothing is indistinguishable from a clean cycle, which is the "
             + "one wrong answer that leaves no evidence it was ever asked");
  }
}
