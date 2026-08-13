using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Stream-integrity convergence state: the consumer-side memory of what divergence has already
/// been REPORTED (cooldown-suppressed re-reports) and how often a bucket's REPAIR has been
/// requested (exponential backoff, attempt-capped). Without this ledger a persistent divergence
/// — an origin that is down, or a genuinely damaged bucket — re-reports and re-requests on every
/// audit cycle forever, and the integrity machinery itself becomes the storm.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityRepairLedger.cs</code-under-test>
[Category("Messaging")]
public class IntegrityRepairLedgerTests {

  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(60);
  private static readonly TimeSpan _backoff = TimeSpan.FromMinutes(5);
  private const int MAX_ATTEMPTS = 3;

  private static IntegrityRepairLedger.DivergenceKey _key(Guid? origin = null, Guid? stream = null) => new(
    origin ?? TrackedGuid.NewMedo().Value, "tenant-a", "Contracts.TypeX", stream ?? TrackedGuid.NewMedo().Value);

  [Test]
  public async Task FirstSighting_ReportsAndRepairsAsync() {
    var ledger = new IntegrityRepairLedger();
    var key = _key();
    var now = DateTimeOffset.UtcNow;

    await Assert.That(ledger.TryBeginReport(key, 11, 21, 0, 0, now, _cooldown)).IsTrue()
      .Because("a divergence never seen before is always worth naming.");
    await Assert.That(ledger.TryBeginRepair(key, now, _backoff, MAX_ATTEMPTS)).IsTrue()
      .Because("the first repair attempt goes out immediately.");
  }

  [Test]
  public async Task UnchangedDivergence_WithinCooldown_SuppressesReportAsync() {
    var ledger = new IntegrityRepairLedger();
    var key = _key();
    var now = DateTimeOffset.UtcNow;
    ledger.TryBeginReport(key, 11, 21, 0, 0, now, _cooldown);

    await Assert.That(ledger.TryBeginReport(key, 11, 21, 0, 0, now + TimeSpan.FromMinutes(1), _cooldown)).IsFalse()
      .Because("the same divergence re-detected a minute later is the audit cadence, not news — " +
               "re-reporting it every cycle is exactly the outbox flood this ledger exists to stop.");
  }

  [Test]
  public async Task UnchangedDivergence_AfterCooldown_ReportsAgainAsync() {
    var ledger = new IntegrityRepairLedger();
    var key = _key();
    var now = DateTimeOffset.UtcNow;
    ledger.TryBeginReport(key, 11, 21, 0, 0, now, _cooldown);

    await Assert.That(ledger.TryBeginReport(key, 11, 21, 0, 0, now + _cooldown, _cooldown)).IsTrue()
      .Because("a still-unhealed divergence resurfaces once per cooldown so operators keep seeing it.");
  }

  [Test]
  public async Task SignatureChange_ReportsImmediatelyAndResetsAttemptsAsync() {
    var ledger = new IntegrityRepairLedger();
    var key = _key();
    var now = DateTimeOffset.UtcNow;
    ledger.TryBeginReport(key, 11, 21, 0, 0, now, _cooldown);
    for (var i = 0; i < MAX_ATTEMPTS; i++) {
      ledger.TryBeginRepair(key, now + TimeSpan.FromHours(i), _backoff, MAX_ATTEMPTS);
    }
    var later = now + TimeSpan.FromHours(MAX_ATTEMPTS);
    await Assert.That(ledger.TryBeginRepair(key, later, _backoff, MAX_ATTEMPTS)).IsFalse()
      .Because("precondition: the attempt cap is exhausted.");

    await Assert.That(ledger.TryBeginReport(key, 11, 21, 5, 7, later + TimeSpan.FromMinutes(1), _cooldown)).IsTrue()
      .Because("either side's digest moving means progress or fresh damage — report it now, not at the cooldown.");
    await Assert.That(ledger.TryBeginRepair(key, later + TimeSpan.FromMinutes(1), _backoff, MAX_ATTEMPTS)).IsTrue()
      .Because("a changed signature is a NEW situation — the attempt budget starts over.");
  }

  [Test]
  public async Task RepairBackoff_DoublesPerAttemptAsync() {
    var ledger = new IntegrityRepairLedger();
    var key = _key();
    var t0 = DateTimeOffset.UtcNow;
    await Assert.That(ledger.TryBeginRepair(key, t0, _backoff, maxAttempts: 10)).IsTrue();

    await Assert.That(ledger.TryBeginRepair(key, t0 + _backoff - TimeSpan.FromSeconds(1), _backoff, 10)).IsFalse()
      .Because("the second attempt waits out the base backoff.");
    await Assert.That(ledger.TryBeginRepair(key, t0 + _backoff, _backoff, 10)).IsTrue();

    var t2 = t0 + _backoff;
    await Assert.That(ledger.TryBeginRepair(key, t2 + _backoff, _backoff, 10)).IsFalse()
      .Because("after the second attempt the wait DOUBLES — an origin that stays silent is asked " +
               "less and less often, not hammered on every audit.");
    await Assert.That(ledger.TryBeginRepair(key, t2 + _backoff + _backoff, _backoff, 10)).IsTrue();
  }

