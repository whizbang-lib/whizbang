using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The outstanding budget is sized so the leased set drains inside a lease (rate x lease x safety). Measured
/// under a bulk import (#741): a consumer's completion rate fell from about 130 to about 22 rows per second
/// per pod while its leased set stayed at the ceiling, because the smoothed rate estimate followed the drop
/// one fifth per sample and the target stayed above the ceiling for many samples; 5,386 leases lapsed and
/// 10,788 rows were re-claimed. A measured lower rate now takes effect on the next sample; a rise stays
/// smoothed, since over-claiming on one fast interval is the failure the smoothing exists to prevent; and a
/// quiet interval stays smoothed too, since one interval without completions is not evidence of a stall.
/// </summary>
/// <docs>operations/workers/claim-backpressure#lease-aware-budget</docs>
[Category("Unit")]
public class AdaptiveOutstandingBudgetLeaseAwarenessTests {
  private const int LEASE_SECONDS = 300;
  private const int CEILING = 10_000;

  private static AdaptiveOutstandingBudget _budget() =>
    new(LEASE_SECONDS, ceiling: CEILING, floor: 100, safetyFactor: 0.5, smoothing: 0.2);

  [Test]
  public async Task Observe_ARateCollapse_ShrinksTheBudgetOnTheNextSampleAsync() {
    var budget = _budget();
    for (var i = 0; i < 30; i++) {
      budget.Observe(completed: 1_300, elapsed: TimeSpan.FromSeconds(10));   // 130/s: target above the ceiling
    }
    await Assert.That(budget.Current).IsEqualTo(CEILING);

    budget.Observe(completed: 220, elapsed: TimeSpan.FromSeconds(10));       // 22/s: the drain slowed six-fold

    await Assert.That(budget.DrainRatePerSecond).IsLessThanOrEqualTo(22.0)
      .Because("a measured drop is evidence the consumer cannot finish what it holds; waiting for the estimate to catch up is how leases lapse");
    await Assert.That(budget.Current).IsLessThanOrEqualTo((int)(22.0 * LEASE_SECONDS * 0.5))
      .Because("the budget must not exceed what the new rate can complete inside a lease with the safety margin");
  }

  [Test]
  public async Task Observe_ARateRise_IsStillSmoothedAsync() {
    var budget = _budget();
    for (var i = 0; i < 30; i++) {
      budget.Observe(completed: 220, elapsed: TimeSpan.FromSeconds(10));     // settle at 22/s
    }
    var settled = budget.Current;

    budget.Observe(completed: 1_300, elapsed: TimeSpan.FromSeconds(10));     // one fast interval at 130/s

    await Assert.That(budget.DrainRatePerSecond).IsLessThan(130.0)
      .Because("one fast interval must not become the standing estimate; over-claiming on it is the failure the smoothing exists to prevent");
    await Assert.That(budget.Current).IsGreaterThan(settled)
      .Because("the estimate still moves toward the faster rate");
    await Assert.That(budget.Current).IsLessThan(CEILING);
  }

  [Test]
  public async Task Observe_AQuietInterval_StaysSmoothed_ButALowerPositiveRateTakesEffectAtOnceAsync() {
    // One interval with no completions is ambiguous (a pause, an empty moment) and stays smoothed, as the
    // existing smoothing contract requires; a measured LOWER rate is not ambiguous and takes effect now.
    var budget = _budget();
    for (var i = 0; i < 30; i++) {
      budget.Observe(completed: 220, elapsed: TimeSpan.FromSeconds(10));
    }

    budget.Observe(completed: 0, elapsed: TimeSpan.FromSeconds(10));
    await Assert.That(budget.DrainRatePerSecond).IsGreaterThan(0.0)
      .Because("a single quiet interval must not close the headroom; the worker could never observe its own recovery");

    budget.Observe(completed: 50, elapsed: TimeSpan.FromSeconds(10));
    await Assert.That(budget.DrainRatePerSecond).IsLessThanOrEqualTo(5.0)
      .Because("a positive sample below the estimate is a measured slowdown and replaces the estimate at once");
  }
}
