using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;

namespace Whizbang.Core.Tests.Execution;

/// <summary>
/// A governor that finds its own width by watching whether widening actually helps.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AdaptiveConcurrencyGovernor"/> needs a caller to tell it when the resource pushed
/// back. Nothing in the drain path produces that signal today, and a governor that never observes
/// contention grows to its ceiling and stays there — worse than the constant it replaces.
/// </para>
/// <para>
/// This one needs no such signal. Every cycle already reports how much work was waiting and how
/// long the cycle took, which is throughput. If widening stopped improving throughput, the extra
/// width is buying nothing and is probably costing something shared — that IS the contention
/// signal, inferred rather than instrumented.
/// </para>
/// <para>
/// The dangerous failure is mistaking a WORKLOAD change for contention. Throughput falls when the
/// items get more expensive, when a burst ends, or when the queue empties — none of which mean
/// "too wide". A controller that shrinks on those will converge to its floor on a perfectly
/// healthy system and never recover. Most of what is pinned below is that distinction.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Execution/ThroughputGovernor.cs</code-under-test>
[Category("Execution")]
public class ThroughputGovernorTests {

  /// <summary>A cycle that did <paramref name="items"/> units of work in <paramref name="ms"/>.</summary>
  private static GovernorSignal _cycle(int items, double ms, int queued = 5000)
    => new(QueuedItems: queued, Contended: false, Elapsed: TimeSpan.FromMilliseconds(ms),
           CompletedItems: items);

  // ---------- it must still behave like a governor ----------

  [Test]
  public async Task StartsAtFloor_AndStaysInBandAsync() {
    var g = new ThroughputGovernor(floor: 4, ceiling: 64);

    await Assert.That(g.CurrentWidth).IsEqualTo(4)
      .Because("width is earned from observed improvement, never assumed");

    for (var i = 0; i < 300; i++) {
      g.Observe(_cycle(items: 1000, ms: 100));
    }

    await Assert.That(g.CurrentWidth).IsGreaterThanOrEqualTo(4);
    await Assert.That(g.CurrentWidth).IsLessThanOrEqualTo(64)
      .Because("the band is the safety contract — escaping the ceiling is the connection "
             + "exhaustion this exists to avoid");
  }

  // ---------- the core behavior: widen while it pays ----------

  [Test]
  public async Task WidensWhileThroughputKeepsImprovingAsync() {
    var g = new ThroughputGovernor(floor: 2, ceiling: 64);
    var start = g.CurrentWidth;

    // Each cycle completes more work per unit time than the last — widening is paying off.
    var perCycle = 100;
    for (var i = 0; i < 12; i++) {
      g.Observe(_cycle(items: perCycle, ms: 100));
      perCycle += 60;
    }

    await Assert.That(g.CurrentWidth).IsGreaterThan(start)
      .Because("improving throughput is the only evidence that more width helps, and it is the "
             + "whole reason to grow");
  }

  [Test]
  public async Task StopsWideningOnceThroughputPlateausAsync() {
    var g = new ThroughputGovernor(floor: 2, ceiling: 512);

    // Ramp until it plateaus, then hold throughput flat for a long stretch.
    var perCycle = 100;
    for (var i = 0; i < 10; i++) { g.Observe(_cycle(items: perCycle, ms: 100)); perCycle += 60; }
    var atPlateau = g.CurrentWidth;
    for (var i = 0; i < 40; i++) { g.Observe(_cycle(items: perCycle, ms: 100)); }

    await Assert.That(g.CurrentWidth).IsLessThanOrEqualTo(atPlateau + 2)
      .Because("flat throughput means the extra width bought nothing — continuing to grow would "
             + "consume shared resources for no gain, which is exactly how a governor turns a "
             + "slow path into someone else's outage");
  }

