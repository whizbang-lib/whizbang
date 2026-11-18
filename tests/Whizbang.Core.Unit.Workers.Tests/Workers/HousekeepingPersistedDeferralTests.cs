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
/// The forced-pass ceiling is measured from a first deferral the shared store holds, so it survives a restart (#2288).
/// </summary>
/// <remarks>
/// <para>
/// The ceiling used to be measured from a moment held only in process memory, so every restart started the hour over
/// and a service redeployed more often than <c>MaxDeferral</c> never reached its forced pass. The store now holds the
/// moment, read with the service backlog (<see cref="ServiceBacklog.HousekeepingDeferrals"/>). The coordinator does no
/// I/O of its own: a decision reports what the caller records (<see cref="HousekeepingCoordinator.Decision.DeferralToRecord"/>)
/// and what it clears once the pass completes (<see cref="HousekeepingCoordinator.Decision.DeferralToClear"/>).
/// </para>
/// <para>
/// A backlog without the figure (a backend that cannot hold it) keeps the in-memory behavior exactly. Every test runs
/// on a fake clock and advances it explicitly.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/HousekeepingCoordinator.cs</code-under-test>
/// <docs>operations/workers/housekeeping-arbitration#the-ceiling-survives-a-restart</docs>
[Category("Workers")]
public class HousekeepingPersistedDeferralTests {

  private static readonly DateTimeOffset _origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private static (HousekeepingCoordinator Coordinator, FakeTimeProvider Clock) _build(DateTimeOffset? now = null) {
    var clock = new FakeTimeProvider(now ?? _origin);
    return (new HousekeepingCoordinator(new HousekeepingCoordinator.Settings {
      SettledCooldown = TimeSpan.Zero,
      MaxDeferral = TimeSpan.FromHours(1),
    }, clock), clock);
  }

  private static ServiceBacklog _busy(IReadOnlyDictionary<string, DateTimeOffset>? stored) =>
    new() { UnprocessedInboxRows = 500, ActiveLeasedRows = 1, HousekeepingDeferrals = stored };

  private static ServiceBacklog _quiet(IReadOnlyDictionary<string, DateTimeOffset>? stored) =>
    new() { HousekeepingDeferrals = stored };

  private static Dictionary<string, DateTimeOffset> _stored(string key, DateTimeOffset at) =>
    new(StringComparer.Ordinal) { [key] = at };

  private static readonly Dictionary<string, DateTimeOffset> _nothingStored = new(StringComparer.Ordinal);

