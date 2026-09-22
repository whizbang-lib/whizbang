using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The durable signal tail used to scan <c>wh_signals</c> every two seconds regardless of whether
/// anything ever arrived. On an idle fleet that scan, with the pooled connection's reset behind
/// it, was the single largest source of idle statements per instance. The cadence now starts at
/// the two second floor and doubles on every empty scan up to the notification stack's polling
/// fallback interval, an option that already bounds every other doorbell-less recovery on the
/// host, so no new knob is introduced. No database is needed to prove the shape of the cadence.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDurableSignalTailWorker.cs</code-under-test>
[Category("Shard1")]
public class PgDurableSignalTailWorkerCadenceTests {

  [Test]
  public async Task CreateBackoff_FloorsAtTwoSecondsAndCeilsAtThePollingFallbackAsync() {
    var options = new WhizbangNotificationOptions { PollingFallbackInterval = TimeSpan.FromSeconds(30) };

    var backoff = PgDurableSignalTailWorker.CreateBackoff(options);

    await Assert.That(backoff.Floor).IsEqualTo(PgDurableSignalTailWorker.TickFloor);
    await Assert.That(backoff.Floor).IsEqualTo(TimeSpan.FromSeconds(2))
      .Because("the busy cadence is unchanged: the fast path is NOTIFY and the tail only plugs missed notifies");
    await Assert.That(backoff.Ceiling).IsEqualTo(TimeSpan.FromSeconds(30))
      .Because("an idle instance scans once per polling fallback interval instead of fifteen times");
  }

  [Test]
  public async Task CreateBackoff_IdleScans_ReachTheCeilingInFourStepsAsync() {
    var backoff = PgDurableSignalTailWorker.CreateBackoff(new WhizbangNotificationOptions { PollingFallbackInterval = TimeSpan.FromSeconds(30) });

    var delays = new List<double>();
    for (var i = 0; i < 6; i++) {
      delays.Add(backoff.Next(foundWork: false).TotalSeconds);
    }

    await Assert.That(delays).IsEquivalentTo(new double[] { 2, 4, 8, 16, 30, 30 });
    await Assert.That(backoff.Next(foundWork: true)).IsEqualTo(TimeSpan.FromSeconds(2))
      .Because("a scan that delivers a signal snaps the tail back to the floor");
  }

  [Test]
  public async Task CreateBackoff_PollingFallbackBelowTheFloor_ClampsTheCeilingToTheFloorAsync() {
    // Test fixtures set very short fallback intervals; the tail must not throw or invert its
    // range, it simply never backs off.
    var options = new WhizbangNotificationOptions { PollingFallbackInterval = TimeSpan.FromMilliseconds(500) };

    var backoff = PgDurableSignalTailWorker.CreateBackoff(options);

    await Assert.That(backoff.Ceiling).IsEqualTo(PgDurableSignalTailWorker.TickFloor);
    await Assert.That(backoff.Next(foundWork: false)).IsEqualTo(PgDurableSignalTailWorker.TickFloor);
    await Assert.That(backoff.Current).IsEqualTo(PgDurableSignalTailWorker.TickFloor);
  }

  [Test]
  public async Task CreateBackoff_NullOptions_ThrowsAsync() {
    await Assert.That(() => PgDurableSignalTailWorker.CreateBackoff(null!)).Throws<ArgumentNullException>();
  }
}
