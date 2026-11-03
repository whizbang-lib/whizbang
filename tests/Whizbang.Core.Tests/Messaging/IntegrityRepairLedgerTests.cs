// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S6966:Awaitable method should be used", Justification = "The synchronous API is the unit under test.")]
public class IntegrityRepairLedgerTests {

  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(60);
  private static readonly TimeSpan _backoff = TimeSpan.FromMinutes(5);
  private const int MAX_ATTEMPTS = 3;

  private static IntegrityRepairLedger.DivergenceKey _key(Guid? origin = null, Guid? stream = null) => new(
    origin ?? TrackedGuid.New().Value, "tenant-a", "Contracts.TypeX", stream ?? TrackedGuid.New().Value);

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
  public async Task RepairAttempts_PastCap_AreRefusedForGood_WithNoTerminalCadenceAsync() {
    // #2029: past the cap the ladder used to flatten to a terminal cadence (base x 2^6) and ask again forever, which
    // re-shipped the same events several times a day per bucket. The cap now ends the asking until a change repair
    // can act on resets it (a moved local digest or a deficit that grew), as the durable ledger does.
    var ledger = new IntegrityRepairLedger();
    var key = _key();
    var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    for (var i = 0; i < MAX_ATTEMPTS; i++) {
      await Assert.That(ledger.TryBeginRepair(key, now + TimeSpan.FromHours(i), _backoff, MAX_ATTEMPTS)).IsTrue();
    }
    var lastGrant = now + TimeSpan.FromHours(MAX_ATTEMPTS - 1);
    var terminalWait = _backoff * Math.Pow(2, 6);

    await Assert.That(ledger.TryBeginRepair(key, lastGrant + terminalWait, _backoff, MAX_ATTEMPTS)).IsFalse()
      .Because("the old terminal cadence no longer earns an ask");
    await Assert.That(ledger.TryBeginRepair(key, lastGrant + TimeSpan.FromDays(30), _backoff, MAX_ATTEMPTS)).IsFalse()
      .Because("the cap is the end of asking, not a slower cadence");
  }

  [Test]
  public async Task MarkAnswered_ClearsThePendingAnswer_SoOnlyTheBackoffGovernsTheNextAskAsync() {
    // #2029: a bundle that lands for the bucket answers the request; the answer timeout no longer holds it back.
    var ledger = new IntegrityRepairLedger();
    var key = _key();
    var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    _ = ledger.TryBeginReport(key, 11, 21, 0, 0, now, _cooldown);
    _ = ledger.TryBeginRepair(key, now, _backoff, MAX_ATTEMPTS);
    _ = await ledger.MarkAnswerPendingAsync([key], null, null, now, TimeSpan.FromHours(3));

    await ledger.MarkAnsweredAsync([key], now.AddMinutes(1));

    await Assert.That(ledger.TryBeginRepair(key, now + _backoff, _backoff, MAX_ATTEMPTS)).IsTrue()
      .Because("the answer landed, so only the backoff stands between the bucket and its next ask");
  }

  [Test]
  public async Task TypeLane_StandsDownOnlyForItsOwnOriginAndTenant_AndANullTenantIsTheEmptyOneAsync() {
    var ledger = new IntegrityRepairLedger();
    var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    var origin = TrackedGuid.New().Value;
    var otherOrigin = new IntegrityRepairLedger.DivergenceKey(TrackedGuid.New().Value, null, "Contracts.TypeX", TrackedGuid.New().Value);
    var otherTenant = new IntegrityRepairLedger.DivergenceKey(origin, "tenant-b", "Contracts.TypeX", TrackedGuid.New().Value);
    var emptyTenant = new IntegrityRepairLedger.DivergenceKey(origin, "", "Contracts.TypeY", TrackedGuid.New().Value);
    foreach (var key in new[] { otherOrigin, otherTenant, emptyTenant }) {
      _ = ledger.TryBeginReport(key, 11, 21, 0, 0, now, _cooldown);
    }
    _ = await ledger.MarkAnswerPendingAsync([otherOrigin, otherTenant, emptyTenant], null, null, now, TimeSpan.FromHours(3));

    var lane = ledger.TryBeginRepair(new IntegrityRepairLedger.DivergenceKey(origin, null, "Contracts.TypeX", Guid.Empty), now, _backoff, MAX_ATTEMPTS);
    var nullTenantLane = ledger.TryBeginRepair(new IntegrityRepairLedger.DivergenceKey(origin, null, "Contracts.TypeY", Guid.Empty), now, _backoff, MAX_ATTEMPTS);

    await Assert.That(lane).IsTrue().Because("the pending rows of TypeX belong to another origin or another tenant");
    await Assert.That(nullTenantLane).IsFalse().Because("no tenant and an empty tenant are the same bucket, as the durable ledger keys it");
  }

  [Test]
  public async Task MarkAnswered_ABucketTheLedgerDoesNotTrack_IsNotAddedAsync() {
    var ledger = new IntegrityRepairLedger();

    await ledger.MarkAnsweredAsync([_key()], new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    await Assert.That(ledger.Count).IsEqualTo(0);
  }

  [Test]
  public async Task MarkAnswerPending_OnTheTypeLane_WithAWindow_CoversNoInMemoryRow_WhichCarriesNoWindowAsync() {
    // Per-stream rows here carry no compared window (only the durable store stamps one), and a bounded request cannot
    // be shown to cover an unbounded row, the same rule the durable store applies to its pre-stamp rows.
    var ledger = new IntegrityRepairLedger();
    var origin = TrackedGuid.New().Value;
    var perStream = _key(origin);
    var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    _ = ledger.TryBeginReport(perStream, 11, 21, 0, 0, now, _cooldown);

    var marked = await ledger.MarkAnswerPendingAsync([_key(origin, Guid.Empty)], 0, 100, now, TimeSpan.FromHours(3));

    await Assert.That(marked).IsEqualTo(0);
  }

  [Test]
  public async Task ReportBatch_RecordsEachDeficitsSize_AndASizeOnlyTheBatchCarriesAsync() {
    // The batch carries the deficit size the single-key report cannot; an unknown size keeps the recorded one.
    var ledger = new IntegrityRepairLedger();
    var key = _key();
    var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    _ = await ledger.TryBeginReportBatchAsync([new IntegrityReportObservation(key, 11, 21, 0, 0, true, 3)], now, _cooldown);
    _ = await ledger.TryBeginRepairAsync(key, now, _backoff, maxAttempts: 1);

    _ = await ledger.TryBeginReportBatchAsync([new IntegrityReportObservation(key, 12, 22, 0, 0, true)], now.AddMinutes(1), _cooldown);
    var unknownSize = await ledger.TryBeginRepairAsync(key, now.AddDays(1), _backoff, maxAttempts: 1);
    _ = await ledger.TryBeginReportBatchAsync([new IntegrityReportObservation(key, 13, 23, 0, 0, true, 4)], now.AddDays(1), _cooldown);
    var grown = await ledger.TryBeginRepairAsync(key, now.AddDays(1), _backoff, maxAttempts: 1);

    await Assert.That(unknownSize).IsFalse().Because("a report without a size says nothing about growth");
    await Assert.That(grown).IsTrue().Because("4 is more than the 3 recorded before the unsized report");
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
