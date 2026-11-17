// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// #2261: the three derived completion figures, each a documented calculation over base readings, so a team that drops
/// the derived gauges from its telemetry feed computes the same numbers itself. These tests are the formulas' contract:
/// the docs page quotes them, and a change to one fails here.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/IntegrityCompletion.cs</code-under-test>
/// <docs>resilience/stream-integrity#derived-completion-meters</docs>
public class IntegrityCompletionTests {

  // ── verified_percent ─────────────────────────────────────────────────────────────────────────

  [Test]
  [Arguments(0L, 100L, 0.0)]
  [Arguments(1L, 100L, 0.0)]
  [Arguments(51L, 100L, 50.0)]
  [Arguments(101L, 100L, 100.0)]
  [Arguments(5_000L, 100L, 100.0)]
  public async Task VerifiedPercent_IsTheVerifiedSequencesOverTheOriginsLatestAsync(long sealedThrough, long latest, double expected) {
    // sealed_through is exclusive: a seal of 51 verified sequences 1 to 50 of the 100 the origin committed.
    await Assert.That(IntegrityCompletion.VerifiedPercent(sealedThrough, latest)).IsEqualTo(expected);
  }

  [Test]
  public async Task VerifiedPercent_WithNoKnownLatest_IsNoMeasurement_AndAnOriginThatCommittedNothingIsCompleteAsync() {
    await Assert.That(IntegrityCompletion.VerifiedPercent(40, null)).IsNull()
      .Because("a percentage of an unknown total is not a number to report");
    await Assert.That(IntegrityCompletion.VerifiedPercent(0, 0)).IsEqualTo(100.0)
      .Because("every event an origin with no events has committed is verified");
  }

  [Test]
  public async Task Verified_ReportsEachKnownOrigin_AndAllOriginsWeightedByWhatEachCommittedAsync() {
    var a = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    var b = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    var unknown = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    var reading = IntegrityCompletion.Verified([
      new OriginSeal(a, 51, 100), new OriginSeal(b, 1_001, 1_000), new OriginSeal(unknown, 7),
    ]);

    await Assert.That(reading.Reported).IsTrue();
    await Assert.That(reading.Origins.Count).IsEqualTo(2).Because("an origin without a known latest has no percentage");
    await Assert.That(reading.Origins.Single(o => o.OriginServiceId == a).Percent).IsEqualTo(50.0);
    await Assert.That(reading.Origins.Single(o => o.OriginServiceId == b).Percent).IsEqualTo(100.0);
    await Assert.That(Math.Round(reading.AllOrigins!.Value, 9)).IsEqualTo(Math.Round(1_050.0 / 1_100.0 * 100.0, 9))
      .Because("(50 + 1000) of (100 + 1000) committed sequences are verified: the busier origin weighs more");
  }

  [Test]
  public async Task Verified_WithNoOriginKnown_HasNoRollUp_AndOriginsThatCommittedNothingAreCompleteAsync() {
    var none = IntegrityCompletion.Verified([new OriginSeal(Guid.Empty, 9)]);
    var empty = IntegrityCompletion.Verified([new OriginSeal(Guid.Empty, 0, 0)]);

    await Assert.That(none.Origins).IsEmpty();
    await Assert.That(none.AllOrigins).IsNull();
    await Assert.That(empty.AllOrigins).IsEqualTo(100.0);
  }

  // ── repair_progress_percent ──────────────────────────────────────────────────────────────────

  [Test]
  [Arguments(200L, 150L, 75.0)]
  [Arguments(50L, 100L, 200.0)]
  [Arguments(10L, 0L, 0.0)]
  public async Task RepairProgressPercent_IsClosedOverOpenedAsync(long opened, long closed, double expected) {
    // Above 100: more closed than opened in the window, so the backlog shrank.
    await Assert.That(IntegrityCompletion.RepairProgressPercent(opened, closed)).IsEqualTo(expected);
  }

  [Test]
  public async Task RepairProgressPercent_WithNothingOpened_IsNoMeasurementAsync() {
    await Assert.That(IntegrityCompletion.RepairProgressPercent(0, 30)).IsNull()
      .Because("a percentage of nothing is not a number; a window with no openings has no progress to report");
  }

  // ── backlog_eta_seconds ──────────────────────────────────────────────────────────────────────

  [Test]
  public async Task BacklogEtaSeconds_IsTheOpenBacklogOverTheNetCloseRateAsync() {
    // 400 closed and 100 opened in an hour: a net 300 an hour, so 900 open take three hours.
    await Assert.That(IntegrityCompletion.BacklogEtaSeconds(900, opened: 100, closed: 400, TimeSpan.FromHours(1)))
      .IsEqualTo(10_800.0);
  }

  [Test]
  [Arguments(100L, 100L)]
  [Arguments(300L, 100L)]
  public async Task BacklogEtaSeconds_WhenTheBacklogIsFlatOrGrowing_IsNoMeasurementAsync(long opened, long closed) {
    await Assert.That(IntegrityCompletion.BacklogEtaSeconds(900, opened, closed, TimeSpan.FromHours(1))).IsNull()
      .Because("a backlog that is not shrinking has no time to empty, and any number would mislead");
  }

  [Test]
  public async Task BacklogEtaSeconds_WithNothingOpen_IsZeroAsync() {
    await Assert.That(IntegrityCompletion.BacklogEtaSeconds(0, opened: 5, closed: 5, TimeSpan.FromHours(1))).IsEqualTo(0.0)
      .Because("an empty backlog is done now, whatever the rates");
  }

  [Test]
  public async Task RepairableOpenBuckets_SumsEveryStateButUnhealable_AndAnUnreportedBacklogHasNoneAsync() {
    var backlog = new LedgerBacklogSnapshot {
      Reported = true,
      Buckets = [
        new LedgerBacklogCount("orders", LedgerBacklogStates.OPEN, 40),
        new LedgerBacklogCount("orders", LedgerBacklogStates.WAITING, 5),
        new LedgerBacklogCount("orders", LedgerBacklogStates.UNHEALABLE, 300),
        new LedgerBacklogCount("billing", LedgerBacklogStates.EXHAUSTED, 2),
      ],
    };

    await Assert.That(IntegrityCompletion.RepairableOpenBuckets(backlog)).IsEqualTo(47L)
      .Because("an unhealable bucket was closed when it was marked, so it is not left for repair to close");
    await Assert.That(IntegrityCompletion.RepairableOpenBuckets(LedgerBacklogSnapshot.NotReported)).IsNull();
  }
}
