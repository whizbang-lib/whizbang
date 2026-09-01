using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The integrity repair ledger's batch surface, driven against the real SQL functions.
/// </summary>
/// <remarks>
/// The ledger is how a consumer remembers that a stream diverged from its origin and how far it
/// has got repairing it. Every method here is a thin wrapper over a plpgsql function, so the
/// contract that matters lives in the round trip rather than in the C#: whether a grant is
/// actually exclusive, whether the cooldown holds across calls, whether a claim is invisible to a
/// concurrent drainer, and whether the returned array lines up positionally with the keys that
/// were sent. A wrapper can look correct and still bind the wrong parameter shape, mis-order the
/// arrays, or silently swallow an error — the C# catches everything and returns null, which reads
/// exactly like "nothing to do".
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/090_IntegrityLedger.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/094_IntegrityLedgerBatch.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/097_PacedRepairDrain.sql</code-under-test>
[Category("Shard3")]
public class IntegrityLedgerSqlTests : EFCoreTestBase {

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin, string eventType = "OrderPlaced") =>
    new(origin, TenantScope: "tenant-a", EventType: eventType, StreamId: (Guid)TrackedGuid.NewMedo());

  private static IntegrityReportObservation _observation(IntegrityRepairLedger.DivergenceKey key) =>
    new(key, OriginLo: 10, OriginHi: 20, LocalLo: 10, LocalHi: 18);

  private static async Task<long> _ledgerRowCountAsync(WorkCoordinationDbContext ctx, Guid origin) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_integrity_ledger WHERE origin_service_id = @o";
    cmd.Parameters.AddWithValue("o", origin);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  // ============================================================
  // Reporting
  // ============================================================

  [Test]
  public async Task TryBeginReportBatch_GrantsEveryFirstSightingAsync() {
    // A bucket nobody has reported yet must always be granted, or the very first divergence
    // report — the one that creates the ledger row — never happens.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var observations = new[] { _observation(_key(origin)), _observation(_key(origin)), _observation(_key(origin)) };

    var grants = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, observations, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    await Assert.That(grants).IsNotNull();
    await Assert.That(grants!.Count).IsEqualTo(3)
      .Because("the result is read positionally against the keys sent — a shorter array silently "
             + "reassigns each verdict to the wrong bucket");
    await Assert.That(grants.All(g => g)).IsTrue();
  }

  [Test]
  public async Task TryBeginReportBatch_RefusesASecondReportInsideTheCooldownAsync() {
    // The cooldown is what stops a persistently diverged stream from reporting on every pass.
    // Without it a single bad stream floods the report path for as long as it stays broken.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var observations = new[] { _observation(_key(origin)) };
    var now = DateTimeOffset.UtcNow;

    var first = await coordinator.IntegrityTryBeginReportBatchAsync(origin, observations, now, TimeSpan.FromMinutes(5));
    var second = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, observations, now.AddMinutes(1), TimeSpan.FromMinutes(5));

    await Assert.That(first![0]).IsTrue();
    await Assert.That(second![0]).IsFalse()
      .Because("re-reporting the same bucket a minute into a five-minute cooldown must be refused");
  }

  [Test]
  public async Task TryBeginReportBatch_GrantsAgainOnceTheCooldownHasElapsedAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var observations = new[] { _observation(_key(origin)) };
    var now = DateTimeOffset.UtcNow;

    _ = await coordinator.IntegrityTryBeginReportBatchAsync(origin, observations, now, TimeSpan.FromMinutes(5));
    var later = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, observations, now.AddMinutes(10), TimeSpan.FromMinutes(5));

    await Assert.That(later![0]).IsTrue()
      .Because("a cooldown that never expires turns a rate limit into a permanent mute");
  }

  [Test]
  public async Task TryBeginReportBatch_CreatesOneLedgerRowPerBucketAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var observations = new[] { _observation(_key(origin)), _observation(_key(origin)) };

    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, observations, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    await Assert.That(await _ledgerRowCountAsync(ctx, origin)).IsEqualTo(2L);
  }

  [Test]
  public async Task TryBeginReportBatch_WithNoObservations_IsHarmlessAsync() {
    // The caller passes whatever the comparison produced, and a clean pass produces nothing.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();

    var grants = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    await Assert.That(grants is null || grants.Count == 0).IsTrue();
    await Assert.That(await _ledgerRowCountAsync(ctx, origin)).IsEqualTo(0L);
  }

  // ============================================================
  // Repair grants
  // ============================================================

  [Test]
  public async Task TryBeginRepairBatch_RespectsTheGrantCeilingAsync() {
    // Repairs are expensive — each one re-reads a window from the origin. The ceiling is the
    // only thing bounding how many a single pass can start, so exceeding it turns a wide
    // divergence into a self-inflicted load spike.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var keys = Enumerable.Range(0, 5).Select(_ => _key(origin)).ToList();
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [.. keys.Select(_observation)], now, TimeSpan.FromMinutes(5));

    var grants = await coordinator.IntegrityTryBeginRepairBatchAsync(
      origin, keys, now, TimeSpan.FromSeconds(30), maxAttempts: 5, maxGrants: 2);

    await Assert.That(grants).IsNotNull();
    await Assert.That(grants!.Count).IsEqualTo(5)
      .Because("every key gets a verdict — the ceiling limits grants, not the array");
    await Assert.That(grants.Count(g => g)).IsEqualTo(2);
  }

  [Test]
  public async Task TryBeginRepairBatch_HoldsOffASecondAttemptDuringBackoffAsync() {
    // A repair that just ran and did not heal the bucket must not immediately re-run; the
    // backoff is what keeps a permanently-unhealable bucket from spinning.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var keys = new List<IntegrityRepairLedger.DivergenceKey> { _key(origin) };
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [.. keys.Select(_observation)], now, TimeSpan.FromMinutes(5));

    var first = await coordinator.IntegrityTryBeginRepairBatchAsync(
      origin, keys, now, TimeSpan.FromMinutes(10), maxAttempts: 5, maxGrants: 10);
    var second = await coordinator.IntegrityTryBeginRepairBatchAsync(
      origin, keys, now.AddSeconds(5), TimeSpan.FromMinutes(10), maxAttempts: 5, maxGrants: 10);

    await Assert.That(first![0]).IsTrue();
    await Assert.That(second![0]).IsFalse();
  }

  [Test]
  public async Task TryBeginRepairBatch_PastTheCap_FlattensToTheTerminalCadenceAsync() {
    // Migration 099 deliberately walked back a hard cap: a bucket that exhausts its attempts is
    // not silenced, it drops to one ask per terminal interval (base x 2^6). The distinction
    // matters because a static deficit — a stream that will never reconcile without an operator —
    // would otherwise be shadow-banned forever, and the ledger would stop showing that it is
    // still trying. So past the cap the answer must be "not yet", never "never again".
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var keys = new List<IntegrityRepairLedger.DivergenceKey> { _key(origin) };
    var baseBackoff = TimeSpan.FromSeconds(10);
    var terminalInterval = TimeSpan.FromSeconds(baseBackoff.TotalSeconds * 64);
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [.. keys.Select(_observation)], now, TimeSpan.FromMinutes(5));

    // Walk well past the cap with the clock far ahead, so every one of these is granted.
    var lastGrantAt = now;
    for (var i = 0; i < 4; i++) {
      lastGrantAt = now.AddHours(i + 1);
      var verdict = await coordinator.IntegrityTryBeginRepairBatchAsync(
        origin, keys, lastGrantAt, baseBackoff, maxAttempts: 2, maxGrants: 10);
      await Assert.That(verdict![0]).IsTrue()
        .Because("an hour is far longer than the terminal cadence, so every ask here is due");
    }

    var tooSoon = await coordinator.IntegrityTryBeginRepairBatchAsync(
      origin, keys, lastGrantAt.Add(terminalInterval / 2), baseBackoff, maxAttempts: 2, maxGrants: 10);
    var due = await coordinator.IntegrityTryBeginRepairBatchAsync(
      origin, keys, lastGrantAt.Add(terminalInterval * 2), baseBackoff, maxAttempts: 2, maxGrants: 10);

    await Assert.That(tooSoon![0]).IsFalse()
      .Because("inside the terminal interval the capped bucket must still be paced");
    await Assert.That(due![0]).IsTrue()
      .Because("past the terminal interval it earns its one ask — a capped bucket is rate-limited, "
             + "not permanently refused");
  }

  // ============================================================
  // Window stamping and the paced drain
  // ============================================================

  [Test]
  public async Task ClaimRepairDrain_ReturnsTheStampedWindowAsync() {
    // The drain re-reads exactly the window the comparison flagged. Losing the stamp is not
    // fatal but it is expensive: dispatch falls back to a coarser per-origin range.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var keys = new List<IntegrityRepairLedger.DivergenceKey> { _key(origin) };
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [.. keys.Select(_observation)], now, TimeSpan.FromMinutes(5));

    await coordinator.IntegrityStampRepairWindowsAsync(origin, keys, windowFrom: 100, windowUntil: 250);
    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddHours(1), TimeSpan.FromSeconds(1), maxAttempts: 5, limit: 10);

    await Assert.That(claimed.Count).IsEqualTo(1);
    await Assert.That(claimed[0].WindowFrom).IsEqualTo(100L);
    await Assert.That(claimed[0].WindowUntil).IsEqualTo(250L);
    await Assert.That(claimed[0].StreamId).IsEqualTo(keys[0].StreamId);
    await Assert.That(claimed[0].EventType).IsEqualTo(keys[0].EventType);
  }

  [Test]
  public async Task ClaimRepairDrain_LeavesTheWindowNullWhenItWasNeverStampedAsync() {
    // Rows written before window stamping existed have no window; the drain has to report that
    // honestly rather than substituting a zero range that would repair nothing.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var keys = new List<IntegrityRepairLedger.DivergenceKey> { _key(origin) };
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [.. keys.Select(_observation)], now, TimeSpan.FromMinutes(5));

    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddHours(1), TimeSpan.FromSeconds(1), maxAttempts: 5, limit: 10);

    await Assert.That(claimed.Count).IsEqualTo(1);
    await Assert.That(claimed[0].WindowFrom).IsNull();
    await Assert.That(claimed[0].WindowUntil).IsNull();
  }

  [Test]
  public async Task ClaimRepairDrain_HonorsTheLimitAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var keys = Enumerable.Range(0, 6).Select(_ => _key(origin)).ToList();
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [.. keys.Select(_observation)], now, TimeSpan.FromMinutes(5));

    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddHours(1), TimeSpan.FromSeconds(1), maxAttempts: 5, limit: 2);

    await Assert.That(claimed.Count).IsEqualTo(2)
      .Because("the limit is the drain's pacing — overrunning it is what the paced drain exists to prevent");
  }

  [Test]
  public async Task ClaimRepairDrain_StampsTheAttemptSoASecondPassSkipsTheClaimAsync() {
    // The claim is exclusive: it stamps the attempt as it hands the row out, so a concurrent
    // drainer cannot pick up the same bucket and repair it twice.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var keys = new List<IntegrityRepairLedger.DivergenceKey> { _key(origin) };
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [.. keys.Select(_observation)], now, TimeSpan.FromMinutes(5));

    var first = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddHours(1), TimeSpan.FromHours(1), maxAttempts: 5, limit: 10);
    var second = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddHours(1), TimeSpan.FromHours(1), maxAttempts: 5, limit: 10);

    await Assert.That(first.Count).IsEqualTo(1);
    await Assert.That(second).IsEmpty()
      .Because("a claimed bucket is in backoff — handing it out again would repair it twice concurrently");
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task ClaimRepairDrain_WithANonPositiveLimit_ClaimsNothingAsync(int limit) {
    // Short-circuited before the round trip: a zero limit is how the drain says "no budget this
    // pass", and it must not stamp attempts on rows it never intends to hand out.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();

    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), maxAttempts: 5, limit: limit);

    await Assert.That(claimed).IsEmpty();
  }

  [Test]
  public async Task ClaimRepairDrain_WithNoOriginIds_ClaimsNothingAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      [], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), maxAttempts: 5, limit: 10);

    await Assert.That(claimed).IsEmpty();
  }

  // ============================================================
  // Healing
  // ============================================================

  [Test]
  public async Task MarkHealedBatch_ForgetsTheBucketsAsync() {
    // Healing deletes the row. A bucket that stays behind keeps consuming repair grants and
    // keeps the unhealed gauge above zero, which reads as an integrity problem that is over.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var keys = Enumerable.Range(0, 3).Select(_ => _key(origin)).ToList();
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [.. keys.Select(_observation)], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    var healed = await coordinator.IntegrityMarkHealedBatchAsync(origin, keys);

    await Assert.That(healed).IsTrue();
    await Assert.That(await _ledgerRowCountAsync(ctx, origin)).IsEqualTo(0L);
  }

  [Test]
  public async Task MarkHealedBatchWithAges_ReturnsAnAgePerHealedBucketAsync() {
    // The age is read from the rows the delete destroys — it is the only chance to measure how
    // long the divergence lived, and it feeds the repair-latency metric.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var keys = Enumerable.Range(0, 2).Select(_ => _key(origin)).ToList();
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [.. keys.Select(_observation)], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    var ages = await coordinator.IntegrityMarkHealedBatchWithAgesAsync(origin, keys);

    await Assert.That(ages).IsNotNull();
    await Assert.That(ages!.Count).IsEqualTo(2);
    await Assert.That(ages.All(a => a >= 0)).IsTrue()
      .Because("a negative age would mean the heal was stamped before the first sighting");
  }

  [Test]
  public async Task MarkHealedBatchWithAges_ForAnUnknownBucket_ReturnsNoAgesAsync() {
    // Healing something never reported is a no-op, not an error: the comparison can find a
    // stream clean that another instance already healed.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();

    var ages = await coordinator.IntegrityMarkHealedBatchWithAgesAsync(origin, [_key(origin)]);

    await Assert.That(ages).IsNotNull();
    await Assert.That(ages!).IsEmpty();
  }

  [Test]
  public async Task MarkHealedBatch_HealsOnlyTheKeysItWasGivenAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var keys = Enumerable.Range(0, 3).Select(_ => _key(origin)).ToList();
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [.. keys.Select(_observation)], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    _ = await coordinator.IntegrityMarkHealedBatchAsync(origin, [keys[0]]);

    await Assert.That(await _ledgerRowCountAsync(ctx, origin)).IsEqualTo(2L)
      .Because("healing one bucket must not forget the two that are still diverged");
  }

  // ============================================================
  // Gauges
  // ============================================================

  [Test]
  public async Task LedgerSummary_CountsUnhealedBucketsAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var keys = Enumerable.Range(0, 4).Select(_ => _key(origin)).ToList();
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [.. keys.Select(_observation)], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    var summary = await coordinator.GetIntegrityLedgerSummaryAsync(maxRepairAttempts: 5);

    await Assert.That(summary.UnhealedBuckets).IsGreaterThanOrEqualTo(4L);
  }

  [Test]
  public async Task LedgerSummary_OnAnEmptyLedger_ReportsZeroAsync() {
    // The gauge has to read zero on a healthy consumer rather than going unreported — an absent
    // series and a zero series look identical on a dashboard until someone alerts on one.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var summary = await coordinator.GetIntegrityLedgerSummaryAsync(maxRepairAttempts: 5);

    await Assert.That(summary.UnhealedBuckets).IsEqualTo(0L);
    await Assert.That(summary.RepairExhausted).IsEqualTo(0L);
  }

  [Test]
  public async Task LedgerSummary_CountsBucketsPastTheAttemptCapAsExhaustedAsync() {
    // Exhausted is the number an operator is meant to act on: buckets the framework has stopped
    // trying to fix. Rolling them into the unhealed count would hide them.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var keys = new List<IntegrityRepairLedger.DivergenceKey> { _key(origin) };
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [.. keys.Select(_observation)], now, TimeSpan.FromMinutes(5));
    for (var i = 0; i < 3; i++) {
      _ = await coordinator.IntegrityTryBeginRepairBatchAsync(
        origin, keys, now.AddHours(i + 1), TimeSpan.FromSeconds(1), maxAttempts: 10, maxGrants: 10);
    }

    // Three attempts recorded, and the gauge is asked to treat two as the ceiling.
    var summary = await coordinator.GetIntegrityLedgerSummaryAsync(maxRepairAttempts: 2);

    await Assert.That(summary.RepairExhausted).IsGreaterThanOrEqualTo(1L);
  }

  // ============================================================
  // Perspective completion
  // ============================================================

  [Test]
  public async Task CompletePerspectiveEvents_WithNoWorkItems_ShortCircuitsAsync() {
    // Guarded before the round trip: an empty batch is the common case on an idle consumer, and
    // calling the function with an empty array every poll is pure round-trip cost.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var completed = await coordinator.CompletePerspectiveEventsAsync([], debugMode: false);

    await Assert.That(completed).IsEqualTo(0);
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task CompletePerspectiveEvents_ForUnknownIds_CompletesNothingAsync(bool debugMode) {
    // Ids that no longer exist are ordinary: another instance completed them, or a sweep removed
    // them. That has to return zero rather than fault the batch the surviving ids are in.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var completed = await coordinator.CompletePerspectiveEventsAsync(
      [(Guid)TrackedGuid.NewMedo(), (Guid)TrackedGuid.NewMedo()], debugMode);

    await Assert.That(completed).IsEqualTo(0);
  }

  // ============================================================
  // The single-key fallbacks
  // ============================================================
  //
  // The batch functions degrade to these one key at a time when a batch call fails — the batch
  // wrappers say so in their own log line. So this is the path that runs precisely when the
  // database is already unhappy, and it had no tests: a divergence discovered during an outage
  // would be reported, repaired and healed entirely through code nothing had exercised.

  [Test]
  public async Task TryBeginReport_GrantsAFirstSightingAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var key = _key(origin);

    var granted = await coordinator.IntegrityTryBeginReportAsync(
      key, originLo: 10, originHi: 20, localLo: 10, localHi: 18,
      DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    await Assert.That(granted).IsTrue();
    await Assert.That(await _ledgerRowCountAsync(ctx, origin)).IsEqualTo(1L);
  }

  [Test]
  public async Task TryBeginReport_RefusesInsideTheCooldownAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var key = _key(origin);
    var now = DateTimeOffset.UtcNow;

    _ = await coordinator.IntegrityTryBeginReportAsync(key, 10, 20, 10, 18, now, TimeSpan.FromMinutes(5));
    var second = await coordinator.IntegrityTryBeginReportAsync(
      key, 10, 20, 10, 18, now.AddMinutes(1), TimeSpan.FromMinutes(5));

    await Assert.That(second).IsFalse()
      .Because("the single-key path has to hold the same rate limit as the batch it stands in for");
  }

  [Test]
  public async Task TryBeginRepair_GrantsThenHoldsOffAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var key = _key(origin);
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportAsync(key, 10, 20, 10, 18, now, TimeSpan.FromMinutes(5));

    var first = await coordinator.IntegrityTryBeginRepairAsync(key, now, TimeSpan.FromMinutes(10), maxAttempts: 5);
    var second = await coordinator.IntegrityTryBeginRepairAsync(
      key, now.AddSeconds(5), TimeSpan.FromMinutes(10), maxAttempts: 5);

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsFalse();
  }

  [Test]
  public async Task MarkHealed_ForgetsTheBucketAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var key = _key(origin);
    _ = await coordinator.IntegrityTryBeginReportAsync(
      key, 10, 20, 10, 18, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    await coordinator.IntegrityMarkHealedAsync(key);

    await Assert.That(await _ledgerRowCountAsync(ctx, origin)).IsEqualTo(0L);
  }

  [Test]
  public async Task MarkHealed_ForAnUnknownBucket_IsANoOpAsync() {
    // Healing something never reported is ordinary: another instance may have healed it first.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();

    await coordinator.IntegrityMarkHealedAsync(_key(origin));

    await Assert.That(await _ledgerRowCountAsync(ctx, origin)).IsEqualTo(0L);
  }

  [Test]
  public async Task SingleKey_TreatsANullTenantScopeAsTheEmptyScopeAsync() {
    // The SQL coalesces a null scope to the empty string, so the C# has to bind the same way or
    // a null-scoped key would report under one identity and heal under another — leaving a
    // bucket that can never be cleared.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var nullScoped = new IntegrityRepairLedger.DivergenceKey(
      origin, TenantScope: null, EventType: "OrderPlaced", StreamId: (Guid)TrackedGuid.NewMedo());

    _ = await coordinator.IntegrityTryBeginReportAsync(
      nullScoped, 10, 20, 10, 18, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    await Assert.That(await _ledgerRowCountAsync(ctx, origin)).IsEqualTo(1L);

    await coordinator.IntegrityMarkHealedAsync(nullScoped);

    await Assert.That(await _ledgerRowCountAsync(ctx, origin)).IsEqualTo(0L)
      .Because("reporting and healing must agree on the identity of a null-scoped key, or the "
             + "bucket can never be cleared");
  }

  [Test]
  public async Task SingleKeyAndBatch_ShareOneLedgerRowAsync() {
    // The single-key path is a fallback for the batch, so the two must address the same row —
    // otherwise a chunk that degraded mid-flight would double-report the same divergence.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var key = _key(origin);
    var now = DateTimeOffset.UtcNow;

    _ = await coordinator.IntegrityTryBeginReportAsync(key, 10, 20, 10, 18, now, TimeSpan.FromMinutes(5));
    var batchAgain = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [_observation(key)], now.AddMinutes(1), TimeSpan.FromMinutes(5));

    await Assert.That(await _ledgerRowCountAsync(ctx, origin)).IsEqualTo(1L);
    await Assert.That(batchAgain![0]).IsFalse()
      .Because("the batch must see the cooldown the single-key call just set on the same row");
  }

  [Test]
  public async Task BatchHealing_ClearsWhatTheSingleKeyPathReportedAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = (Guid)TrackedGuid.NewMedo();
    var key = _key(origin);
    _ = await coordinator.IntegrityTryBeginReportAsync(
      key, 10, 20, 10, 18, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    _ = await coordinator.IntegrityMarkHealedBatchAsync(origin, [key]);

    await Assert.That(await _ledgerRowCountAsync(ctx, origin)).IsEqualTo(0L);
  }
}