  [Test]
  public async Task RepairAttempts_CapExhausted_StopsClimbingTheLadderAsync() {
    var ledger = new IntegrityRepairLedger();
    var key = _key();
    var now = DateTimeOffset.UtcNow;
    for (var i = 0; i < MAX_ATTEMPTS; i++) {
      await Assert.That(ledger.TryBeginRepair(key, now + TimeSpan.FromHours(i), _backoff, MAX_ATTEMPTS)).IsTrue();
    }
    var lastGrant = now + TimeSpan.FromHours(MAX_ATTEMPTS - 1);
    var terminalWait = _backoff * Math.Pow(2, 6);

    await Assert.That(ledger.TryBeginRepair(key, lastGrant + terminalWait - TimeSpan.FromSeconds(1), _backoff, MAX_ATTEMPTS)).IsFalse()
      .Because("past the cap the exponential ladder stops climbing — inside the terminal wait the " +
               "requester stays quiet instead of hammering a bucket that has already burned its budget.");
  }

  [Test]
  public async Task RepairAttempts_PastCap_RetriesAtTerminalCadenceAsync() {
    // A bucket that burns its whole attempt budget against an unreachable origin has a STATIC
    // signature — nothing ever changes to reset it — so a permanent deny would shadow-ban a real,
    // repairable deficit forever. Past the cap the ladder flattens to its terminal cadence
    // (base × 2^6) instead of going silent: convergence stays eventually-true at bounded cost.
    var ledger = new IntegrityRepairLedger();
    var key = _key();
    var now = DateTimeOffset.UtcNow;
    for (var i = 0; i < MAX_ATTEMPTS; i++) {
      ledger.TryBeginRepair(key, now + TimeSpan.FromHours(i), _backoff, MAX_ATTEMPTS);
    }
    var lastGrant = now + TimeSpan.FromHours(MAX_ATTEMPTS - 1);
    var terminalWait = _backoff * Math.Pow(2, 6);

    await Assert.That(ledger.TryBeginRepair(key, lastGrant + terminalWait, _backoff, MAX_ATTEMPTS)).IsTrue()
      .Because("once the terminal wait has fully elapsed the bucket earns one more ask — an origin " +
               "that was down while the budget burned is still repairable when it comes back.");
    await Assert.That(ledger.TryBeginRepair(key, lastGrant + terminalWait + TimeSpan.FromMinutes(1), _backoff, MAX_ATTEMPTS)).IsFalse()
      .Because("the terminal grant is a cadence, not a reopened floodgate — the next ask waits out " +
               "another full terminal interval.");
    await Assert.That(ledger.TryBeginRepair(key, lastGrant + terminalWait + terminalWait, _backoff, MAX_ATTEMPTS)).IsTrue()
      .Because("each terminal interval earns exactly one more ask, forever.");
  }

  [Test]
  public async Task MarkHealed_ForgetsTheBucketAsync() {
    var ledger = new IntegrityRepairLedger();
    var key = _key();
    var now = DateTimeOffset.UtcNow;
    ledger.TryBeginReport(key, 11, 21, 0, 0, now, _cooldown);
    ledger.MarkHealed(key);

    await Assert.That(ledger.TryBeginReport(key, 11, 21, 0, 0, now + TimeSpan.FromMinutes(1), _cooldown)).IsTrue()
      .Because("a healed bucket is forgotten — if it diverges again later that is a brand-new incident.");
  }

  [Test]
  public async Task Bounded_EvictsTheOldestEntryAsync() {
    var ledger = new IntegrityRepairLedger(maxEntries: 3);
    var now = DateTimeOffset.UtcNow;
    var oldest = _key();
    ledger.TryBeginReport(oldest, 1, 1, 0, 0, now, _cooldown);
    ledger.TryBeginReport(_key(), 2, 2, 0, 0, now + TimeSpan.FromSeconds(1), _cooldown);
    ledger.TryBeginReport(_key(), 3, 3, 0, 0, now + TimeSpan.FromSeconds(2), _cooldown);

    ledger.TryBeginReport(_key(), 4, 4, 0, 0, now + TimeSpan.FromSeconds(3), _cooldown);

    await Assert.That(ledger.Count).IsEqualTo(3)
      .Because("the ledger is hard-bounded — mass divergence must not become unbounded memory.");
    await Assert.That(ledger.TryBeginReport(oldest, 1, 1, 0, 0, now + TimeSpan.FromSeconds(4), _cooldown)).IsTrue()
      .Because("the evicted (oldest-touched) entry reads as fresh — over-reporting is the safe failure mode.");
  }

  [Test]
  public async Task MarkHealedBatchWithAges_ReturnsOneAgePerKnownKey_AndForgetsThemAsync() {
    // The heal already destroys the row that carried the first-seen clock — reading the age back
    // out of that destruction is the whole point: per-bucket time-to-reconcile at zero extra cost.
    var ledger = new IntegrityRepairLedger();
    var known = _key();
    var unknown = _key();
    var now = DateTimeOffset.UtcNow;
    ledger.TryBeginReport(known, 11, 21, 0, 0, now - TimeSpan.FromMinutes(10), _cooldown);

    var ages = await ledger.MarkHealedBatchWithAgesAsync([known, unknown]);

    await Assert.That(ages.Count).IsEqualTo(1)
      .Because("only a bucket the ledger was tracking has a first-seen clock to report");
    await Assert.That(ages[0]).IsGreaterThanOrEqualTo(0d);
    await Assert.That(ledger.TryBeginReport(known, 11, 21, 0, 0, now, _cooldown)).IsTrue()
      .Because("a healed bucket is forgotten — the same signature later is a brand-new incident");
  }
}
