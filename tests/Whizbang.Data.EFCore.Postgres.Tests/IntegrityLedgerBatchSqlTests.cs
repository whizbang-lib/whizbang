using System;
using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The batched ledger entry points (migration 094), against real Postgres. A manifest chunk
/// carries hundreds of buckets, and consulting the ledger per bucket made each comparison up to
/// ~1000 sequential round trips — slower than manifests arrive, which queued arrivals (payloads
/// and all) in memory until the process died. Each batch function loops the proven single-key
/// function INSIDE one call, so these tests pin parity, not new semantics — plus the one genuinely
/// new rule: the repair batch stops CONSULTING once the grant cap is reached, because a grant
/// records an attempt and a discarded grant burns backoff budget for nothing.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[Category("Integration")]
[Category("Shard4")]
public class IntegrityLedgerBatchSqlTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin, Guid stream) =>
    new(origin, "tenant-a", "Contracts.LedgerBatchProbe", stream);

  private static IntegrityReportObservation _obs(IntegrityRepairLedger.DivergenceKey key, long lo = 11, long hi = 21) =>
    new(key, lo, hi, 0, 0);

  [Test]
  public async Task ReportBatch_MatchesSingleSemantics_FirstSightingGrants_CooldownSuppressesAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var keys = Enumerable.Range(0, 3).Select(_ => _key(origin, Guid.NewGuid())).ToList();
    var now = DateTimeOffset.UtcNow;

    var first = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, keys.Select(k => _obs(k)).ToList(), now, TimeSpan.FromMinutes(60));
    await Assert.That(first).IsNotNull();
    await Assert.That(first!.All(granted => granted)).IsTrue()
      .Because("first sighting of every bucket reports — exactly the single-key rule");

    var second = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, keys.Select(k => _obs(k)).ToList(), now.AddMinutes(1), TimeSpan.FromMinutes(60));
    await Assert.That(second!.All(granted => !granted)).IsTrue()
      .Because("an unchanged signature inside the cooldown suppresses — cadence, not news");

    var changed = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, keys.Select(k => _obs(k, lo: 99)).ToList(), now.AddMinutes(2), TimeSpan.FromMinutes(60));
    await Assert.That(changed!.All(granted => granted)).IsTrue()
      .Because("a moved digest is progress or fresh damage — always news, exactly like the single");
  }

  [Test]
  public async Task RepairBatch_GrantsInOrder_UpToTheCap_AndStopsConsultingPastItAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var keys = Enumerable.Range(0, 5).Select(_ => _key(origin, Guid.NewGuid())).ToList();
    var now = DateTimeOffset.UtcNow;
    // Seed ledger rows (repair consults only known divergences).
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, keys.Select(k => _obs(k)).ToList(), now, TimeSpan.FromMinutes(60));

    var flags = await coordinator.IntegrityTryBeginRepairBatchAsync(
      origin, keys, now, TimeSpan.FromSeconds(300), maxAttempts: 8, maxGrants: 2);

    await Assert.That(flags).IsNotNull();
    await Assert.That(flags!.Count(granted => granted)).IsEqualTo(2)
      .Because("the cap is enforced inside the batch, in manifest order");
    await Assert.That(flags[0] && flags[1]).IsTrue();

    // The keys past the cap were never CONSULTED: asking again with a full budget must grant
    // them IMMEDIATELY (first attempt) — a burned attempt would have put them into backoff.
    var again = await coordinator.IntegrityTryBeginRepairBatchAsync(
      origin, keys.Skip(2).ToList(), now.AddSeconds(1), TimeSpan.FromSeconds(300), maxAttempts: 8, maxGrants: 10);
    await Assert.That(again!.All(granted => granted)).IsTrue()
      .Because("past-cap keys must not pay backoff for grants the caller never received");
  }

  [Test]
  public async Task HealedBatch_ForgetsEveryKey_SoALaterDivergenceIsFreshAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var keys = Enumerable.Range(0, 3).Select(_ => _key(origin, Guid.NewGuid())).ToList();
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, keys.Select(k => _obs(k)).ToList(), now, TimeSpan.FromMinutes(60));

    var handled = await coordinator.IntegrityMarkHealedBatchAsync(origin, keys);
    await Assert.That(handled).IsTrue();

    var after = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, keys.Select(k => _obs(k)).ToList(), now.AddMinutes(1), TimeSpan.FromMinutes(60));
    await Assert.That(after!.All(granted => granted)).IsTrue()
      .Because("a healed bucket is forgotten — the same signature minutes later is a brand-new incident");
  }

  [Test]
  public async Task HealedBatchWithAges_ReturnsEachDestroyedRowsAge_AndNothingForUnknownKeysAsync() {
    // The delete was already destroying the rows that carry first_seen_at — the ages are read
    // back out of that destruction (migration 095), not computed by any extra work.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var known = new[] { _key(origin, Guid.NewGuid()), _key(origin, Guid.NewGuid()) };
    var unknown = _key(origin, Guid.NewGuid());
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, known.Select(k => _obs(k)).ToList(), now, TimeSpan.FromMinutes(60));
    await using (var conn = new Npgsql.NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "UPDATE wh_integrity_ledger SET first_seen_at = NOW() - INTERVAL '10 minutes'";
      await cmd.ExecuteNonQueryAsync();
    }

    var ages = await coordinator.IntegrityMarkHealedBatchWithAgesAsync(origin, [.. known, unknown]);

    await Assert.That(ages).IsNotNull();
    await Assert.That(ages!.Count).IsEqualTo(2)
      .Because("two tracked buckets healed; the unknown key had no row and therefore no clock");
    await Assert.That(ages.All(a => a is > 500 and < 700)).IsTrue()
      .Because("each age is read from the destroyed row's ten-minute-old first_seen_at");

    var after = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, known.Select(k => _obs(k)).ToList(), now.AddMinutes(1), TimeSpan.FromMinutes(60));
    await Assert.That(after!.All(granted => granted)).IsTrue()
      .Because("healed buckets are forgotten — the age read must not survive as ledger state");
  }

  [Test]
  public async Task LedgerSummary_CarriesPerOriginSeals_ForTheSealedThroughGaugeAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var originA = Guid.NewGuid();
    var originB = Guid.NewGuid();
    await coordinator.AdvanceIntegritySealAsync(originA, 300);
    await coordinator.AdvanceIntegritySealAsync(originB, 0);

    var summary = await coordinator.GetIntegrityLedgerSummaryAsync(maxRepairAttempts: 8);

    await Assert.That(summary.Seals.Count).IsEqualTo(2)
      .Because("each audited origin is its own gauge series — one number would hide a stuck lane");
    await Assert.That(summary.Seals.Any(x => x.OriginServiceId == originA && x.SealedThrough == 300)).IsTrue();
    await Assert.That(summary.Seals.Any(x => x.OriginServiceId == originB && x.SealedThrough == 0)).IsTrue();
  }
}
