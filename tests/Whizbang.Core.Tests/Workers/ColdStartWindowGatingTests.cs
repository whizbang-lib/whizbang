using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Two adaptive controls govern how much work an instance takes: the claim window (streams per
/// claim) and the outstanding budget (total rows held). Only the budget measures drain. While it has
/// no sample yet, the window must not ramp on its own.
/// </summary>
/// <remarks>
/// <para>
/// At cold start the budget correctly begins at its floor because it has no drain history. The
/// window, however, grows additively from its own feedback (re-claim share), and acquisition
/// accumulates across polls before the first drain sample lands. During that blind period two
/// controls ramp independently and only one of them is measuring anything, so an instance starting
/// onto a large backlog can commit to more than it can drain inside one lease.
/// </para>
/// <para>
/// Observed: a restart onto a large backlog lapsed several hundred rows exactly once — attempts
/// 1→2, self-corrected, nothing dead-lettered. Bounded, but it spends retry budget for no reason,
/// and a restart onto a backlog is precisely when the budget is blind.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
[Category("Workers")]
public class ColdStartWindowGatingTests {

  private static AdaptiveClaimWindow _window() => new(ceiling: 1000, floor: 25, additiveStep: 25);

  [Test]
  public async Task WindowDoesNotGrow_WhileTheBudgetHasNoDrainSampleAsync() {
    var window = _window();
    var budget = new AdaptiveOutstandingBudget(leaseSeconds: 300, ceiling: 10_000, floor: 100);
    var start = window.Current;

    // Clean claims — the window's own feedback says "grow" — but the budget has measured nothing.
    for (var i = 0; i < 20; i++) {
      window.Observe(claimedRows: 500, reclaimedRows: 0, drainMeasured: budget.HasDrainSample);
    }

    await Assert.That(window.Current).IsEqualTo(start)
      .Because("the window must not ramp while the only control that MEASURES has no reading — two "
             + "controls ramping independently during the blind period is what lets a restart "
             + "commit to more than it can drain inside one lease");
  }

  [Test]
  public async Task WindowGrowsNormally_OnceDrainHasBeenMeasuredAsync() {
    var window = _window();
    var budget = new AdaptiveOutstandingBudget(leaseSeconds: 300, ceiling: 10_000, floor: 100);
    budget.Observe(completed: 500, elapsed: TimeSpan.FromSeconds(1));
    var start = window.Current;

    for (var i = 0; i < 5; i++) {
      window.Observe(claimedRows: 500, reclaimedRows: 0, drainMeasured: budget.HasDrainSample);
    }

    await Assert.That(window.Current).IsGreaterThan(start)
      .Because("the gate is for the blind period ONLY — once drain is measured the window must ramp "
             + "as before, or this turns a startup guard into a permanent throughput ceiling");
  }

  [Test]
  public async Task WindowStillSHRINKS_WhileBlind_BecauseBackingOffIsAlwaysSafeAsync() {
    var window = _window();
    for (var i = 0; i < 5; i++) {
      window.Observe(claimedRows: 500, reclaimedRows: 0, drainMeasured: true);
    }
    var grown = window.Current;

    // Heavy re-claim share: the window's signal that it is over-committed. Blind or not, shrinking
    // must never be gated — the gate exists to stop unmeasured GROWTH, and refusing to back off
    // would make the guard itself a hazard.
    for (var i = 0; i < 5; i++) {
      window.Observe(claimedRows: 500, reclaimedRows: 400, drainMeasured: false);
    }

    await Assert.That(window.Current).IsLessThan(grown)
      .Because("backing off is always safe and must never be gated — only unmeasured growth is the "
             + "hazard, and a guard that blocked shrinking would deepen the very over-commit it "
             + "exists to prevent");
  }

  [Test]
  public async Task BudgetReportsNoSampleUntilItObservesAsync() {
    var budget = new AdaptiveOutstandingBudget(leaseSeconds: 300, ceiling: 10_000, floor: 100);

    await Assert.That(budget.HasDrainSample).IsFalse()
      .Because("a budget that has measured nothing must say so — this is the signal the window gate "
             + "reads, and defaulting it to true would silently disable the gate");

    budget.Observe(completed: 10, elapsed: TimeSpan.FromSeconds(1));

    await Assert.That(budget.HasDrainSample).IsTrue();
  }
}
