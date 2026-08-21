using System;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the durable stream-integrity ledger (migration 090), against real Postgres.
///
/// <para>
/// The in-memory ledger this replaces was per-process and died on restart. That was sound only
/// while restarts were rare: in practice the report storm saturated a shared database, the
/// saturation restarted the pods, and the restart erased the state that would have suppressed the
/// storm. Observed live as 260,602 undelivered report messages across twelve databases. It was
/// also per-replica, so N pods asked about the same divergence N times.
/// </para>
///
/// <para>
/// These tests drive the SQL functions directly rather than a fake, because "the durable path is
/// wired and actually works" is the whole claim — a delegating class that silently falls back to
/// memory would satisfy any mock and none of the requirement.
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[Category("Shard4")]
public class IntegrityLedgerSqlTests : EFCoreTestBase {
  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static IntegrityRepairLedger.DivergenceKey _key(Guid? stream = null) =>
    new(Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111"), "tenant-a",
        "Some.Event, Some.Assembly", stream ?? Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222"));

  [Test]
  public async Task Report_FirstSighting_ThenSuppressedInsideCooldown_ThenAllowedAfterAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var key = _key();
    var t0 = DateTimeOffset.UtcNow;
    var cooldown = TimeSpan.FromMinutes(60);

    await Assert.That(await coordinator.IntegrityTryBeginReportAsync(key, 1, 2, 3, 4, t0, cooldown))
      .IsTrue().Because("first sighting of a divergence is always news");

    await Assert.That(await coordinator.IntegrityTryBeginReportAsync(key, 1, 2, 3, 4, t0.AddMinutes(30), cooldown))
      .IsFalse().Because("the same unhealed divergence inside the cooldown is cadence, not news — "
                         + "re-reporting it every cycle is what produced a quarter-million messages");

    await Assert.That(await coordinator.IntegrityTryBeginReportAsync(key, 1, 2, 3, 4, t0.AddMinutes(61), cooldown))
      .IsTrue().Because("past the cooldown a still-unhealed divergence is worth restating");
  }

  [Test]
  public async Task Report_SuppressionSurvivesANewCoordinator_AsARestartWouldAsync() {
    // The property the in-memory ledger could not provide, and the reason the storm was
    // self-sustaining: a fresh process must not re-report what a previous one already reported.
    var key = _key(Guid.Parse("cccccccc-3333-3333-3333-333333333333"));
    var t0 = DateTimeOffset.UtcNow;
    var cooldown = TimeSpan.FromMinutes(60);

    await using (var ctx1 = CreateDbContext()) {
      await Assert.That(await _coordinator(ctx1).IntegrityTryBeginReportAsync(key, 1, 2, 3, 4, t0, cooldown))
        .IsTrue();
    }

    // A brand-new coordinator over a brand-new context — the closest this can get to a restart.
    await using var ctx2 = CreateDbContext();
    await Assert.That(await _coordinator(ctx2).IntegrityTryBeginReportAsync(key, 1, 2, 3, 4, t0.AddMinutes(5), cooldown))
      .IsFalse().Because("state that dies with the process is state the storm erases by restarting it");
  }

  [Test]
  public async Task Report_ChangedSignature_ReportsImmediatelyAndReopensRepairAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var key = _key(Guid.Parse("dddddddd-4444-4444-4444-444444444444"));
    var t0 = DateTimeOffset.UtcNow;
    var cooldown = TimeSpan.FromMinutes(60);
    var backoff = TimeSpan.FromSeconds(300);

    _ = await coordinator.IntegrityTryBeginReportAsync(key, 1, 2, 3, 4, t0, cooldown);
    await Assert.That(await coordinator.IntegrityTryBeginRepairAsync(key, t0, backoff, maxAttempts: 1))
      .IsTrue().Because("the first repair attempt goes immediately");
    await Assert.That(await coordinator.IntegrityTryBeginRepairAsync(key, t0.AddHours(1), backoff, maxAttempts: 1))
      .IsFalse().Because("past the attempt cap the ladder holds the terminal wait (base x 2^6 = 5.3h "
                         + "here) — inside it the requester stays quiet, or repair becomes its own storm");

