using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The adaptive controls act on one pipeline at the same time; these pin what they do to EACH OTHER.
/// </summary>
/// <remarks>
/// <para>
/// Each control is sound in isolation and each has its own tests. What none of them can express
/// alone is the composition: the claim window counts STREAMS, the per-stream page counts ROWS PER
/// STREAM, and the outstanding budget counts TOTAL ROWS. Their product is what actually lands on
/// the service, so two independently reasonable ceilings multiply into a volume neither one
/// authorized.
/// </para>
/// <para>
/// They also share inputs. Re-claim churn shrinks the claim window AND the per-stream page, so a
/// single bad cycle applies two halvings at once — a quarter of the previous volume, from one
/// signal. That is defensible as a safety response but must not be able to reach zero, and must
/// recover, or a transient blip becomes a permanent throughput cut.
/// </para>
/// <para>
/// Cold start is the sharpest case: every control deliberately begins at its floor and gates growth
/// on drain having been measured, while the thing that measures drain only gets a sample if work is
/// admitted. Floors have to be large enough to break that circularity by themselves.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/AdaptiveStreamBatch.cs</code-under-test>
[Category("Workers")]
public class GovernanceInteractionTests {

  private const int LEASE_SECONDS = 300;

  private static (AdaptiveClaimWindow Window, AdaptiveStreamBatch Page, AdaptiveOutstandingBudget Budget) _stack(
      int windowCeiling = 500, int pageCeiling = 1000, int budgetCeiling = 20_000)
    => (new AdaptiveClaimWindow(ceiling: windowCeiling),
        new AdaptiveStreamBatch(ceiling: pageCeiling),
        new AdaptiveOutstandingBudget(leaseSeconds: LEASE_SECONDS, ceiling: budgetCeiling));

  // ---- Composition: the product, not the parts. ----

  [Test]
  public async Task TheBudgetBoundsTheProductOfWindowAndPageAsync() {
    var (window, page, budget) = _stack();

    // Drive both width controls to their ceilings on clean, saturated cycles.
    for (var i = 0; i < 100; i++) {
      window.Observe(claimedRows: window.Current, reclaimedRows: 0);
      page.Observe(rowsReturned: page.Current, capRequested: page.Current, reclaimedRows: 0);
      budget.Observe(completed: 50, elapsed: TimeSpan.FromSeconds(1));
    }

    var wouldFetch = (long)window.Current * page.Current;
    var allowed = budget.Headroom(outstanding: 0);

    await Assert.That(wouldFetch).IsGreaterThan(allowed)
      .Because("this is the hazard, stated as a fact rather than a worry: streams x rows-per-stream "
             + "vastly exceeds the row budget, so the budget is the ONLY thing standing between two "
             + "independently-reasonable ceilings and a commitment neither authorized");
    await Assert.That(allowed).IsLessThanOrEqualTo(20_000)
      .Because("whatever the width controls decide, the row-denominated bound must still cap what "
             + "is actually taken — a caller that consults window and page but not the budget has "
             + "no upper bound at all");
  }

  [Test]
  public async Task AtEveryCeilingTheBudgetStillRefusesWhenFullyOutstandingAsync() {
    var (window, page, budget) = _stack();
    for (var i = 0; i < 100; i++) {
      window.Observe(window.Current, 0);
      page.Observe(page.Current, page.Current, 0);
      budget.Observe(completed: 500, elapsed: TimeSpan.FromSeconds(1));
    }

    await Assert.That(budget.Headroom(outstanding: budget.Current)).IsEqualTo(0)
      .Because("saturated width controls must not be able to talk the budget into overcommitting; "
             + "when everything allowed is already held, the answer is take nothing");
  }

  // ---- Shared signals: one cause, two effects. ----

  [Test]
  public async Task OneChurnCycleShrinksBothWidthControlsAtOnceAsync() {
    var (window, page, _) = _stack();
    for (var i = 0; i < 20; i++) {
      window.Observe(window.Current, 0);
      page.Observe(page.Current, page.Current, 0);
    }
    var wideWindow = window.Current;
    var widePage = page.Current;

    // A single bad cycle, reported to both controls as it would be in the pipeline.
    window.Observe(claimedRows: 400, reclaimedRows: 300);
    page.Observe(rowsReturned: 400, capRequested: 400, reclaimedRows: 300);

    await Assert.That(window.Current).IsLessThan(wideWindow);
    await Assert.That(page.Current).IsLessThan(widePage);
    await Assert.That((long)window.Current * page.Current)
      .IsLessThanOrEqualTo((long)wideWindow * widePage / 4)
      .Because("two halvings compound to a QUARTER of the previous volume from one signal — "
             + "acceptable as a safety response, but it has to be a known quantity rather than an "
             + "emergent surprise, because it is also the recovery distance");
  }