  [Test]
  public async Task AfterARestart_TheCeilingRunsFromTheStoredFirstDeferralAsync() {
    // A new process, fifty minutes after an earlier one first deferred the sweep.
    var (coordinator, clock) = _build(_origin + TimeSpan.FromMinutes(50));
    var stored = _stored("maintenance", _origin);

    var stillInside = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy(stored));
    clock.Advance(TimeSpan.FromMinutes(10));
    var atCeiling = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy(stored));

    await Assert.That(stillInside.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceBusy);
    await Assert.That(atCeiling.Granted).IsTrue()
      .Because("the sweep has been held back for an hour across both processes, and the restart is not a pass");
    await Assert.That(atCeiling.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit);
  }

  [Test]
  public async Task TheFirstDeferral_IsReportedForTheStore_AndOnlyOnceItHoldsNoneAsync() {
    var (coordinator, clock) = _build();

    var first = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy(_nothingStored));
    clock.Advance(TimeSpan.FromMinutes(10));
    var second = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy(_stored("maintenance", _origin)));

    await Assert.That(first.DeferralToRecord).IsEqualTo(new HousekeepingCoordinator.DeferralRecord("maintenance", _origin))
      .Because("the store holds no first deferral for the sweep, so this one starts the run of deferrals the next "
        + "process must measure from");
    await Assert.That(second.DeferralToRecord).IsNull()
      .Because("the store already holds the run's first deferral; writing again on every tick would be a write per "
        + "tick for nothing");
  }

  [Test]
  public async Task AStoreThatMissedTheRecord_IsGivenThisProcesssFirstDeferralAgainAsync() {
    var (coordinator, clock) = _build();
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy(_nothingStored));
    clock.Advance(TimeSpan.FromMinutes(10));

    // The write did not land (or a peer's completed pass cleared it): the store still holds nothing.
    var again = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy(_nothingStored));

    await Assert.That(again.DeferralToRecord).IsEqualTo(new HousekeepingCoordinator.DeferralRecord("maintenance", _origin))
      .Because("this process has been holding the sweep back since its first deferral, and that is the moment the "
        + "store should measure from");
  }

  [Test]
  public async Task WithoutTheFigure_NothingIsRecorded_AndTheCeilingIsMeasuredInMemoryAsync() {
    var (coordinator, clock) = _build();

    var first = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy(stored: null));
    clock.Advance(TimeSpan.FromHours(1));
    var atCeiling = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy(stored: null));

    await Assert.That(first.DeferralToRecord).IsNull()
      .Because("a backend that does not hold the figure has nowhere to record it");
    await Assert.That(atCeiling.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("without a store the ceiling is measured in memory, as it always was");
    await Assert.That(atCeiling.DeferralToClear).IsNull();
  }

  [Test]
  public async Task AGrantWhileTheStoreHoldsTheBudget_AsksForItToBeClearedOnceThePassEndsAsync() {
    var (coordinator, _) = _build(_origin + TimeSpan.FromMinutes(5));

    var granted = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _quiet(_stored("maintenance", _origin)));
    coordinator.End(HousekeepingCoordinator.Activity.Maintenance);
    var grantedWithNothingStored = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _quiet(_nothingStored));

    await Assert.That(granted.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.Proceed);
    await Assert.That(granted.DeferralToClear).IsEqualTo("maintenance")
      .Because("a completed pass re-arms the budget, for every instance of the service and for the next process");
    await Assert.That(grantedWithNothingStored.DeferralToClear).IsNull()
      .Because("there is nothing stored to clear, and a delete per pass for nothing would be a write per pass");
  }

  [Test]
  public async Task TheEarlierOfTheStoredAndTheInMemoryFirstDeferral_GovernsAsync() {
    var (coordinator, clock) = _build();
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy(_nothingStored));
    clock.Advance(TimeSpan.FromHours(1));

    // The store holds a LATER first deferral (a peer's pass cleared the earlier one and a peer deferred again).
    var laterStored = coordinator.TryBegin(
      HousekeepingCoordinator.Activity.Maintenance, _busy(_stored("maintenance", _origin + TimeSpan.FromMinutes(30))));

    var (restarted, _) = _build(_origin + TimeSpan.FromMinutes(30));
    var earlierStored = restarted.TryBegin(
      HousekeepingCoordinator.Activity.DeadLetterRecovery, _busy(_stored("dead-letter-recovery", _origin - TimeSpan.FromMinutes(30))));

    await Assert.That(laterStored.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("the store only ever moves the ceiling earlier: a ceiling read from it is never later than the one this "
        + "process measured itself, so a record that is lost or late costs nothing over the in-memory behavior");
    await Assert.That(earlierStored.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("an hour has passed since the stored first deferral, however recently this process started");
  }

  [Test]
  public async Task EachIntegrityRequester_HasItsOwnStoredBudgetAsync() {
    var (coordinator, _) = _build(_origin + TimeSpan.FromHours(1));
    var stored = _stored("integrity:audit", _origin);

    var other = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy(stored), "delivery");
    var audit = coordinator.TryBegin(HousekeepingCoordinator.Activity.Integrity, _busy(stored), "audit");

    await Assert.That(other.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ServiceBusy)
      .Because("the requesters share a slot but not a budget (#2001), in the store as in memory");
    await Assert.That(other.DeferralToRecord)
      .IsEqualTo(new HousekeepingCoordinator.DeferralRecord("integrity:delivery", _origin + TimeSpan.FromHours(1)));
    await Assert.That(audit.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit);
    await Assert.That(audit.DeferralToClear).IsEqualTo("integrity:audit");
  }

  [Test]
  public async Task ARefusalBehindAHigherRank_IsRecordedTooAsync() {
    var (coordinator, _) = _build();
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, backlog: null);

    var refused = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _quiet(_nothingStored));

    await Assert.That(refused.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.HigherPriorityRunning);
    await Assert.That(refused.DeferralToRecord).IsEqualTo(new HousekeepingCoordinator.DeferralRecord("maintenance", _origin))
      .Because("a refusal behind a higher rank spends the budget (#2287), so it starts the stored ceiling as any "
        + "deferral does");
  }

  [Test]
  public async Task AnAskRefusedBecauseThePassIsAlreadyRunning_ChangesNothingStoredAsync() {
    var (coordinator, _) = _build();
    _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _quiet(_nothingStored));

    var overlap = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy(_nothingStored));

    await Assert.That(overlap.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.AlreadyRunning);
    await Assert.That(overlap.DeferralToRecord).IsNull()
      .Because("the activity is running, so nothing is being held back");
    await Assert.That(overlap.DeferralToClear).IsNull();
  }
}
