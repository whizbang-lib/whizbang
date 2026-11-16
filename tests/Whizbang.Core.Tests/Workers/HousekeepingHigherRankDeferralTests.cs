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
/// Time spent refused for a higher-ranked activity counts toward the lower activity's forced-pass ceiling (#2287).
/// </summary>
/// <remarks>
/// <para>
/// The rank rule refuses a lower activity while a higher one holds the slot, and that refusal used to return before the
/// deferral budget was touched. A long integrity audit (the multi-window catch-up after a restart, say) therefore held
/// maintenance back with no bound at all: none of those refusals advanced <c>MaxDeferral</c>, so the hour only began
/// once the slot was free, and a service that kept integrity busy kept cleanup waiting indefinitely.
/// </para>
/// <para>
/// The rank rule itself is unchanged: integrity still outranks maintenance and is never interrupted. What changes is
/// that the refusal spends the refused activity's budget, so once the ceiling has passed the activity is admitted as
/// <see cref="HousekeepingCoordinator.Verdict.ProceedDeferralLimit"/> at the first ask that finds the higher slot free,
/// without also waiting out the settled cooldown. Every test runs on a fake clock and advances it explicitly.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/HousekeepingCoordinator.cs</code-under-test>
/// <docs>operations/workers/housekeeping-arbitration#a-higher-rank-spends-the-ceiling</docs>
[Category("Workers")]
public class HousekeepingHigherRankDeferralTests {

  private static readonly DateTimeOffset _origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private static readonly TimeSpan _tick = TimeSpan.FromMinutes(10);

  private static ServiceBacklog _busy() => new() { UnprocessedInboxRows = 500, ActiveLeasedRows = 1 };

  // Nothing queued, nothing leased: the reading a service gives while only the framework's integrity work is running.
  private static ServiceBacklog _quiet() => new();

  private static (HousekeepingCoordinator Coordinator, FakeTimeProvider Clock) _build(TimeSpan? cooldown = null) {
    var clock = new FakeTimeProvider(_origin);
    return (new HousekeepingCoordinator(new HousekeepingCoordinator.Settings {
      SettledCooldown = cooldown ?? TimeSpan.FromMinutes(2),
      MaxDeferral = TimeSpan.FromHours(1),
    }, clock), clock);
  }

  // Integrity takes the slot the way the integrity requesters do, without a reading to judge.
  private static void _integrityHolds(HousekeepingCoordinator coordinator) =>
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, backlog: null, requester: "audit");

  [Test]
  public async Task ARefusalBehindIntegrity_StartsTheCeiling_SoTheFirstFreeAskPastItIsForcedAsync() {
    var (coordinator, clock) = _build();
    _integrityHolds(coordinator);

    // An hour of maintenance ticks, every one refused because integrity holds the slot.
    for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(1); elapsed += _tick) {
      var refused = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());
      await Assert.That(refused.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.HigherPriorityRunning);
      clock.Advance(_tick);
    }
    coordinator.End(HousekeepingCoordinator.Activity.Integrity);

    var firstFreeAsk = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());

    await Assert.That(firstFreeAsk.Granted).IsTrue()
      .Because("the sweep has been held back for an hour since its first refusal, and a refusal for a higher rank is "
        + "time held back like any other");
    await Assert.That(firstFreeAsk.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit);
  }

  [Test]
  public async Task ARefusalBehindIntegrity_DoesNotForceThePassBeforeTheCeilingAsync() {
    var (coordinator, clock) = _build();
    _integrityHolds(coordinator);
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());
    coordinator.End(HousekeepingCoordinator.Activity.Integrity);

    clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
    var early = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());
    clock.Advance(TimeSpan.FromSeconds(1));
    var atCeiling = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());

    await Assert.That(early.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceBusy)
      .Because("a second short of an hour after the refusal behind integrity is still inside the ceiling");
    await Assert.That(atCeiling.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("the ceiling is measured from the refusal behind integrity, the first time the sweep was held back");
  }

  [Test]
  public async Task PastTheCeiling_TheFreedSlotAdmitsTheSweepWithoutWaitingOutTheCooldownAsync() {
    var (coordinator, clock) = _build(cooldown: TimeSpan.FromMinutes(30));
    _integrityHolds(coordinator);
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());
    clock.Advance(TimeSpan.FromHours(1));
    coordinator.End(HousekeepingCoordinator.Activity.Integrity);

    // The service has only just gone quiet, so a sweep that had not been held back would still be cooling down.
    var freed = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _quiet());

    await Assert.That(freed.Granted).IsTrue();
    await Assert.That(freed.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("past the ceiling the sweep runs at the first free ask; making it also sit out a fresh cooldown "
        + "would stretch the bound by the cooldown every time integrity let go");
  }

  [Test]
  public async Task PastTheCeiling_IntegrityIsStillNeverInterruptedAsync() {
    var (coordinator, clock) = _build();
    _integrityHolds(coordinator);
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());
    clock.Advance(TimeSpan.FromHours(2));

    var whileIntegrityHolds = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _quiet());

    await Assert.That(whileIntegrityHolds.Granted).IsFalse()
      .Because("the ceiling bounds how long the sweep waits for the slot, never whether it may take a slot that is "
        + "held: integrity outranks cleanup and is not interrupted");
    await Assert.That(whileIntegrityHolds.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.HigherPriorityRunning);
  }

  [Test]
  public async Task IntegrityRefusedByRecovery_SpendsItsOwnRequestersBudgetOnlyAsync() {
    var (coordinator, clock) = _build();
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, backlog: null);

    var refused = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy(), "audit");
    clock.Advance(TimeSpan.FromHours(1));
    coordinator.End(HousekeepingCoordinator.Activity.DeadLetterRecovery);
    var otherRequester = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy(), "delivery");
    var refusedRequester = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy(), "audit");

    await Assert.That(refused.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.HigherPriorityRunning);
    await Assert.That(otherRequester.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceBusy)
      .Because("a requester that never asked while recovery held the slot was never held back, so its ceiling has "
        + "not started");
    await Assert.That(refusedRequester.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("the requester refused behind recovery an hour ago spends its own budget, as it would for a busy "
        + "service");
  }

  [Test]
  public async Task TheSweepRefusedBehindRecovery_SpendsItsOwnBudgetAsync() {
    var (coordinator, clock) = _build();
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, backlog: null);

    var refused = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());
    clock.Advance(TimeSpan.FromHours(1));
    coordinator.End(HousekeepingCoordinator.Activity.DeadLetterRecovery);
    var freed = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());

    await Assert.That(refused.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.HigherPriorityRunning);
    await Assert.That(freed.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("recovery outranks the sweep as integrity does, and a refusal behind it spends the sweep's budget too");
  }

  [Test]
  public async Task WithTheLegacyCount_ARefusalBehindAHigherRankIsCountedAsync() {
    var clock = new FakeTimeProvider(_origin);
    var coordinator = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings {
      SettledCooldown = TimeSpan.Zero,
      MaxConsecutiveDeferrals = 2,
    }, clock);
    _integrityHolds(coordinator);

    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());
    coordinator.End(HousekeepingCoordinator.Activity.Integrity);
    var third = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());

    await Assert.That(third.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("the count is the legacy form of the same ceiling, so the two refusals behind integrity are two of the "
        + "deferrals it tolerates");
  }
}
