using System;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the convergence contract every <see cref="IIntegrityRepairLedger"/> must honour.
///
/// <para>
/// The ledger decides whether a divergence is reported again. Get it wrong in the permissive
/// direction and a persistent divergence re-reports on every cycle: observed live as 260,602
/// undelivered report messages across twelve databases, enough to saturate a shared server, which
/// restarted the pods, which cleared the in-memory ledger, which re-reported everything. The state
/// that bounds the storm was being erased by the storm.
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public class IntegrityLedgerContractTests {

  private static IntegrityRepairLedger.DivergenceKey _key(Guid? stream = null) =>
    new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "tenant-a",
        "Some.Event, Some.Assembly", stream ?? Guid.Parse("22222222-2222-2222-2222-222222222222"));

  [Test]
  public async Task UnchangedDivergence_InsideCooldown_IsNotReportedAgainAsync() {
    var ledger = new IntegrityRepairLedger();
    var t0 = DateTimeOffset.UtcNow;
    var cooldown = TimeSpan.FromMinutes(60);

    await Assert.That(await ledger.TryBeginReportAsync(_key(), 1, 2, 3, 4, t0, cooldown)).IsTrue()
      .Because("first sighting is always news");

    await Assert.That(await ledger.TryBeginReportAsync(_key(), 1, 2, 3, 4, t0.AddMinutes(30), cooldown)).IsFalse()
      .Because("the same unhealed divergence inside the cooldown is cadence, not news — reporting "
               + "it again is what produced a quarter-million undelivered messages");

    await Assert.That(await ledger.TryBeginReportAsync(_key(), 1, 2, 3, 4, t0.AddMinutes(61), cooldown)).IsTrue()
      .Because("past the cooldown a still-unhealed divergence is worth restating");
  }

  [Test]
  public async Task ChangedSignature_ReportsImmediatelyAndReopensTheRepairBudgetAsync() {
    var ledger = new IntegrityRepairLedger();
    var t0 = DateTimeOffset.UtcNow;
    var cooldown = TimeSpan.FromMinutes(60);
    var backoff = TimeSpan.FromSeconds(300);

    _ = await ledger.TryBeginReportAsync(_key(), 1, 2, 3, 4, t0, cooldown);
    // Space the attempts a day apart. The ladder tops out at base x 2^6 (about 5.3h here), so
    // 24h gaps clear every rung and all eight attempts actually record — with tighter spacing the
    // later calls are refused by the backoff and the budget is never truly spent.
    var recorded = 0;
    for (var i = 0; i < 8; i++) {
      if (await ledger.TryBeginRepairAsync(_key(), t0.AddDays(i), backoff, maxAttempts: 8)) {
        recorded++;
      }
    }
    await Assert.That(recorded).IsEqualTo(8).Because("the budget must actually be spent for the next assertion to mean anything");
    await Assert.That(await ledger.TryBeginRepairAsync(_key(), t0.AddDays(30), backoff, maxAttempts: 8)).IsFalse()
      .Because("a bucket that has exhausted its attempts must stop asking, or repair becomes its own storm");

    // Either side's digest moving means real movement — progress or fresh damage.
    await Assert.That(await ledger.TryBeginReportAsync(_key(), 9, 9, 3, 4, t0.AddDays(30), cooldown)).IsTrue()
      .Because("a changed signature is a new incident and must be reported without waiting out the cooldown");
    await Assert.That(await ledger.TryBeginRepairAsync(_key(), t0.AddDays(30), backoff, maxAttempts: 8)).IsTrue()
      .Because("a new incident reopens the repair budget; otherwise a bucket that breaks again is never fixed");
  }

  [Test]
  public async Task HealedBucket_IsForgottenSoALaterDivergenceIsFreshAsync() {
    var ledger = new IntegrityRepairLedger();
    var t0 = DateTimeOffset.UtcNow;
    var cooldown = TimeSpan.FromMinutes(60);

    _ = await ledger.TryBeginReportAsync(_key(), 1, 2, 3, 4, t0, cooldown);
    await ledger.MarkHealedAsync(_key());

    await Assert.That(await ledger.TryBeginReportAsync(_key(), 1, 2, 3, 4, t0.AddMinutes(1), cooldown)).IsTrue()
      .Because("a bucket that folded identical and later diverges again is a brand-new incident");
  }

  [Test]
  public async Task DistinctBuckets_AreTrackedIndependentlyAsync() {
    var ledger = new IntegrityRepairLedger();
    var t0 = DateTimeOffset.UtcNow;
    var cooldown = TimeSpan.FromMinutes(60);
    var a = _key(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    var b = _key(Guid.Parse("44444444-4444-4444-4444-444444444444"));

    await Assert.That(await ledger.TryBeginReportAsync(a, 1, 2, 3, 4, t0, cooldown)).IsTrue();
    await Assert.That(await ledger.TryBeginReportAsync(b, 1, 2, 3, 4, t0, cooldown)).IsTrue()
      .Because("the key is the identity of a divergence — a different stream is a different incident");
    await Assert.That(await ledger.TryBeginReportAsync(a, 1, 2, 3, 4, t0.AddMinutes(1), cooldown)).IsFalse()
      .Because("and suppression must still apply per bucket");
  }
}
