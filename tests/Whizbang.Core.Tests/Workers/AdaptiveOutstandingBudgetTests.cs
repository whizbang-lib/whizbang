using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The claim loop hands a batch to the channel and immediately claims again, while leases live for
/// LeaseSeconds regardless. Outstanding claimed work therefore accumulates across cycles until the
/// entire backlog is held — measured at 21,512 of 21,622 pending rows leased at once. Bounding the
/// per-batch size cannot fix that: at any batch size a fast loop still accumulates everything, it
/// only changes how long it takes.
///
/// This budget bounds OUTSTANDING work instead, in ROWS (the unit leases are held in), so the loop
/// never holds more than it can plausibly drain inside the lease window.
/// </summary>
public class AdaptiveOutstandingBudgetTests {

  private const int LEASE_SECONDS = 300;
  private const int CEILING = 10_000;
  private const int FLOOR = 100;

  private static AdaptiveOutstandingBudget _budget(double safetyFactor = 0.5) =>
    new(LEASE_SECONDS, CEILING, FLOOR, safetyFactor);

  [Test]
  public async Task Current_BeforeAnyObservation_StartsAtTheFloorAsync() {
    // Cold start is the DANGEROUS moment, not the safe one: a restart carrying a large backlog has
    // no drain history and is exactly when the old code claimed everything. Start low and earn the
    // budget, rather than assuming capacity until proven otherwise.
    await Assert.That(_budget().Current).IsEqualTo(FLOOR);
  }

  [Test]
  public async Task Current_AfterSustainedDrain_ApproachesRateTimesLeaseTimesSafetyAsync() {
    var budget = _budget();

    // 10 rows/sec sustained. Target = 10 * 300 * 0.5 = 1500.
    for (var i = 0; i < 40; i++) {
      budget.Observe(completed: 10, elapsed: TimeSpan.FromSeconds(1));
    }

    await Assert.That(budget.Current).IsGreaterThan(1000);
    await Assert.That(budget.Current).IsLessThanOrEqualTo(1500);
  }

  [Test]
  public async Task SafetyFactor_ScalesTheComputedBudgetAsync() {
    var half = _budget(safetyFactor: 0.5);
    var full = _budget(safetyFactor: 1.0);

    for (var i = 0; i < 40; i++) {
      half.Observe(completed: 10, elapsed: TimeSpan.FromSeconds(1));
      full.Observe(completed: 10, elapsed: TimeSpan.FromSeconds(1));
    }

    // Lease expiry is a cliff, not a gradual degradation — running at the full computed capacity
    // means any slowdown tips straight into expiry. The factor buys headroom for that.
    await Assert.That(full.Current).IsGreaterThan(half.Current);
  }

  [Test]
  public async Task Growth_IsGradual_NotAnImmediateJumpToTheFullBudgetAsync() {
    var budget = _budget();

    budget.Observe(completed: 10, elapsed: TimeSpan.FromSeconds(1));
    var afterOne = budget.Current;

    // One sample is not evidence of sustained capacity. Jumping to the full computed budget on a
    // single good reading is how a control loop overshoots straight back into the failure.
    await Assert.That(afterOne).IsLessThan(1500);
  }

  [Test]
  public async Task Current_NeverExceedsTheCeilingAsync() {
    var budget = _budget();

    for (var i = 0; i < 200; i++) {
      budget.Observe(completed: 100_000, elapsed: TimeSpan.FromSeconds(1));
    }

    await Assert.That(budget.Current).IsLessThanOrEqualTo(CEILING);
  }

  [Test]
  public async Task Current_NeverDropsBelowTheFloorAsync() {
    var budget = _budget();

    for (var i = 0; i < 50; i++) {
      budget.Observe(completed: 0, elapsed: TimeSpan.FromSeconds(1));
    }

    // Even a fully stalled service must retain a floor, otherwise it can never pick work up again
    // once the stall clears.
    await Assert.That(budget.Current).IsGreaterThanOrEqualTo(FLOOR);
  }

  [Test]
  public async Task Headroom_WhenOutstandingIsAtBudget_IsZeroAsync() {
    var budget = _budget();

    await Assert.That(budget.Headroom(outstanding: FLOOR)).IsEqualTo(0);
    await Assert.That(budget.Headroom(outstanding: FLOOR + 5_000)).IsEqualTo(0);
  }

  [Test]
  public async Task Headroom_WhenStalledWithWorkOutstanding_IsZeroAsync() {
    var budget = _budget();

    // Drain has stopped while work is still held. Claiming more cannot help a stuck handler and
    // only burns attempts on rows that will never be reached — so claim nothing at all.
    budget.Observe(completed: 0, elapsed: TimeSpan.FromSeconds(5));

    await Assert.That(budget.Headroom(outstanding: 1)).IsEqualTo(0);
  }

  [Test]
  public async Task Headroom_BeforeAnyMeasurement_TreatsZeroDrainAsUnknownNotStalledAsync() {
    var budget = _budget();

    // No sample has been taken yet, so the drain rate is zero by INITIALISATION, not by
    // measurement. Treating that as a stall deadlocks the loop: a worker holding any outstanding
    // work would refuse to claim, and therefore never observe the completion that would prove it
    // is healthy. "No data yet" and "no progress" must not collapse into the same state.
    await Assert.That(budget.Headroom(outstanding: 10)).IsGreaterThan(0);
  }

  [Test]
  public async Task Headroom_WhenIdleWithNothingOutstanding_AllowsProbingAsync() {
    var budget = _budget();

    // Nothing outstanding and nothing completing means an EMPTY queue, not a stalled one. The
    // worker must still be allowed to look for work, or an idle service would never restart.
    budget.Observe(completed: 0, elapsed: TimeSpan.FromSeconds(5));

    await Assert.That(budget.Headroom(outstanding: 0)).IsGreaterThan(0);
  }

  [Test]
  public async Task Observe_WithNonPositiveElapsed_IsIgnoredAsync() {
    var budget = _budget();
    for (var i = 0; i < 40; i++) {
      budget.Observe(completed: 10, elapsed: TimeSpan.FromSeconds(1));
    }
    var settled = budget.Current;

    budget.Observe(completed: 1_000_000, elapsed: TimeSpan.Zero);
    budget.Observe(completed: 1_000_000, elapsed: TimeSpan.FromSeconds(-1));

    // Rate is completed/elapsed, so a zero or negative interval is a division by zero or a negative
    // rate. Either would wreck the estimate from a single bad sample — and the caller supplies the
    // interval, so a clock glitch or a same-tick double-poll is enough to produce one.
    await Assert.That(budget.Current).IsEqualTo(settled);
  }

  [Test]
  public async Task Constructor_FloorAboveCeiling_ClampsRatherThanThrowsAsync() {
    // Mirrors AdaptiveClaimWindow: a careless configuration should degrade to "fixed size" rather
    // than refuse to start.
    var budget = new AdaptiveOutstandingBudget(LEASE_SECONDS, ceiling: 50, floor: 5_000);

    await Assert.That(budget.Current).IsLessThanOrEqualTo(50);
  }
}
