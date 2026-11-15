// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2354: the operational retention sweep removes the rows of its Rolling tables that are past their category's resting
/// period and closed, in bounded batches, and reports what is left. An open row (a restore still pending, restoring or
/// failed) is state and stays whatever its age.
/// </summary>
/// <docs>operations/infrastructure/operational-retention</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/451_OperationalRecordsRollOff.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class OperationalRetentionSweepSqlTests : EFCoreTestBase {
  private const long DAY = 86_400;

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private sealed record Row(string Category, string Table, int Expired, long Remaining, double? OldestAgeSeconds);

  private static async Task<List<Row>> _sweepAsync(NpgsqlConnection conn, string retentionJson, int batch = 1000) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT category, table_name, rows_expired, rows_remaining, oldest_age_seconds
      FROM wh_sweep_operational_retention(NOW(), @r::jsonb, @b)
      """;
    cmd.Parameters.AddWithValue("r", retentionJson);
    cmd.Parameters.AddWithValue("b", batch);
    var rows = new List<Row>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new Row(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetDouble(4)));
    }
    return rows;
  }

  /// <summary>One rewrite-log row per generation, each the given number of days old.</summary>
  private static async Task _seedRewritesAsync(NpgsqlConnection conn, params (long Generation, int DaysOld)[] rows) {
    foreach (var (generation, daysOld) in rows) {
      await _executeAsync(conn, $"""
        INSERT INTO wh_integrity_origin_rewrites (generation, rewritten_from, rewritten_at)
        VALUES ({generation}, 0, NOW() - INTERVAL '{daysOld} days')
        """);
    }
    await _executeAsync(conn, $"""
      UPDATE wh_settings SET setting_value = '{rows.Max(r => r.Generation)}' WHERE setting_key = 'integrity_origin_generation';
      UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'integrity_origin_rewrite_log_from';
      """);
  }

  private static async Task _seedRestoreAsync(NpgsqlConnection conn, string state, int daysOld) {
    var stream = TrackedGuid.New().Value;
    await _executeAsync(conn, $"""
      INSERT INTO wh_stream_lifecycle (stream_id, stream_type, first_appended_at, last_appended_bucket, last_version, state)
      VALUES ('{stream}', 'TestAggregate', NOW(), NOW(), 1, 'Archived');
      INSERT INTO wh_archive_restore_requests (stream_id, state, requested_at, updated_at)
      VALUES ('{stream}', '{state}', NOW() - INTERVAL '{daysOld} days', NOW() - INTERVAL '{daysOld} days');
      """);
  }

  // ── the rewrite log ────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task TheRewriteLog_LosesItsRowsPastTheRetention_AndItsStartMovesWithThemAsync() {
    await using var conn = await _openAsync();
    await _seedRewritesAsync(conn, (1, 40), (2, 20), (3, 5), (4, 1));

    var rows = await _sweepAsync(conn, $$"""{"RewriteLog": {{14 * DAY}}}""");

    var log = rows.Single(r => r.Table == "wh_integrity_origin_rewrites");
    await Assert.That(log.Category).IsEqualTo(nameof(OperationalCategory.RewriteLog));
    await Assert.That(log.Expired).IsEqualTo(2);
    await Assert.That(log.Remaining).IsEqualTo(2L);
    await Assert.That(log.OldestAgeSeconds!.Value).IsGreaterThan(4.9 * DAY).And.IsLessThan(5.1 * DAY);
    await Assert.That(await _scalarAsync(conn,
        "SELECT setting_value::bigint FROM wh_settings WHERE setting_key = 'integrity_origin_rewrite_log_from'"))
      .IsEqualTo(2L).Because("the log no longer reaches back past generation 2, so it must not vouch for one older");
    await Assert.That(await _scalarAsync(conn, "SELECT CASE WHEN vouched THEN 1 ELSE 0 END FROM wh_integrity_history_rewrite(1)"))
      .IsEqualTo(0L).Because("a consumer behind the log is answered as unknown and re-verifies, never vouched for falsely");
    await Assert.That(await _scalarAsync(conn, "SELECT CASE WHEN vouched THEN 1 ELSE 0 END FROM wh_integrity_history_rewrite(2)"))
      .IsEqualTo(1L);
  }

  [Test]
  public async Task TheRewriteLog_IsRemovedOldestGenerationFirst_InBoundedBatchesAsync() {
    await using var conn = await _openAsync();
    await _seedRewritesAsync(conn, (1, 40), (2, 30), (3, 20), (4, 1));

    var first = (await _sweepAsync(conn, $$"""{"RewriteLog": {{14 * DAY}}}""", batch: 2)).Single(r => r.Table == "wh_integrity_origin_rewrites");

    await Assert.That(first.Expired).IsEqualTo(2).Because("a batch never removes more than its size");
    await Assert.That(await _scalarAsync(conn, "SELECT MIN(generation) FROM wh_integrity_origin_rewrites")).IsEqualTo(3L)
      .Because("the log is removed from its oldest end, so what is left is contiguous and the start is exact");

    var second = (await _sweepAsync(conn, $$"""{"RewriteLog": {{14 * DAY}}}""", batch: 2)).Single(r => r.Table == "wh_integrity_origin_rewrites");
    await Assert.That(second.Expired).IsEqualTo(1);
  }

  // ── finished restore requests ──────────────────────────────────────────────────────────────

  [Test]
  public async Task ARestoredRequest_IsRemovedPastTheRetention_AndAnOpenOrFailedOneStaysAsync() {
    await using var conn = await _openAsync();
    await _seedRestoreAsync(conn, "Restored", 30);
    await _seedRestoreAsync(conn, "Restored", 2);
    await _seedRestoreAsync(conn, "Pending", 30);
    await _seedRestoreAsync(conn, "Restoring", 30);
    await _seedRestoreAsync(conn, "Failed", 30);

    var queues = (await _sweepAsync(conn, $$"""{"Queues": {{14 * DAY}}}""")).Single(r => r.Table == "wh_archive_restore_requests");

    await Assert.That(queues.Category).IsEqualTo(nameof(OperationalCategory.Queues));
    await Assert.That(queues.Expired).IsEqualTo(1);
    await Assert.That(queues.Remaining).IsEqualTo(1L).Because("only finished requests are operational records");
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_archive_restore_requests WHERE state <> 'Restored'"))
      .IsEqualTo(3L).Because("Pending and Restoring are work, and a Failed request holds back a restore storm until a restore succeeds");
  }

  // ── no retention: report only ───────────────────────────────────────────────────────────────

  [Test]
  public async Task ACategoryWithNoRetentionGiven_RemovesNothing_ButIsStillReportedAsync() {
    await using var conn = await _openAsync();
    await _seedRewritesAsync(conn, (1, 40));
    await _seedRestoreAsync(conn, "Restored", 40);

    var rows = await _sweepAsync(conn, "{}");

    await Assert.That(rows.Sum(r => r.Expired)).IsEqualTo(0);
    await Assert.That(rows.Single(r => r.Table == "wh_integrity_origin_rewrites").Remaining).IsEqualTo(1L);
    await Assert.That(rows.Single(r => r.Table == "wh_archive_restore_requests").Remaining).IsEqualTo(1L);
  }

  [Test]
  public async Task AnEmptyTable_ReportsNoRows_AndNoAgeAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, "DELETE FROM wh_integrity_origin_rewrites");

    var log = (await _sweepAsync(conn, $$"""{"RewriteLog": {{DAY}}}""")).Single(r => r.Table == "wh_integrity_origin_rewrites");

    await Assert.That(log.Remaining).IsEqualTo(0L);
    await Assert.That(log.OldestAgeSeconds).IsNull();
  }

  // ── the coordinator ────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Coordinator_SweepOperationalRetentionAsync_DrivesTheSqlFunctionAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    await _seedRewritesAsync(conn, (1, 40), (2, 1));
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var results = await coordinator.SweepOperationalRetentionAsync(
      new Dictionary<OperationalCategory, TimeSpan> { [OperationalCategory.RewriteLog] = TimeSpan.FromDays(14) }, batchSize: 100);

    var log = results.Single(r => r.Table == "wh_integrity_origin_rewrites");
    await Assert.That(log.Category).IsEqualTo(OperationalCategory.RewriteLog);
    await Assert.That(log.Expired).IsEqualTo(1);
    await Assert.That(log.Remaining).IsEqualTo(1L);
    await Assert.That(log.OldestAge.HasValue).IsTrue();
  }
}
