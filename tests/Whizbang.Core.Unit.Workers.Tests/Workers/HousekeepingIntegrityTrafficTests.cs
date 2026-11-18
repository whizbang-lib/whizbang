// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2253: a service whose only traffic is the framework's periodic integrity checkpoints reads idle. Every peer publishes a
/// checkpoint on its own cadence, and each one, with the redelivery chatter it sets off, is a background-band row for a
/// moment. Counted as work, those rows reset the settled dwell every half minute, so a two-minute dwell never completed and
/// maintenance ran only as the forced pass at the deferral ceiling. Application work still resets the dwell. #2263: the
/// reading that admits the idle drain leaves that traffic out too, and still lets the idle band it drains hold rows.
/// </summary>
/// <docs>operations/workers/housekeeping-arbitration#what-counts-as-work</docs>
/// <code-under-test>src/Whizbang.Core/Workers/HousekeepingCoordinator.cs</code-under-test>
[Category("Workers")]
public class HousekeepingIntegrityTrafficTests {
  private static readonly DateTimeOffset _origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  // What the backlog reading reports while a checkpoint (or a redelivery it set off) is in the inbox.
  private static ServiceBacklog _checkpointArriving() => new() { UnprocessedInboxRows = 1, ActiveLeasedRows = 1, NonIntegrityWorkRows = 0 };

  private static ServiceBacklog _empty() => new() { NonIntegrityWorkRows = 0 };

  private static ServiceBacklog _applicationWork() => new() { UnprocessedInboxRows = 1, NonIntegrityWorkRows = 1 };

  private static (HousekeepingCoordinator Coordinator, FakeTimeProvider Clock) _build() {
    var clock = new FakeTimeProvider(_origin);
    return (new HousekeepingCoordinator(new HousekeepingCoordinator.Settings { SettledCooldown = TimeSpan.FromMinutes(2) }, clock), clock);
  }

  [Test]
  public async Task AServiceWhoseOnlyTrafficIsIntegrityCheckpoints_AdmitsMaintenanceAfterTheCooldownAsync() {
    var (coordinator, clock) = _build();

    // Every 30 seconds an integrity admission reads the backlog just as a checkpoint lands, and in between it reads empty.
    for (var i = 0; i < 4; i++) {
      coordinator.Observe(_checkpointArriving());
      clock.Advance(TimeSpan.FromSeconds(15));
      coordinator.Observe(_empty());
      clock.Advance(TimeSpan.FromSeconds(15));
    }
    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _checkpointArriving());

    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.Proceed)
      .Because("the framework's own integrity chatter is not work the sweep could get in the way of");
    await Assert.That(decision.Granted).IsTrue();
  }

  [Test]
  public async Task ApplicationWork_StillResetsTheDwellAsync() {
    var (coordinator, clock) = _build();

    coordinator.Observe(_checkpointArriving());
    clock.Advance(TimeSpan.FromSeconds(110));
    coordinator.Observe(_applicationWork());
    clock.Advance(TimeSpan.FromSeconds(20));
    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _checkpointArriving());

    await Assert.That(decision.Granted).IsFalse();
    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceCoolingDown)
      .Because("application work in the middle of the dwell means the service was not idle for the window");
    await Assert.That(coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _applicationWork()).Reason)
      .IsEqualTo(HousekeepingCoordinator.Verdict.ServiceBusy);
  }

  [Test]
  public async Task WithoutTheFigure_EveryRowStillCountsAsWorkAsync() {
    var (coordinator, clock) = _build();
    var unmeasured = new ServiceBacklog { UnprocessedInboxRows = 1 };

    coordinator.Observe(new ServiceBacklog());
    clock.Advance(TimeSpan.FromMinutes(2));
    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, unmeasured);

    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceBusy)
      .Because("a backend that cannot tell integrity traffic apart reports every row as work, as before");
  }

  // ===== #2263: the idle drain =====

  private static readonly TimeSpan _readingMaxAge = TimeSpan.FromMinutes(1);

  [Test]
  public async Task IntegrityOnlyTraffic_AdmitsTheIdleDrainAsync() {
    var (coordinator, _) = _build();

    coordinator.Observe(_checkpointArriving());

    await Assert.That(coordinator.ServiceReadsSettled(_readingMaxAge)).IsTrue()
      .Because("a service whose only rows are the framework's integrity chatter has nothing the idle band would get in the way of");
  }

  [Test]
  public async Task AnIdleBandBesideIntegrityTraffic_StillAdmitsTheIdleDrainAsync() {
    var (coordinator, _) = _build();

    coordinator.Observe(new ServiceBacklog { UnprocessedInboxRows = 1, PendingIdleRows = 40, NonIntegrityWorkRows = 0 });

    await Assert.That(coordinator.ServiceReadsSettled(_readingMaxAge)).IsTrue()
      .Because("the idle band is what the drain exists to release, so its rows never hold the drain back");
  }

  [Test]
  public async Task ApplicationWork_StillHoldsTheIdleDrainBackAsync() {
    var (coordinator, _) = _build();

    coordinator.Observe(_applicationWork());
    var withApplicationWork = coordinator.ServiceReadsSettled(_readingMaxAge);
    coordinator.Observe(new ServiceBacklog { UnprocessedInboxRows = 1, PendingPerspectiveRows = 1, NonIntegrityWorkRows = 0 });
    var withPerspectiveWork = coordinator.ServiceReadsSettled(_readingMaxAge);

    await Assert.That(withApplicationWork).IsFalse()
      .Because("application work in the inbox, under a lease or in the outbox is work the idle band must not compete with");
    await Assert.That(withPerspectiveWork).IsFalse()
      .Because("pending perspective events are application work too, whatever the integrity figure says");
  }

  [Test]
  public async Task WithoutTheFigure_TheIdleDrainStillReadsEveryRowAsWorkAsync() {
    var (coordinator, _) = _build();

    coordinator.Observe(new ServiceBacklog { UnprocessedInboxRows = 1 });
    var withARow = coordinator.ServiceReadsSettled(_readingMaxAge);
    coordinator.Observe(new ServiceBacklog { PendingIdleRows = 40 });
    var withOnlyTheIdleBand = coordinator.ServiceReadsSettled(_readingMaxAge);

    await Assert.That(withARow).IsFalse()
      .Because("a backend that cannot tell integrity traffic apart reports every row as work, as before");
    await Assert.That(withOnlyTheIdleBand).IsTrue();
  }
}