  [Test]
  public async Task SustainedChurnCannotDriveTheCombinedVolumeToZeroAsync() {
    var (window, page, budget) = _stack();

    for (var i = 0; i < 200; i++) {
      window.Observe(claimedRows: 500, reclaimedRows: 500);
      page.Observe(rowsReturned: 500, capRequested: 500, reclaimedRows: 500);
      budget.Observe(completed: 0, elapsed: TimeSpan.FromSeconds(1));
    }

    await Assert.That(window.Current).IsGreaterThan(0);
    await Assert.That(page.Current).IsGreaterThan(0);
    await Assert.That(budget.Current).IsGreaterThan(0);
    await Assert.That((long)window.Current * page.Current).IsGreaterThan(0)
      .Because("if any control could reach zero the pipeline stops permanently and nothing can "
             + "ever produce the clean cycle needed to reopen it — the floors exist so a service "
             + "under sustained pressure degrades instead of latching off");
  }

  [Test]
  public async Task TheStackRecoversAfterAFullCollapseAsync() {
    var (window, page, budget) = _stack();
    for (var i = 0; i < 200; i++) {
      window.Observe(500, 500);
      page.Observe(500, 500, 500);
    }
    var collapsedWindow = window.Current;
    var collapsedPage = page.Current;

    budget.Observe(completed: 200, elapsed: TimeSpan.FromSeconds(1));
    for (var i = 0; i < 40; i++) {
      window.Observe(claimedRows: window.Current, reclaimedRows: 0, drainMeasured: budget.HasDrainSample);
      page.Observe(page.Current, page.Current, 0, drainMeasured: budget.HasDrainSample);
    }

    await Assert.That(window.Current).IsGreaterThan(collapsedWindow);
    await Assert.That(page.Current).IsGreaterThan(collapsedPage)
      .Because("a transient incident must not become a permanent throughput cut; both controls "
             + "have to climb back once cycles are clean, or every blip ratchets the service down");
  }

  // ---- Cold start: the circular dependency. ----

  [Test]
  public async Task ColdStartDoesNotDeadlockWithEveryControlAtItsFloorAsync() {
    var (window, page, budget) = _stack();

    await Assert.That(budget.HasDrainSample).IsFalse();

    // Nothing has measured drain, so both width controls are growth-gated. The floors alone must
    // still admit work, or nothing is ever claimed, nothing completes, and no sample is ever taken.
    window.Observe(claimedRows: window.Current, reclaimedRows: 0, drainMeasured: budget.HasDrainSample);
    page.Observe(page.Current, page.Current, 0, drainMeasured: budget.HasDrainSample);

    var admissible = Math.Min(budget.Headroom(outstanding: 0), window.Current * page.Current);
    await Assert.That(admissible).IsGreaterThan(0)
      .Because("growth is gated on a measurement that only happens if work flows — the floors are "
             + "what breaks the circularity, so a stack that starts fully closed never opens");
  }

  [Test]
  public async Task AnUnmeasuredZeroRateIsNotTreatedAsStalledAsync() {
    var (_, _, budget) = _stack();

    // Below the floor, so the only thing that could close the gate is the stall guard.
    await Assert.That(budget.Headroom(outstanding: 50)).IsGreaterThan(0)
      .Because("a rate of zero that has never been MEASURED means unknown, not stuck; refusing to "
             + "claim on it means the worker never observes the completion that would prove it "
             + "healthy — a deadlock manufactured purely from no-data-yet");
  }

  [Test]
  public async Task HoldingMoreThanTheBudgetClosesTheGateWithoutStallingForeverAsync() {
    var (_, _, budget) = _stack();

    // A restart can resume holding more rows than the cold-start floor allows.
    await Assert.That(budget.Headroom(outstanding: budget.Current * 3)).IsEqualTo(0)
      .Because("already over budget means take nothing — this is the correct answer, not a "
             + "deadlock, because the held rows still drain and lower `outstanding` on their own");

    await Assert.That(budget.Headroom(outstanding: 0)).IsGreaterThan(0)
      .Because("and the gate must reopen purely from work completing, with no clean cycle or "
             + "measurement required first — otherwise recovery depends on the very flow the "
             + "closed gate is preventing");
  }

  [Test]
  public async Task AMeasuredStallClosesTheGateEvenWithWidthControlsWideAsync() {
    var (window, page, budget) = _stack();
    for (var i = 0; i < 100; i++) {
      window.Observe(window.Current, 0);
      page.Observe(page.Current, page.Current, 0);
    }
    budget.Observe(completed: 0, elapsed: TimeSpan.FromSeconds(30));

    await Assert.That(budget.Headroom(outstanding: 500)).IsEqualTo(0)
      .Because("when drain has been measured at zero while work is held, the handler is stuck; "
             + "wide width controls must not override that, since claiming more only burns "
             + "attempts on rows nothing will reach");
  }

  // ---- The bound that sits OUTSIDE the row-denominated controls. ----