    // Either side's digest moving is real movement — progress, or fresh damage.
    await Assert.That(await coordinator.IntegrityTryBeginReportAsync(key, 9, 9, 3, 4, t0.AddMinutes(1), cooldown))
      .IsTrue().Because("a changed signature is a new incident and must not wait out the cooldown");
    await Assert.That(await coordinator.IntegrityTryBeginRepairAsync(key, t0.AddMinutes(2), backoff, maxAttempts: 1))
      .IsTrue().Because("a new incident reopens the repair budget; otherwise a bucket that breaks "
                        + "again can never be fixed");
  }

  [Test]
  public async Task Repair_BacksOffExponentiallyBetweenAttemptsAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var key = _key(Guid.Parse("eeeeeeee-5555-5555-5555-555555555555"));
    var t0 = DateTimeOffset.UtcNow;
    var backoff = TimeSpan.FromSeconds(300);   // 5 minutes

    await Assert.That(await coordinator.IntegrityTryBeginRepairAsync(key, t0, backoff, maxAttempts: 8)).IsTrue();
    await Assert.That(await coordinator.IntegrityTryBeginRepairAsync(key, t0.AddMinutes(1), backoff, maxAttempts: 8))
      .IsFalse().Because("a second attempt one minute after the first is inside the 5-minute base wait");
    await Assert.That(await coordinator.IntegrityTryBeginRepairAsync(key, t0.AddMinutes(6), backoff, maxAttempts: 8))
      .IsTrue().Because("past the base wait the next attempt is allowed");
  }

  [Test]
  public async Task Repair_PastCap_RetriesAtTerminalCadenceAsync() {
    // A bucket that burns its budget against an unreachable origin has a STATIC signature —
    // the origin served nothing, so no digest ever moves and the signature-change reset never
    // fires. Observed live: a whole-type backfill lane capped out against a scaled-to-zero
    // origin and stayed shadow-banned after the origin returned, freezing a real deficit until
    // an operator reset the row by hand. Past the cap the ladder flattens to its terminal
    // cadence (base x 2^6) instead of going silent.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var key = _key(Guid.Parse("abababab-7777-7777-7777-777777777777"));
    var t0 = DateTimeOffset.UtcNow;
    var backoff = TimeSpan.FromSeconds(300);            // terminal wait = 300s x 2^6 = 5h20m
    var terminal = TimeSpan.FromSeconds(300 * 64);

    await Assert.That(await coordinator.IntegrityTryBeginRepairAsync(key, t0, backoff, maxAttempts: 1)).IsTrue();

    await Assert.That(await coordinator.IntegrityTryBeginRepairAsync(key, t0 + terminal - TimeSpan.FromMinutes(1), backoff, maxAttempts: 1))
      .IsFalse().Because("inside the terminal wait a capped bucket stays quiet");
    await Assert.That(await coordinator.IntegrityTryBeginRepairAsync(key, t0 + terminal, backoff, maxAttempts: 1))
      .IsTrue().Because("a capped bucket earns one more ask per terminal interval — an origin that "
                        + "was down while the budget burned is still repairable when it comes back");
    await Assert.That(await coordinator.IntegrityTryBeginRepairAsync(key, t0 + terminal + TimeSpan.FromMinutes(1), backoff, maxAttempts: 1))
      .IsFalse().Because("the terminal grant is a cadence, not a reopened floodgate");
    await Assert.That(await coordinator.IntegrityTryBeginRepairAsync(key, t0 + terminal + terminal, backoff, maxAttempts: 1))
      .IsTrue().Because("each terminal interval earns exactly one more ask, forever");
  }

  [Test]
  public async Task MarkHealed_ForgetsTheBucketSoALaterDivergenceIsFreshAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var key = _key(Guid.Parse("ffffffff-6666-6666-6666-666666666666"));
    var t0 = DateTimeOffset.UtcNow;
    var cooldown = TimeSpan.FromMinutes(60);

    _ = await coordinator.IntegrityTryBeginReportAsync(key, 1, 2, 3, 4, t0, cooldown);
    await coordinator.IntegrityMarkHealedAsync(key);

    await Assert.That(await coordinator.IntegrityTryBeginReportAsync(key, 1, 2, 3, 4, t0.AddMinutes(1), cooldown))
      .IsTrue().Because("a bucket that folded identical and later diverges again is a brand-new incident");
  }

  [Test]
  public async Task DistinctBuckets_AreTrackedIndependentlyAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var a = _key(Guid.Parse("11111111-7777-7777-7777-777777777777"));
    var b = _key(Guid.Parse("22222222-8888-8888-8888-888888888888"));
    var t0 = DateTimeOffset.UtcNow;
    var cooldown = TimeSpan.FromMinutes(60);

    await Assert.That(await coordinator.IntegrityTryBeginReportAsync(a, 1, 2, 3, 4, t0, cooldown)).IsTrue();
    await Assert.That(await coordinator.IntegrityTryBeginReportAsync(b, 1, 2, 3, 4, t0, cooldown))
      .IsTrue().Because("the key is the identity of a divergence — a different stream is a different incident");
    await Assert.That(await coordinator.IntegrityTryBeginReportAsync(a, 1, 2, 3, 4, t0.AddMinutes(1), cooldown))
      .IsFalse().Because("and suppression still applies per bucket");
  }

  /// <summary>
  /// The summary is what an operator watches instead of a stream of report events, so it has to
  /// move in both directions: up as buckets diverge, down as they heal. A number that only rises
  /// is the counter this replaces.
  /// </summary>
  [Test]
  public async Task LedgerSummary_CountsUnhealedAndExhausted_AndFallsWhenHealedAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var t0 = DateTimeOffset.UtcNow;
    var cooldown = TimeSpan.FromMinutes(60);
    var backoff = TimeSpan.FromSeconds(1);
    var healthy = _key(Guid.Parse("aaaa1111-0000-0000-0000-00000000aaaa"));
    var stuck = _key(Guid.Parse("bbbb2222-0000-0000-0000-00000000bbbb"));

    _ = await coordinator.IntegrityTryBeginReportAsync(healthy, 1, 2, 3, 4, t0, cooldown);
    _ = await coordinator.IntegrityTryBeginReportAsync(stuck, 1, 2, 3, 4, t0, cooldown);

    // Spend the second bucket's repair budget (cap 1: the first attempt lands, the next is refused).
    _ = await coordinator.IntegrityTryBeginRepairAsync(stuck, t0, backoff, maxAttempts: 1);

    var summary = await coordinator.GetIntegrityLedgerSummaryAsync(maxRepairAttempts: 1);
    await Assert.That(summary.UnhealedBuckets).IsGreaterThanOrEqualTo(2)
      .Because("both divergent buckets are unhealed and must be counted");
    await Assert.That(summary.RepairExhausted).IsGreaterThanOrEqualTo(1)
      .Because("a bucket that has spent its budget has stopped asking — that is the set needing a human");
    await Assert.That(summary.OldestUnhealedAgeSeconds).IsGreaterThanOrEqualTo(0)
      .Because("age comes from first_seen_at, which is stamped on insert");

    var before = summary.UnhealedBuckets;
    await coordinator.IntegrityMarkHealedAsync(healthy);
    var after = await coordinator.GetIntegrityLedgerSummaryAsync(maxRepairAttempts: 1);

    await Assert.That(after.UnhealedBuckets).IsEqualTo(before - 1)
      .Because("healing DELETEs the row, so the gauge falls on its own — the property that makes "
               + "this a usable replacement for publishing an event per sighting");
  }
}
