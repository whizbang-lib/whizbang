using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The settled reading has to HOLD before cleanup is admitted.
/// </summary>
/// <remarks>
/// <para>
/// Settledness is a sample, and a sample is not a state. A service working through a bulk load
/// empties its work tables between bursts, so an instant reading of zero says only "nothing is
/// queued at this instant". Admitted on that reading, a sweep of tens of seconds starts just as
/// the next burst lands — precisely the collision the gate exists to prevent, arrived at through
/// the gate rather than around it.
/// </para>
/// <para>
/// The dwell is measured on an injected clock, never by waiting: a test that sleeps to cross a
/// threshold is a test that fails on a loaded machine for reasons that have nothing to do with
/// the behavior it names.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/HousekeepingCoordinator.cs</code-under-test>
/// <docs>operations/workers/housekeeping-arbitration</docs>
[Category("Workers")]
public class HousekeepingCooldownTests {

  private static readonly DateTimeOffset _origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private static ServiceBacklog _settled() => new();

  private static ServiceBacklog _busy() => new() { UnprocessedInboxRows = 1 };

  private static (HousekeepingCoordinator Coordinator, FakeTimeProvider Clock) _build(TimeSpan cooldown, int maxDeferrals = 6) {
    var clock = new FakeTimeProvider(_origin);
    var settings = new HousekeepingCoordinator.Settings {
      SettledCooldown = cooldown,
      MaxConsecutiveDeferrals = maxDeferrals,
    };
    return (new HousekeepingCoordinator(settings, clock), clock);
  }

  [Test]
  public async Task TheFirstSettledReading_IsRefused_BecauseItCouldBeATroughAsync() {
    var (coordinator, _) = _build(TimeSpan.FromMinutes(2));

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _settled());

    await Assert.That(decision.Granted).IsFalse()
      .Because("one reading of zero cannot distinguish a gap between bursts from the end of the "
        + "load; admitting on it is how a sweep lands on the next burst.");
    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceCoolingDown)
      .Because("cooling down and busy call for opposite operator responses and must not report "
        + "as the same verdict.");
  }

  [Test]
  public async Task AfterTheDwellElapses_WithTheServiceStillSettled_TheSweepIsAdmittedAsync() {
    var (coordinator, clock) = _build(TimeSpan.FromMinutes(2));

    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _settled());
    clock.Advance(TimeSpan.FromMinutes(2));

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _settled());

    await Assert.That(decision.Granted).IsTrue();
    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.Proceed);
  }

  [Test]
  public async Task WorkArrivingDuringTheDwell_RestartsIt_RatherThanResumingAsync() {
    var (coordinator, clock) = _build(TimeSpan.FromMinutes(2));

    // Settled, then a burst most of the way through the dwell.
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _settled());
    clock.Advance(TimeSpan.FromSeconds(110));
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());

    // Quiet again, and enough time to have finished the ORIGINAL dwell.
    clock.Advance(TimeSpan.FromSeconds(20));
    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _settled());

    await Assert.That(decision.Granted).IsFalse()
      .Because("the dwell measures an UNBROKEN run of settled readings; work arriving in the "
        + "middle means the service was not quiet for the window, and resuming the old clock "
        + "would admit a sweep on exactly the bursty pattern this guards.");
    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceCoolingDown);
  }

  [Test]
  public async Task ACooldownOfZero_AdmitsOnTheFirstSettledReadingAsync() {
    // The pre-cooldown behavior stays reachable, so an operator can turn the dwell off.
    var (coordinator, _) = _build(TimeSpan.Zero);

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _settled());

    await Assert.That(decision.Granted).IsTrue();
    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.Proceed);
  }

  [Test]
  public async Task AServiceThatKeepsCoolingDown_StillReachesTheForcedSweepAsync() {
    // Otherwise the cooldown becomes a way to starve cleanup forever: a service that alternates
    // between working and brief troughs would never satisfy the dwell and never reclaim space.
    var (coordinator, _) = _build(TimeSpan.FromMinutes(2), maxDeferrals: 3);

    for (var i = 0; i < 3; i++) {
      var refused = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _settled());
      await Assert.That(refused.Granted).IsFalse();
    }

    var forced = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _settled());

    await Assert.That(forced.Granted).IsTrue();
    await Assert.That(forced.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("a forced sweep is reported distinctly so 'this service never settled long "
        + "enough' is visible rather than silent.");
  }

  [Test]
  public async Task AnUnmeasurableReading_DoesNotClearTheDwellAsync() {
    // A probe that failed is not evidence of work. Treating null as busy would let one flaky
    // query starve cleanup for the life of the process.
    var (coordinator, clock) = _build(TimeSpan.FromMinutes(2));

    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _settled());
    clock.Advance(TimeSpan.FromMinutes(1));
    coordinator.Observe(null);
    clock.Advance(TimeSpan.FromMinutes(1));

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _settled());

    await Assert.That(decision.Granted).IsTrue()
      .Because("the dwell had elapsed; an unmeasurable reading in the middle must not restart it.");
  }

  [Test]
  public async Task ObservationsBetweenSweeps_CountTowardTheDwellAsync() {
    // Without this the gate's resolution is its own interval: it could only ever see settledness
    // at the moment it wanted to run, which is the one moment that cannot distinguish a trough.
    var (coordinator, clock) = _build(TimeSpan.FromMinutes(2));

    coordinator.Observe(_settled());
    clock.Advance(TimeSpan.FromMinutes(2));

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _settled());

    await Assert.That(decision.Granted).IsTrue()
      .Because("a reading taken between sweeps is still a reading of the service.");
  }

  [Test]
  public async Task ABusyService_StillReportsBusy_NotCoolingDownAsync() {
    // The cooldown must not swallow the busy verdict: they mean different things.
    var (coordinator, _) = _build(TimeSpan.FromMinutes(2));

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());

    await Assert.That(decision.Granted).IsFalse();
    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceBusy);
  }
}