  [Test]
  public async Task BacksOffWhenWideningMakesThroughputWorseAsync() {
    var g = new ThroughputGovernor(floor: 2, ceiling: 512);

    var perCycle = 100;
    for (var i = 0; i < 10; i++) { g.Observe(_cycle(items: perCycle, ms: 100)); perCycle += 60; }
    var widened = g.CurrentWidth;

    // Throughput now collapses while the queue stays deep — the signature of real contention.
    for (var i = 0; i < 6; i++) { g.Observe(_cycle(items: 40, ms: 100)); }

    await Assert.That(widened).IsGreaterThan(2)
      .Because("the arrange step must actually widen or the decay assertion proves nothing");
    await Assert.That(g.CurrentWidth).IsLessThan(widened)
      .Because("throughput falling while work is still queued is what contention looks like from "
             + "the inside — there is no other reason for the same width to do less work");
  }

  // ---------- the dangerous part: do not confuse workload with contention ----------

  [Test]
  public async Task DoesNotShrinkWhenTheQueueSimplyEmptiesAsync() {
    var g = new ThroughputGovernor(floor: 2, ceiling: 64);
    var perCycle = 100;
    for (var i = 0; i < 10; i++) { g.Observe(_cycle(items: perCycle, ms: 100)); perCycle += 60; }
    var widened = g.CurrentWidth;

    // Work runs out. Throughput collapses, but nothing is contended — there is just nothing to do.
    for (var i = 0; i < 10; i++) { g.Observe(_cycle(items: 0, ms: 100, queued: 0)); }

    await Assert.That(g.CurrentWidth).IsEqualTo(widened)
      .Because("an idle queue is not pushback. A governor that decays on quiet arrives at every "
             + "burst already narrowed, which is precisely when width matters most");
  }

  [Test]
  public async Task DoesNotShrinkWhenItemsMerelyGotMoreExpensiveAsync() {
    var g = new ThroughputGovernor(floor: 2, ceiling: 64);
    var perCycle = 100;
    for (var i = 0; i < 10; i++) { g.Observe(_cycle(items: perCycle, ms: 100)); perCycle += 60; }
    var widened = g.CurrentWidth;

    // Same items/sec, but each cycle takes proportionally longer: heavier work, not contention.
    for (var i = 0; i < 8; i++) { g.Observe(_cycle(items: perCycle * 4, ms: 400)); }

    await Assert.That(g.CurrentWidth).IsGreaterThanOrEqualTo(widened)
      .Because("throughput per unit TIME is unchanged — the work simply got heavier. Shrinking "
             + "here would punish a healthy system for processing bigger items");
  }

  [Test]
  public async Task RecoversWidthAfterTransientContentionClearsAsync() {
    var g = new ThroughputGovernor(floor: 2, ceiling: 64);
    var perCycle = 100;
    for (var i = 0; i < 10; i++) { g.Observe(_cycle(items: perCycle, ms: 100)); perCycle += 60; }

    for (var i = 0; i < 5; i++) { g.Observe(_cycle(items: 30, ms: 100)); }
    var afterDip = g.CurrentWidth;

    // Contention clears and throughput climbs again.
    var recovering = 200;
    for (var i = 0; i < 15; i++) { g.Observe(_cycle(items: recovering, ms: 100)); recovering += 80; }

    await Assert.That(g.CurrentWidth).IsGreaterThan(afterDip)
      .Because("a controller that only ever shrinks is a ratchet — one bad minute would cost the "
             + "system its width permanently");
  }

  [Test]
  public async Task ExplicitContentionStillDominatesEverythingAsync() {
    var g = new ThroughputGovernor(floor: 2, ceiling: 64);
    var perCycle = 100;
    for (var i = 0; i < 10; i++) { g.Observe(_cycle(items: perCycle, ms: 100)); perCycle += 60; }
    var widened = g.CurrentWidth;

    // Throughput is still improving AND the queue is deep — every reason to grow. But the caller
    // reports real pushback, which must win.
    g.Observe(new GovernorSignal(QueuedItems: 100_000, Contended: true,
                                 Elapsed: TimeSpan.FromMilliseconds(100), CompletedItems: 5000));

    await Assert.That(g.CurrentWidth).IsLessThan(widened)
      .Because("an inferred signal must never override an observed one — if a caller has a real "
             + "pressure source it is strictly better evidence than throughput arithmetic");
  }
}
