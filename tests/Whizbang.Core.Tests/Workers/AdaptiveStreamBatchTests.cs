using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The per-stream fetch cap must adapt to stream DEPTH instead of being fixed for one shape.
/// </summary>
/// <remarks>
/// <para>
/// The batched-fetch drain amortizes per-call setup across many streams, which is the right trade
/// when each stream holds a row or two. A fixed cap turns pathological when the workload inverts:
/// a stream holding thousands of rows is drained one capped page at a time, by a single drainer
/// task, each page a separate round-trip. Effective parallelism becomes the stream COUNT, so extra
/// replicas idle while one instance walks a deep stream serially.
/// </para>
/// <para>
/// Depth is measurable without new plumbing: a fetch that comes back full is evidence the stream
/// held at least that much. Saturation earns growth, so deep streams converge on fewer, larger
/// round-trips while shallow ones stay at the floor and keep the amortization the fixed cap was
/// chosen for. Neither shape has to be configured.
/// </para>
/// <para>
/// Harm is measured with the signal the other controls already use: rows arriving with more than
/// one attempt. A larger page holds a lease longer, so if the extra width cannot be drained inside
/// the lease window it shows up as re-claims, and the cap must back off for the same reason the
/// claim window does.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/AdaptiveStreamBatch.cs</code-under-test>
[Category("Workers")]
public class AdaptiveStreamBatchTests {

  [Test]
  public async Task ItStartsAtTheFloorNotTheCeilingAsync() {
    var batch = new AdaptiveStreamBatch(ceiling: 1000, floor: 100);

    await Assert.That(batch.Current).IsEqualTo(100)
      .Because("a process that restarts carrying a deep backlog has no feedback yet; a "
             + "ceiling-width first fetch takes the widest possible lease before anything can "
             + "shrink it, and restart-with-backlog is exactly what produces one");
  }

  [Test]
  public async Task ADeepStreamEarnsAWiderPageAsync() {
    var batch = new AdaptiveStreamBatch(ceiling: 1000, floor: 100, additiveStep: 100);

    // The fetch came back full: the stream held at least a page, so there is more behind it.
    batch.Observe(rowsReturned: 100, capRequested: 100, reclaimedRows: 0);

    await Assert.That(batch.Current).IsEqualTo(200)
      .Because("a saturated fetch is the only evidence available that the stream is deep, and "
             + "widening is what converts N serial round-trips into fewer");
  }

  [Test]
  public async Task AShallowStreamStaysAtTheFloorAsync() {
    var batch = new AdaptiveStreamBatch(ceiling: 1000, floor: 100, additiveStep: 100);

    for (var i = 0; i < 20; i++) {
      batch.Observe(rowsReturned: 2, capRequested: 100, reclaimedRows: 0);
    }

    await Assert.That(batch.Current).IsEqualTo(100)
      .Because("streams holding a row or two are the shape the batched fetch was tuned for — "
             + "widening the page buys nothing there and would only lengthen the lease");
  }

  [Test]
  public async Task ChurnShrinksThePageAsync() {
    var batch = new AdaptiveStreamBatch(ceiling: 1000, floor: 100, additiveStep: 100);
    for (var i = 0; i < 5; i++) {
      batch.Observe(rowsReturned: 100, capRequested: 100, reclaimedRows: 0);
    }
    var grown = batch.Current;

    batch.Observe(rowsReturned: 600, capRequested: 600, reclaimedRows: 400);

    await Assert.That(batch.Current).IsLessThan(grown)
      .Because("re-claims mean the page could not be drained inside its lease — the width is "
             + "writing cheques the drain cannot cash, exactly as it would for the claim window");
  }

  [Test]
  public async Task ShrinkingIsNeverGatedOnDrainEvidenceAsync() {
    var batch = new AdaptiveStreamBatch(ceiling: 1000, floor: 100, additiveStep: 100);
    for (var i = 0; i < 5; i++) {
      batch.Observe(rowsReturned: 100, capRequested: 100, reclaimedRows: 0);
    }
    var grown = batch.Current;

    batch.Observe(rowsReturned: 600, capRequested: 600, reclaimedRows: 400, drainMeasured: false);

    await Assert.That(batch.Current).IsLessThan(grown)
      .Because("backing off is always safe; a guard that blocked it during the blind window would "
             + "deepen the over-commit it exists to prevent");
  }

  [Test]
  public async Task GrowthIsGatedOnMeasuredDrainAsync() {
    var batch = new AdaptiveStreamBatch(ceiling: 1000, floor: 100, additiveStep: 100);

    batch.Observe(rowsReturned: 100, capRequested: 100, reclaimedRows: 0, drainMeasured: false);

    await Assert.That(batch.Current).IsEqualTo(100)
      .Because("at cold start nothing has measured drain yet; two adaptive controls ramping on "
             + "their own feedback while neither has evidence is how a restart over-commits");
  }

  [Test]
  public async Task ItNeverExceedsTheCeilingAsync() {
    var batch = new AdaptiveStreamBatch(ceiling: 300, floor: 100, additiveStep: 100);
    for (var i = 0; i < 50; i++) {
      batch.Observe(rowsReturned: batch.Current, capRequested: batch.Current, reclaimedRows: 0);
    }

    await Assert.That(batch.Current).IsEqualTo(300);
  }

  [Test]
  public async Task ItNeverCollapsesBelowTheFloorAsync() {
    var batch = new AdaptiveStreamBatch(ceiling: 1000, floor: 100);
    for (var i = 0; i < 50; i++) {
      batch.Observe(rowsReturned: 500, capRequested: 500, reclaimedRows: 500);
    }

    await Assert.That(batch.Current).IsGreaterThanOrEqualTo(100)
      .Because("a page of zero fetches nothing and the stream stops forever — under sustained "
             + "churn the cap must bottom out at a width that still makes progress");
  }

  [Test]
  public async Task AnEmptyFetchSaysNothingAboutDepthAsync() {
    var batch = new AdaptiveStreamBatch(ceiling: 1000, floor: 100, additiveStep: 100);
    for (var i = 0; i < 5; i++) {
      batch.Observe(rowsReturned: 100, capRequested: 100, reclaimedRows: 0);
    }
    var before = batch.Current;

    batch.Observe(rowsReturned: 0, capRequested: before, reclaimedRows: 0);

    await Assert.That(batch.Current).IsEqualTo(before)
      .Because("an empty stream is not a clean cycle, it is no information — folding it in either "
             + "direction makes the cap track idleness instead of depth");
  }

  [Test]
  public async Task AFloorAboveTheCeilingDegradesToFixedRatherThanThrowingAsync() {
    var batch = new AdaptiveStreamBatch(ceiling: 100, floor: 500);

    await Assert.That(batch.Current).IsEqualTo(100)
      .Because("a careless configuration should degrade to a fixed page, not refuse to start");
  }

  [Test]
  public async Task InvalidBoundsAreRejectedAsync() {
    await Assert.That(() => new AdaptiveStreamBatch(ceiling: 0)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new AdaptiveStreamBatch(ceiling: 100, floor: 0)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new AdaptiveStreamBatch(ceiling: 100, floor: 10, additiveStep: 0)).Throws<ArgumentOutOfRangeException>();
  }
}
