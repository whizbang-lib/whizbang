using System;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Reproduces the session-lock cliff that froze a live fleet's receive side. The SDK renews a
/// session's lock only for <c>MaxAutoLockRenewalDuration</c> (default 5 minutes) — measured from
/// session ACCEPT, not per message. Under a deep backlog a session never goes idle, so every
/// busy session silently outlives its renewal window; the broker lock then lapses mid-stream,
/// every subsequent completion throws <c>SessionLockLost</c>, messages redeliver until they
/// dead-letter, and — because all sessions were accepted together after a deploy — the whole
/// fleet's sessions hit the cliff in the same instant. Observed live: ~300 events/hour applied
/// against 38k queued, ledger frozen for hours, thousands dead-lettered.
///
/// <para>The governor makes rotation an invariant instead of an accident: a session that has
/// been continuously occupied for the occupancy BUDGET (renewal window minus a safety margin
/// that covers the in-flight message's processing + completion) is voluntarily released, so the
/// processor re-accepts it — or another session — under a FRESH lock. FIFO within the session
/// is preserved across the rotation; the budget guarantees completion always happens under a
/// still-renewed lock.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/SessionOccupancyGovernor.cs</code>
public class SessionOccupancyGovernorTests {
  private static readonly TimeSpan _renewalWindow = TimeSpan.FromMinutes(5);

  private static SessionOccupancyGovernor _governor() => new(_renewalWindow);

  [Test]
  public async Task WithinTheBudget_SessionKeepsItsLock_NoReleaseAsync() {
    var g = _governor();
    var t0 = DateTimeOffset.UtcNow;
    g.RecordMessage("s1", t0);

    var mid = t0 + TimeSpan.FromMinutes(2);
    g.RecordMessage("s1", mid);

    await Assert.That(g.ShouldRelease("s1", mid)).IsFalse()
      .Because("rotation below the budget would churn session accepts for no safety gain");
  }

  [Test]
  public async Task ContinuousOccupancy_ReachingTheBudget_ReleasesBeforeTheRenewalCliffAsync() {
    // THE reproduction: a backlog keeps the session busy message after message. Without
    // rotation, occupancy sails past MaxAutoLockRenewalDuration, renewal stops, and the next
    // completion fails with SessionLockLost. The governor must flag release strictly BEFORE
    // the window ends — while the lock is still being renewed.
    var g = _governor();
    var t0 = DateTimeOffset.UtcNow;
    for (var m = 0; m < 60; m++) {
      g.RecordMessage("s1", t0 + TimeSpan.FromSeconds(m * 5));
    }

    var atBudget = t0 + g.OccupancyBudget;
    await Assert.That(g.ShouldRelease("s1", atBudget)).IsTrue()
      .Because("a session that outlives its renewal window is guaranteed to lose its lock "
               + "mid-stream — rotation is the only completion-safe exit");
    await Assert.That(g.OccupancyBudget).IsLessThan(_renewalWindow)
      .Because("the margin must cover the in-flight message's processing + completion, so the "
               + "LAST message before rotation still completes under a renewed lock");
  }

  [Test]
  public async Task ReleasedSession_StartsAFreshClock_OnReacceptAsync() {
    var g = _governor();
    var t0 = DateTimeOffset.UtcNow;
    g.RecordMessage("s1", t0);
    var atBudget = t0 + g.OccupancyBudget;
    await Assert.That(g.ShouldRelease("s1", atBudget)).IsTrue();

    g.OnReleased("s1");
    g.RecordMessage("s1", atBudget + TimeSpan.FromSeconds(1));

    await Assert.That(g.ShouldRelease("s1", atBudget + TimeSpan.FromSeconds(2))).IsFalse()
      .Because("re-accepting the session takes a FRESH broker lock — the occupancy clock "
               + "measures the lock's age, not the stream's history");
  }

  [Test]
  public async Task SessionsAreIndependent_OneCliffDoesNotRotateSiblingsAsync() {
    var g = _governor();
    var t0 = DateTimeOffset.UtcNow;
    g.RecordMessage("old", t0);
    g.RecordMessage("young", t0 + TimeSpan.FromMinutes(4));

    var now = t0 + g.OccupancyBudget;
    await Assert.That(g.ShouldRelease("old", now)).IsTrue();
    await Assert.That(g.ShouldRelease("young", now)).IsFalse()
      .Because("rotation is per-session-lock — releasing healthy young sessions would "
               + "recreate the synchronized churn the governor exists to prevent");
  }

  [Test]
  public async Task InfiniteRenewalWindow_NeverRotatesAsync() {
    var g = new SessionOccupancyGovernor(System.Threading.Timeout.InfiniteTimeSpan);
    var t0 = DateTimeOffset.UtcNow;
    g.RecordMessage("s1", t0);

    await Assert.That(g.ShouldRelease("s1", t0 + TimeSpan.FromDays(1))).IsFalse()
      .Because("an operator who configured unbounded renewal has no cliff to rotate ahead of");
  }

  [Test]
  public async Task VeryShortRenewalWindows_StillLeaveAUsableBudgetAsync() {
    // The margin scales down for short windows instead of consuming them entirely — a 40s
    // window with a fixed 30s margin would leave a 10s budget and thrash accepts.
    var g = new SessionOccupancyGovernor(TimeSpan.FromSeconds(40));

    await Assert.That(g.OccupancyBudget > TimeSpan.FromSeconds(20)).IsTrue()
      .Because("rotation overhead must stay a small fraction of the window, or the cure "
               + "costs more throughput than the disease");
    await Assert.That(g.OccupancyBudget < TimeSpan.FromSeconds(40)).IsTrue();
  }

  [Test]
  public async Task UntrackedSession_NeverReleasesAsync() {
    var g = _governor();

    await Assert.That(g.ShouldRelease("never-seen", DateTimeOffset.UtcNow)).IsFalse()
      .Because("a session with no recorded occupancy has no lock age to rotate on");
  }
}
