using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Periodic housekeeping must yield to live work, and must not collide with itself.
/// </summary>
/// <remarks>
/// <para>
/// The heavy maintenance sweep runs on a fixed timer with no regard for what the service is doing.
/// Its statements take locks that the completion path also needs, so when the sweep lands mid-drain
/// the statement that MARKS WORK COMPLETE queues behind it. Workers keep claiming and processing,
/// then stall at the commit; leases stay held; throughput collapses to near zero until the sweep
/// finishes and a burst of commits lands at once.
/// </para>
/// <para>
/// From the outside that reads as a freeze followed by a jump, repeating on the sweep's cadence.
/// The work is not lost and nothing errors — which is what makes it hard to attribute — but a
/// consumer mid-drain can spend most of a cycle blocked on housekeeping that had no reason to run
/// right then. Deferring the sweep to a settled moment costs nothing: there is no deadline on
/// cleanup, and a busy service is precisely when its cost is highest.
/// </para>
/// <para>
/// Settledness here is a SERVICE property, never an instance one — the same distinction the
/// integrity path already draws. Many instances share one inbox, so an instance that has finished
/// its own slice looks idle from the inside while peers still hold leases on the rows the sweep is
/// about to contend with.
/// </para>
/// <para>
/// Two failure modes are guarded explicitly because both have shipped before. A gate that cannot
/// measure must not silently disable the thing it gates, and a gate with no escape hatch starves
/// it — cleanup has no deadline, but it does have a limit, and a service that stays busy for hours
/// still has to reclaim space eventually.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/HousekeepingCoordinator.cs</code-under-test>
[Category("Workers")]
public class HousekeepingCoordinatorTests {

  private static ServiceBacklog _backlog(long unprocessed = 0, long leased = 0)
    => new() { UnprocessedInboxRows = unprocessed, ActiveLeasedRows = leased };

  // ---- Housekeeping yields to live work. ----

  [Test]
  public async Task MaintenanceWaitsWhileRowsAreStillQueuedAsync() {
    var coordinator = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings());

    var decision = coordinator.TryBegin(
      HousekeepingCoordinator.Activity.Maintenance, _backlog(unprocessed: 18_956));

