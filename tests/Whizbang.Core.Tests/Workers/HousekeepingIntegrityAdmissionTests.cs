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
/// Stream-integrity work is idle-gated like dead-letter recovery (#2001): deferred while the service is busy or
/// cooling down, forced through once it has been deferred for <see cref="HousekeepingCoordinator.Settings.MaxDeferral"/>,
/// and re-armed when a pass completes. It keeps its rank: above the cleanup sweep, below recovery.
/// </summary>
/// <remarks>
/// Before #2001 the integrity slot was granted without asking whether the service was settled. Integrity work on the
/// receive side (reconciling a redelivery bundle, comparing a manifest chunk, building a redelivery) is the expensive
/// part of the feature, and running it in parallel with a service's ordinary traffic is what held a shared database's
/// CPU at its ceiling. Every elapsed-time assertion runs on a fake clock; nothing here waits.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/HousekeepingCoordinator.cs</code-under-test>
/// <docs>operations/workers/housekeeping-arbitration</docs>
[Category("Workers")]
public class HousekeepingIntegrityAdmissionTests {
  private static readonly DateTimeOffset _origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private static ServiceBacklog _busy() => new() { UnprocessedInboxRows = 500, ActiveLeasedRows = 1 };

  private static ServiceBacklog _quiet() => new();

  private static (HousekeepingCoordinator Coordinator, FakeTimeProvider Clock) _build(
      TimeSpan? cooldown = null, TimeSpan? maxDeferral = null) {
    var clock = new FakeTimeProvider(_origin);
    var settings = new HousekeepingCoordinator.Settings {
      SettledCooldown = cooldown ?? TimeSpan.Zero,
      MaxDeferral = maxDeferral ?? TimeSpan.FromHours(1),
    };
    return (new HousekeepingCoordinator(settings, clock), clock);
  }

  [Test]
  public async Task Integrity_IsDeferredWhileTheServiceIsBusyAsync() {
    var (coordinator, _) = _build();

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy());

    await Assert.That(decision.Granted).IsFalse()
      .Because("integrity work on a busy service competes with the traffic it exists to verify");
    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceBusy);
  }

  [Test]
  public async Task Integrity_IsDeferredWhileTheServiceIsCoolingDownAsync() {
    var (coordinator, clock) = _build(cooldown: TimeSpan.FromMinutes(2));
    coordinator.Observe(_quiet());
    clock.Advance(TimeSpan.FromMinutes(1));

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _quiet());

    await Assert.That(decision.Granted).IsFalse()
      .Because("an empty reading between bursts is a trough, not an ending; the dwell tells them apart");
    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceCoolingDown);
  }

  [Test]
  public async Task Integrity_ProceedsOnceTheServiceHasStayedQuietAsync() {
    var (coordinator, clock) = _build(cooldown: TimeSpan.FromMinutes(2));
    coordinator.Observe(_quiet());
    clock.Advance(TimeSpan.FromMinutes(2));

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _quiet());

    await Assert.That(decision.Granted).IsTrue();
    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.Proceed);
  }

  [Test]
  public async Task Integrity_AnUnmeasuredReading_ProceedsAsBeforeAsync() {
    var (coordinator, _) = _build();

    var decision = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, backlog: null);

    await Assert.That(decision.Granted).IsTrue()
      .Because("a gate that cannot measure must never silently disable what it gates");
    await Assert.That(decision.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedUnmeasured);
  }

  [Test]
  public async Task Integrity_IsForcedAtMaxDeferral_AndNotAMomentBeforeAsync() {
    var (coordinator, clock) = _build(maxDeferral: TimeSpan.FromMinutes(30));
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy());

    clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromTicks(1));
    var early = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy());
    clock.Advance(TimeSpan.FromTicks(1));
    var forced = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy());

    await Assert.That(early.Granted).IsFalse();
    await Assert.That(forced.Granted).IsTrue()
      .Because("a service that never goes quiet must still verify its streams");
    await Assert.That(forced.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit);
  }

  [Test]
  public async Task Integrity_ACompletedPass_RearmsTheBudgetAsync() {
    var (coordinator, clock) = _build(maxDeferral: TimeSpan.FromMinutes(30));
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy());
    clock.Advance(TimeSpan.FromMinutes(30));
    var forced = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy());
    coordinator.End(HousekeepingCoordinator.Activity.Integrity);

    var next = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy());

    await Assert.That(forced.Granted).IsTrue();
    await Assert.That(next.Granted).IsFalse()
      .Because("the forced pass is a trickle under load, never an open gate: a pass that ran re-arms the ceiling");
    await Assert.That(next.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceBusy);
  }

  [Test]
  public async Task Integrity_SpendsItsOwnBudget_NotTheSweepsAsync() {
    var (coordinator, clock) = _build(maxDeferral: TimeSpan.FromMinutes(30));
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());
    clock.Advance(TimeSpan.FromMinutes(30));

    var integrity = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy());

    await Assert.That(integrity.Granted).IsFalse()
      .Because("thirty minutes of deferred sweeps are not thirty minutes of deferred integrity work");
  }

  [Test]
  public async Task Integrity_EachRequesterSpendsAndRearmsItsOwnBudgetAsync() {
    // Three integrity callers share one slot. With one shared budget, whichever asks first after the ceiling takes the
    // forced pass and re-arms the ceiling for all of them, so a caller on a slow tick could be starved for as long as
    // the load lasts. Each requester's ceiling is its own.
    var (coordinator, clock) = _build(maxDeferral: TimeSpan.FromMinutes(30));
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy(), requester: "fast");
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy(), requester: "slow");
    clock.Advance(TimeSpan.FromMinutes(30));

    var fast = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy(), requester: "fast");
    coordinator.End(HousekeepingCoordinator.Activity.Integrity);
    var slow = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy(), requester: "slow");
    coordinator.End(HousekeepingCoordinator.Activity.Integrity);
    var fastAgain = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy(), requester: "fast");

    await Assert.That(fast.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit);
    await Assert.That(slow.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("another requester's completed pass does not re-arm this requester's ceiling");
    await Assert.That(fastAgain.Granted).IsFalse()
      .Because("the requester whose pass ran is re-armed");
  }

  [Test]
  public async Task Integrity_KeepsItsRankAboveTheSweepAsync() {
    var (coordinator, _) = _build();
    var sweep = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _quiet());

    var integrity = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _quiet());

    await Assert.That(sweep.Granted).IsTrue();
    await Assert.That(integrity.Granted).IsTrue()
      .Because("a running sweep never holds integrity back; integrity yields only to a strictly higher rank");
  }

  [Test]
  public async Task Integrity_KeepsItsRankBelowRecoveryAsync() {
    var (coordinator, _) = _build();
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, _quiet());

    var integrity = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _quiet());

    await Assert.That(integrity.Granted).IsFalse();
    await Assert.That(integrity.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.HigherPriorityRunning);
  }

  [Test]
  public async Task Integrity_AStrayRelease_DoesNotRearmTheBudgetAsync() {
    var (coordinator, clock) = _build(maxDeferral: TimeSpan.FromMinutes(30));
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy());
    clock.Advance(TimeSpan.FromMinutes(30));
    coordinator.End(HousekeepingCoordinator.Activity.Integrity);

    var forced = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy());

    await Assert.That(forced.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("only a pass that actually ran re-arms the ceiling; a release with nothing running is a no-op");
  }
}
