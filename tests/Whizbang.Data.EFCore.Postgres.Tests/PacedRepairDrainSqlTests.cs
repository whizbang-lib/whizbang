// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The paced-repair-drain substrate (migration 097), against real Postgres: discovery stamps the
/// compared window onto ledger rows, and the drain CLAIMS eligible rows atomically — past
/// backoff, under the attempt cap, least-recently-attempted first, SKIP LOCKED so concurrent
/// drainers never double-dispatch a bucket. The claim stamps the attempt exactly like the proven
/// single-key repair grant, so eligibility semantics cannot drift between the burst path and the
/// drain.
/// </summary>
/// <remarks>
/// #1842: the drain claims only rows a redelivery can repair. The comparison states each divergence's
/// kind when it reports it; identity mismatches, local surpluses and rows whose kind is unknown (written
/// before the kind was recorded, or by a report that did not carry it) stay in the ledger for reporting
/// and are never claimed.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/251_RepairDrainClaimsOnlyDeficits.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>proposals/paced-repair-drain</docs>
[Category("Integration")]
[Category("Shard1")]
public class PacedRepairDrainSqlTests : EFCoreTestBase {

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin, Guid stream) =>
    new(origin, "tenant-a", "Contracts.DrainProbe", stream);

  private static IntegrityReportObservation _obs(IntegrityRepairLedger.DivergenceKey key) =>
    new(key, 11, 21, 0, 0, IsDeficit: true);

  [Test]
  public async Task StampWindows_PersistsTheComparedRange_OnReportedRowsAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var keys = Enumerable.Range(0, 2).Select(_ => _key(origin, Guid.NewGuid())).ToList();
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, keys.ConvertAll(_obs), now, TimeSpan.FromMinutes(60));

    await coordinator.IntegrityStampRepairWindowsAsync(
      origin, keys, windowFrom: 100, windowUntil: 500);

    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);
    await Assert.That(claimed.Count).IsEqualTo(2)
      .Because("both reported buckets are eligible for their first attempt");
    await Assert.That(claimed.All(c => c.WindowFrom == 100 && c.WindowUntil == 500)).IsTrue()
      .Because("the drain dispatches the RANGE the compare disagreed on — the window must ride the row");
  }

  [Test]
  public async Task Claim_StampsTheAttempt_AndBackoffSuppressesTheNextClaimAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var key = _key(origin, Guid.NewGuid());
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [_obs(key)], now, TimeSpan.FromMinutes(60));

    var first = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);
    await Assert.That(first.Count).IsEqualTo(1);

    var insideBackoff = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(2), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);
    await Assert.That(insideBackoff).IsEmpty()
      .Because("a claim IS an attempt — the same exponential ladder the burst path enforces");

    var pastBackoff = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(302), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);
    await Assert.That(pastBackoff.Count).IsEqualTo(1)
      .Because("past the base backoff the bucket re-offers");
  }

  [Test]
  public async Task Claim_RespectsAttemptCap_OriginFilter_AndLimitAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var learned = Guid.NewGuid();
    var unlearned = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var learnedKeys = Enumerable.Range(0, 3).Select(_ => _key(learned, Guid.NewGuid())).ToList();
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      learned, learnedKeys.ConvertAll(_obs), now, TimeSpan.FromMinutes(60));
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      unlearned, [_obs(_key(unlearned, Guid.NewGuid()))], now, TimeSpan.FromMinutes(60));

    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      [learned], now.AddSeconds(1), TimeSpan.FromSeconds(300), maxAttempts: 1, limit: 2);
    await Assert.That(claimed.Count).IsEqualTo(2)
      .Because("the limit bounds one drain pass");
    await Assert.That(claimed.All(c => c.OriginServiceId == learned)).IsTrue()
      .Because("an origin with no learned request topic is never claimed — nothing could be sent");

    var exhausted = await coordinator.IntegrityClaimRepairDrainAsync(
      [learned], now.AddMinutes(10), TimeSpan.FromSeconds(300), maxAttempts: 1, limit: 10);
    await Assert.That(exhausted.Count).IsEqualTo(1)
      .Because("only the never-attempted third row is eligible — the two capped rows hold the "
               + "terminal wait (300s x 2^6), far past the ten-minute mark");
  }

  [Test]
  public async Task Claim_PastCap_RetriesAtTerminalCadenceAsync() {
    // The drain twin of the burst-path terminal cadence: a capped row is not shadow-banned
    // forever — it re-enters the claimable pool once per terminal interval (base x 2^6), so a
    // deficit whose budget burned against a down origin still converges after the origin returns.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var terminal = TimeSpan.FromSeconds(300 * 64);
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [_obs(_key(origin, Guid.NewGuid()))], now, TimeSpan.FromMinutes(60));

    var first = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1), TimeSpan.FromSeconds(300), maxAttempts: 1, limit: 10);
    await Assert.That(first.Count).IsEqualTo(1).Because("precondition: the row's budget is now spent");

    var inside = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1) + terminal - TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(300), maxAttempts: 1, limit: 10);
    await Assert.That(inside.Count).IsEqualTo(0)
      .Because("inside the terminal wait a capped row stays out of the pool");

    var atCadence = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1) + terminal, TimeSpan.FromSeconds(300), maxAttempts: 1, limit: 10);
    await Assert.That(atCadence.Count).IsEqualTo(1)
      .Because("each terminal interval earns the capped row exactly one more claim");

    var rightAfter = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(2) + terminal, TimeSpan.FromSeconds(300), maxAttempts: 1, limit: 10);
    await Assert.That(rightAfter.Count).IsEqualTo(0)
      .Because("the terminal grant is a cadence, not a reopened floodgate");
  }

  [Test]
  public async Task Claim_NeverReturnsTheSyntheticBulkLane_AndOrdersLeastRecentlyAttemptedFirstAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var bulkKey = new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", "Contracts.DrainProbe", Guid.Empty);
    var streamA = _key(origin, Guid.NewGuid());
    var streamB = _key(origin, Guid.NewGuid());
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [_obs(bulkKey), _obs(streamA), _obs(streamB)], now, TimeSpan.FromMinutes(60));

    // streamA gets an early attempt; streamB stays never-attempted — B must come first.
    var first = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1), TimeSpan.FromSeconds(1), maxAttempts: 8, limit: 1);
    await Assert.That(first.Count).IsEqualTo(1);
    var attempted = first[0].StreamId;

    var second = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(3), TimeSpan.FromSeconds(1), maxAttempts: 8, limit: 1);
    await Assert.That(second.Count).IsEqualTo(1);
    await Assert.That(second[0].StreamId).IsNotEqualTo(attempted)
      .Because("least-recently-attempted first — the never-attempted row outranks the just-attempted one");
    await Assert.That(first[0].StreamId).IsNotEqualTo(Guid.Empty);
    await Assert.That(second[0].StreamId).IsNotEqualTo(Guid.Empty)
      .Because("the synthetic bulk lane (stream zero) dispatches through bulk escalation, not the per-stream drain");
  }

  // ── #1842: only deficits are claimed ─────────────────────────────────────

  [Test]
  public async Task Claim_TakesOnlyDeficits_AndLeavesMismatchesAndSurplusesForReportingAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var deficit = _key(origin, Guid.NewGuid());
    var mismatch = _key(origin, Guid.NewGuid());
    var surplus = _key(origin, Guid.NewGuid());
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(origin, [
      _obs(deficit),
      new IntegrityReportObservation(mismatch, 11, 21, 77, 78, IsDeficit: false),
      new IntegrityReportObservation(surplus, 11, 21, 87, 88, IsDeficit: false),
    ], now, TimeSpan.FromMinutes(60));

    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);

    await Assert.That(claimed.Select(c => c.StreamId)).IsEquivalentTo([deficit.StreamId])
      .Because("a redelivery cannot change a mismatch or a surplus, so asking for one is traffic that repairs nothing");
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_integrity_ledger")).IsEqualTo(3L)
      .Because("the mismatch and the surplus stay in the ledger: it is the operator's record of what is divergent");
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_integrity_ledger WHERE repair_attempts > 0")).IsEqualTo(1L)
      .Because("an unclaimed row spends no repair attempt");
  }

  [Test]
  public async Task Claim_ADeficitThatTurnsIntoASurplus_LeavesTheDrain_AndReturnsWhenItIsADeficitAgainAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var key = _key(origin, Guid.NewGuid());
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(origin, [_obs(key)], now, TimeSpan.FromMinutes(60));

    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [new IntegrityReportObservation(key, 11, 21, 91, 92, IsDeficit: false)], now.AddSeconds(1), TimeSpan.FromMinutes(60));
    var whileSurplus = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(2), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);
    await Assert.That(whileSurplus).IsEmpty()
      .Because("the latest sighting decides the kind: a row reported as a surplus is not claimed whatever it was before");

    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [new IntegrityReportObservation(key, 11, 21, 93, 94, IsDeficit: true)], now.AddSeconds(3), TimeSpan.FromMinutes(60));
    var deficitAgain = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(4), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);
    await Assert.That(deficitAgain.Count).IsEqualTo(1)
      .Because("a bucket that is a deficit again is repairable again");
  }

  [Test]
  public async Task Claim_ARowReportedWithoutItsKind_IsNotClaimed_UntilAReportStatesItAsync() {
    // The single-key report (the batch's fallback, and the only path a version that predates the kind
    // has) carries no kind. A row it creates, or whose signature it moves, is of unknown kind; an
    // unchanged signature keeps the kind it had, because the kind is a function of the two event sets.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var unknown = _key(origin, Guid.NewGuid());
    var kept = _key(origin, Guid.NewGuid());
    var moved = _key(origin, Guid.NewGuid());
    var now = DateTimeOffset.UtcNow;
    var cooldown = TimeSpan.FromMinutes(60);
    _ = await coordinator.IntegrityTryBeginReportAsync(unknown, 11, 21, 0, 0, now, cooldown);
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(origin, [_obs(kept), _obs(moved)], now, cooldown);
    _ = await coordinator.IntegrityTryBeginReportAsync(kept, 11, 21, 0, 0, now.AddSeconds(1), cooldown);
    _ = await coordinator.IntegrityTryBeginReportAsync(moved, 11, 21, 95, 96, now.AddSeconds(1), cooldown);

    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(2), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);

    await Assert.That(claimed.Select(c => c.StreamId)).IsEquivalentTo([kept.StreamId])
      .Because("an unknown kind is never claimed: the safe answer when the ledger cannot tell a deficit from a surplus");

    _ = await coordinator.IntegrityTryBeginReportBatchAsync(origin, [_obs(unknown)], now.AddSeconds(3), cooldown);
    var stated = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(4), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);
    await Assert.That(stated.Select(c => c.StreamId)).IsEquivalentTo([unknown.StreamId])
      .Because("the next comparison that states the kind makes the row claimable");
  }

  [Test]
  public async Task Claim_RowsRecordedBeforeKindsWere_AreNotClaimedAsync() {
    // Rows that predate the kind column carry NULL after the upgrade. Their digest halves are hashes,
    // so nothing in the row says whether the bucket is a deficit; the next comparison re-sights it.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [_obs(_key(origin, Guid.NewGuid()))], now, TimeSpan.FromMinutes(60));
    await _execAsync("UPDATE wh_integrity_ledger SET is_deficit = NULL");

    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);

    await Assert.That(claimed).IsEmpty();
  }

  [Test]
  public async Task Claim_ALedgerFullOfSurpluses_ReadsOnlyTheDeficitsThroughThePartialIndexAsync() {
    // The ledger is a hot table and can hold up to its trim ceiling of rows. The claim walks the
    // deficits-only partial index in claim order, so rows the drain must never take are never read.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    await _execAsync($"""
      INSERT INTO wh_integrity_ledger (origin_service_id, tenant_scope, event_type, stream_id,
        origin_lo, origin_hi, local_lo, local_hi, last_reported_at, last_touched, first_seen_at, is_deficit)
      SELECT '{origin}'::uuid, 'tenant-a', 'Contracts.DrainProbe', gen_random_uuid(), 11, 21, n, n, now(), now(), now(), FALSE
      FROM generate_series(1, {SURPLUS_ROWS}) AS n
      """);
    var deficits = Enumerable.Range(0, DEFICIT_ROWS).Select(_ => _key(origin, Guid.NewGuid())).ToList();
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(origin, deficits.ConvertAll(_obs), now, TimeSpan.FromMinutes(60));
    await _execAsync("ANALYZE wh_integrity_ledger");

    var plan = await _planOfOneClaimAsync(origin, now.AddSeconds(1));

    await Assert.That(plan.Claimed).IsEqualTo(DEFICIT_ROWS);
    await Assert.That(plan.Nodes.Any(n => n.Index == "idx_integrity_ledger_repair_drain")).IsTrue()
      .Because("the eligible rows are found through the deficits-only index, in the order the claim takes them");
    await Assert.That(plan.Nodes.Where(n => n.Type == "Seq Scan" && n.Relation == "wh_integrity_ledger")).IsEmpty()
      .Because($"a scan of the table reads every one of the {SURPLUS_ROWS} rows the drain may never claim");
  }

  private const int SURPLUS_ROWS = 20_000;
  private const int DEFICIT_ROWS = 5;

  /// <summary>One claim, rolled back, with the plan of every statement it ran (auto_explain, nested statements).</summary>
  private async Task<ClaimPlan> _planOfOneClaimAsync(Guid origin, DateTimeOffset now) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var notices = new List<string>();
    void Collect(object? _, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
    await _execAsync(conn, """
      LOAD 'auto_explain';
      SET auto_explain.log_min_duration = 0;
      SET auto_explain.log_analyze = on;
      SET auto_explain.log_timing = off;
      SET auto_explain.log_nested_statements = on;
      SET auto_explain.log_format = 'json';
      SET auto_explain.log_level = 'notice';
      """);
    conn.Notice += Collect;
    await _execAsync(conn, "BEGIN");
    long claimed;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT count(*) FROM wh_integrity_claim_repair_drain(ARRAY[@p_origin]::uuid[], @p_now, 300, 8, 100)";
      cmd.Parameters.AddWithValue("p_origin", origin);
      cmd.Parameters.AddWithValue("p_now", now);
      claimed = Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
    await _execAsync(conn, "ROLLBACK");
    conn.Notice -= Collect;
    var nodes = new List<PlanNode>();
    foreach (var notice in notices.Where(n => n.Contains("\"Plan\"", StringComparison.Ordinal))) {
      using var document = JsonDocument.Parse(notice[notice.IndexOf('{', StringComparison.Ordinal)..]);
      _flatten(document.RootElement.GetProperty("Plan"), nodes);
    }
    return new ClaimPlan(claimed, nodes);
  }

  private sealed record ClaimPlan(long Claimed, IReadOnlyList<PlanNode> Nodes);

  private sealed record PlanNode(string Type, string? Relation, string? Index);

  private static void _flatten(JsonElement node, List<PlanNode> nodes) {
    nodes.Add(new PlanNode(
      node.GetProperty("Node Type").GetString()!,
      node.TryGetProperty("Relation Name", out var relation) ? relation.GetString() : null,
      node.TryGetProperty("Index Name", out var index) ? index.GetString() : null));
    if (node.TryGetProperty("Plans", out var children)) {
      foreach (var child in children.EnumerateArray()) {
        _flatten(child, nodes);
      }
    }
  }

  private async Task<long> _scalarAsync(string sql) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private async Task _execAsync(string sql) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _execAsync(conn, sql);
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }
}