  [Test]
  public async Task CompositeExpansionBypassesEveryRowDenominatedBoundAsync() {
    var (window, page, budget) = _stack();
    budget.Observe(completed: 100, elapsed: TimeSpan.FromSeconds(1));

    // One message is admitted. Every control above counted exactly one row for it.
    var admittedRows = 1;
    await Assert.That(budget.Headroom(outstanding: 0)).IsGreaterThanOrEqualTo(admittedRows);

    // Expansion happens AFTER admission: that single row becomes thousands of inbox rows.
    var plan = new CompositeExpansionBudget(maxChildrenPerExpansion: 5000).Plan(innerEventCount: 40_000);

    await Assert.That(plan.Chunks).IsGreaterThan(1)
      .Because("the claim window, the per-stream page and the outstanding budget are all "
             + "calibrated in ROWS and each counted this as one — expansion is the only place the "
             + "real cost is visible, which is why it needs a bound of its own rather than "
             + "inheriting theirs");
  }

  // ---- The allocator turns the row budget into per-stream work without exceeding it. ----

  [Test]
  public async Task TheAllocatorNeverSpendsMoreThanTheBudgetGrantsAsync() {
    var (_, _, budget) = _stack();
    for (var i = 0; i < 50; i++) {
      budget.Observe(completed: 400, elapsed: TimeSpan.FromSeconds(1));
    }
    var allocator = new StreamFairShareAllocator(new StreamFairShareAllocator.Settings());

    var headroom = budget.Headroom(outstanding: 0);
    var demands = Enumerable.Range(0, 60)
      .Select(i => new StreamDemand(Guid.Parse($"00000000-0000-0000-0000-{i + 1:D12}"), 5_000))
      .ToList();

    var plan = allocator.Allocate(headroom, demands);

    await Assert.That(plan.Sum(a => a.Rows)).IsLessThanOrEqualTo(headroom)
      .Because("the allocator is the component that finally spends the budget, so it is the last "
             + "place an overcommit can be introduced — everything upstream only decides how much "
             + "MAY be spent");
  }

  [Test]
  public async Task AStalledBudgetLeavesTheAllocatorWithNothingToSpendAsync() {
    var (_, _, budget) = _stack();
    budget.Observe(completed: 0, elapsed: TimeSpan.FromSeconds(30));
    var allocator = new StreamFairShareAllocator(new StreamFairShareAllocator.Settings());

    var plan = allocator.Allocate(budget.Headroom(outstanding: 500), [new StreamDemand(Guid.NewGuid(), 10_000)]);

    await Assert.That(plan.Count).IsEqualTo(0)
      .Because("a measured stall must propagate all the way through to zero fetching; an allocator "
             + "that treated a deep stream as reason to spend anyway would defeat the stall guard "
             + "entirely");
  }

  [Test]
  public async Task DeepAndShallowStreamsBothProgressUnderOneBudgetAsync() {
    var allocator = new StreamFairShareAllocator(new StreamFairShareAllocator.Settings { MinRowsPerStream = 10 });
    var deep = Guid.Parse("00000000-0000-0000-0000-000000000001");
    var shallow = Guid.Parse("00000000-0000-0000-0000-000000000002");

    var plan = allocator.Allocate(1_000, [new StreamDemand(deep, 100_000), new StreamDemand(shallow, 6)]);

    await Assert.That(plan.Single(a => a.StreamId == shallow).Rows).IsEqualTo(6)
      .Because("the shallow stream finishes outright rather than waiting behind the deep one");
    await Assert.That(plan.Single(a => a.StreamId == deep).Rows).IsGreaterThan(900)
      .Because("and the deep stream still takes nearly the whole budget, so guarding breadth costs "
             + "almost nothing in depth — the two are not actually in tension at this ratio");
  }

  // ---- Housekeeping composes with the width controls through settledness. ----

  [Test]
  public async Task CleanupDefersWhileTheWidthControlsStillHoldWorkAsync() {
    var housekeeping = new HousekeepingCoordinator();
    var (_, _, budget) = _stack();
    budget.Observe(completed: 100, elapsed: TimeSpan.FromSeconds(1));

    var outstanding = budget.Current;
    var decision = housekeeping.TryBegin(
      HousekeepingCoordinator.Activity.Maintenance,
      new ServiceBacklog { UnprocessedInboxRows = 0, ActiveLeasedRows = outstanding });

    await Assert.That(decision.Granted).IsFalse()
      .Because("rows the budget is actively holding are leases the sweep would contend with; "
             + "cleanup keyed off queue depth alone would fire straight into them");
  }

  [Test]
  public async Task CleanupRunsOnceTheWidthControlsHaveReleasedEverythingAsync() {
    var housekeeping = new HousekeepingCoordinator();

    var decision = housekeeping.TryBegin(
      HousekeepingCoordinator.Activity.Maintenance,
      new ServiceBacklog { UnprocessedInboxRows = 0, ActiveLeasedRows = 0 });

    await Assert.That(decision.Granted).IsTrue()
      .Because("the composition must still resolve to 'run' when the pipeline genuinely empties, "
             + "or cleanup is disabled on any service that is ever busy");
  }
}