    await Assert.That(decision.Granted).IsFalse()
      .Because("the sweep's statements contend with the one that marks work complete, so running it "
             + "mid-drain stalls every worker at the commit and collapses throughput until it ends");
    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceBusy);
  }

  [Test]
  public async Task MaintenanceWaitsWhileAPeerHoldsLeasesAsync() {
    var coordinator = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings());

    // Nothing queued: this instance has finished its slice and looks completely idle from inside.
    // Peers still hold leases on the shared inbox, so the service is mid-drain.
    var decision = coordinator.TryBegin(
      HousekeepingCoordinator.Activity.Maintenance, _backlog(unprocessed: 0, leased: 3_724));

    await Assert.That(decision.Granted).IsFalse()
      .Because("settledness is a SERVICE property — an instance deciding from its local view starts "
             + "a heavy sweep against rows its own peers are actively committing");
    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceBusy);
  }

  [Test]
  public async Task MaintenanceProceedsOnceTheServiceSettlesAsync() {
    var coordinator = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings());

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _backlog());

    await Assert.That(decision.Granted).IsTrue()
      .Because("deferring is only correct if it still RUNS when the service is quiet — a gate that "
             + "never opens is a disabled feature, not a scheduled one");
    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.Proceed);
  }

  // ---- The two activities are prioritized, not merely serialized. ----

  [Test]
  public async Task MaintenanceDoesNotFireWhileIntegrityWorkIsRunningAsync() {
    var coordinator = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings());
    coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _backlog());

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _backlog());

    await Assert.That(decision.Granted).IsFalse()
      .Because("both walk the same tables; overlapping them puts housekeeping in contention with "
             + "housekeeping, on top of whatever live work is already competing for those locks");
    await Assert.That(decision.Reason)
      .IsEqualTo(HousekeepingCoordinator.Verdict.HigherPriorityRunning);
  }

  [Test]
  public async Task IntegrityIsNotHeldBackByAMaintenanceSweepAsync() {
    var coordinator = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings());
    coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _backlog());

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _backlog());

    await Assert.That(decision.Granted).IsTrue()
      .Because("the priority is asymmetric on purpose — integrity work is correctness-bearing and "
             + "runs on a far tighter cadence, while a deferred cleanup sweep simply runs next tick");
  }

  [Test]
  public async Task ASecondMaintenanceRunCannotOverlapTheFirstAsync() {
    var coordinator = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings());
    coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _backlog());

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _backlog());

    await Assert.That(decision.Granted).IsFalse()
      .Because("a sweep that outruns its own interval would otherwise stack copies of itself, each "
             + "contending with the last");
  }

  [Test]
  public async Task ReleasingTheSlotLetsTheNextActivityInAsync() {
    var coordinator = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings());
    coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _backlog());
    coordinator.End(HousekeepingCoordinator.Activity.Integrity);

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _backlog());

    await Assert.That(decision.Granted).IsTrue()
      .Because("exclusion that outlives the activity is a permanent block, which is strictly worse "
             + "than the contention it was added to prevent");
  }

  // ---- Failure modes that have shipped before. ----

  [Test]
  public async Task AnUnmeasurableBacklogMustNotDisableMaintenanceAsync() {
    var coordinator = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings());

    // Backends that cannot answer the settledness query return null.
    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, backlog: null);

    await Assert.That(decision.Granted).IsTrue()
      .Because("a gate that cannot measure must fall back to prior behavior, never silently switch "
             + "off the thing it gates — 'unknown' and 'busy' are the same value and opposite facts");
    await Assert.That(decision.Reason)
      .IsEqualTo(HousekeepingCoordinator.Verdict.ProceedUnmeasured);
  }

  [Test]
  public async Task MaintenanceIsNotStarvedByAPermanentlyBusyServiceAsync() {
    var coordinator = new HousekeepingCoordinator(
      new HousekeepingCoordinator.Settings { MaxConsecutiveDeferrals = 3 });

    HousekeepingCoordinator.Decision decision = default;
    for (var i = 0; i < 4; i++) {
      decision = coordinator.TryBegin(
        HousekeepingCoordinator.Activity.Maintenance, _backlog(unprocessed: 50_000));
    }

    await Assert.That(decision.Granted).IsTrue()
      .Because("cleanup has no deadline but it does have a limit — a service busy for hours still "
             + "has to reclaim space, and a gate with no escape hatch turns deferral into a leak");
    await Assert.That(decision.Reason)
      .IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("an operator must be able to tell a forced sweep from a settled one — the forced "
             + "case means the service never went quiet, which is itself the incident");
  }

  [Test]
  public async Task TheDeferralBudgetResetsAfterASweepActuallyRunsAsync() {
    var coordinator = new HousekeepingCoordinator(
      new HousekeepingCoordinator.Settings { MaxConsecutiveDeferrals = 2 });

    coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _backlog(unprocessed: 10));
    coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _backlog(unprocessed: 10));
    var forced = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _backlog(unprocessed: 10));
    await Assert.That(forced.Granted).IsTrue();
    coordinator.End(HousekeepingCoordinator.Activity.Maintenance);

    var afterReset = coordinator.TryBegin(
      HousekeepingCoordinator.Activity.Maintenance, _backlog(unprocessed: 10));

    await Assert.That(afterReset.Granted).IsFalse()
      .Because("without a reset the counter stays at its limit and every later cycle forces through, "
             + "which is the un-gated behavior the escape hatch was meant to bound");
    await Assert.That(afterReset.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceBusy);
  }

  [Test]
  public async Task ASettledRunDoesNotConsumeTheDeferralBudgetAsync() {
    var coordinator = new HousekeepingCoordinator(
      new HousekeepingCoordinator.Settings { MaxConsecutiveDeferrals = 2 });

    for (var i = 0; i < 5; i++) {
      var granted = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _backlog());
      await Assert.That(granted.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.Proceed)
        .Because("a service that keeps settling must keep taking the normal path — never the forced "
               + "one, which would misreport a healthy deployment as one that never goes quiet");
      coordinator.End(HousekeepingCoordinator.Activity.Maintenance);
    }
  }

  [Test]
  public async Task EndingAnActivityThatNeverBeganIsHarmlessAsync() {
    var coordinator = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings());
    coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _backlog());

    // A stray release must not hand away someone else's slot.
    coordinator.End(HousekeepingCoordinator.Activity.Maintenance);
    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _backlog());

    await Assert.That(decision.Granted).IsFalse()
      .Because("releasing a slot you do not hold must not cancel the exclusion protecting the "
             + "activity that does");
  }

  [Test]
  public async Task TheContainerRegistersItSoTheGateIsOnByDefaultAsync() {
    var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
    Whizbang.Core.Workers.WorkerPipelineExtensions.AddWhizbangWorkers(services);
    var provider = services.BuildServiceProvider();

    var first = provider.GetService<HousekeepingCoordinator>();
    var second = provider.GetService<HousekeepingCoordinator>();

    await Assert.That(first).IsNotNull()
      .Because("the gate has to arrive with the framework — an opt-in fix ships as a fix nobody "
             + "turns on, and the contention it prevents is the default configuration's problem");
    await Assert.That(second).IsSameReferenceAs(first)
      .Because("exclusion across two workers only works if both resolve the SAME instance; a "
             + "transient registration gives each its own slot and excludes nothing");
  }

  [Test]
  public async Task NullSettingsAreRejectedRatherThanDefaultedAsync()
    => await Assert.That(() => new HousekeepingCoordinator(null!)).Throws<ArgumentNullException>();
}
