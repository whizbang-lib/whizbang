// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Issue #1572: the maintenance sweep bounds the framework ledgers that had no delete path: <c>wh_log</c>,
/// <c>wh_schedule_runs</c>, the stream-purge audit, and the integrity ledger, whose trim nothing called.
/// </summary>
/// <docs>fundamentals/workers/maintenance-steps#ledger-retention</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/221_LedgerRetention.sql</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class LedgerRetentionSqlTests : EFCoreTestBase {

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<Dictionary<string, (long Rows, string Status)>> _maintainAsync(NpgsqlConnection connection) {
    var rows = await connection.QueryAsync<(string Task, long Rows, double Ms, string Status)>(
      "SELECT task_name, rows_affected, duration_ms, status FROM perform_maintenance()");
    return rows.ToDictionary(r => r.Task, r => (r.Rows, r.Status));
  }

  private static Task _settingAsync(NpgsqlConnection connection, string key, string value) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES (@key, @value, 'integer', 'test') " +
      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value", new { key, value });

  private static Task _logAsync(NpgsqlConnection connection, string message, int ageDays) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_log (logged_at, log_level, source, error_message) VALUES (NOW() - make_interval(days => @ageDays), 2, 'test', @message)",
      new { message, ageDays });

  private static Task _runAsync(NpgsqlConnection connection, Guid schedule, int ageDays) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_schedule_runs (schedule_id, occurrence_id, fired_at, status) VALUES (@schedule, @occurrence, NOW() - make_interval(days => @ageDays), 0)",
      new { schedule, occurrence = Guid.CreateVersion7(), ageDays });

  [Test]
  public async Task Log_OlderThanTheWindow_IsRemoved_YoungerIsKeptAsync() {
    await using var connection = await _openAsync();
    await _logAsync(connection, "old", ageDays: 15);
    await _logAsync(connection, "young", ageDays: 13);

    var results = await _maintainAsync(connection);

    await Assert.That(await connection.QueryAsync<string>("SELECT error_message FROM wh_log WHERE source = 'test'"))
      .IsEquivalentTo(["young"])
      .Because("the default log window is 14 days");
    await Assert.That(results["purge_old_log_entries"].Rows).IsGreaterThanOrEqualTo(1L);
  }

  [Test]
  public async Task Log_ConfiguredWindow_IsHonoredAsync() {
    await using var connection = await _openAsync();
    await _settingAsync(connection, "log_retention_days", "2");
    await _logAsync(connection, "three-days", ageDays: 3);
    await _logAsync(connection, "today", ageDays: 0);

    await _maintainAsync(connection);

    await Assert.That(await connection.QueryAsync<string>("SELECT error_message FROM wh_log WHERE source = 'test'"))
      .IsEquivalentTo(["today"]);
  }

  [Test]
  public async Task ScheduleRuns_OlderThanTheWindow_AreRemoved_ButTheNewestRunOfEachScheduleIsKeptAsync() {
    await using var connection = await _openAsync();
    var busy = Guid.CreateVersion7();
    var dormant = Guid.CreateVersion7();
    await _runAsync(connection, busy, ageDays: 40);
    await _runAsync(connection, busy, ageDays: 1);
    await _runAsync(connection, dormant, ageDays: 60);
    await _runAsync(connection, dormant, ageDays: 50);

    var results = await _maintainAsync(connection);

    await Assert.That(await connection.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_schedule_runs WHERE schedule_id = @busy", new { busy })).IsEqualTo(1L)
      .Because("a run past the 30-day default goes when a newer run of its schedule remains");
    await Assert.That(await connection.ExecuteScalarAsync<int>(
      "SELECT EXTRACT(DAY FROM NOW() - fired_at)::int FROM wh_schedule_runs WHERE schedule_id = @dormant", new { dormant })).IsEqualTo(50)
      .Because("the newest run of every schedule survives any window, so a dormant schedule keeps its last outcome");
    await Assert.That(results["purge_old_schedule_runs"].Rows).IsEqualTo(2L);
  }

  [Test]
  public async Task StreamPurgeAudit_IsKeptForeverByDefault_AndBoundedWhenConfiguredAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(
      "INSERT INTO wh_stream_purge_audit (purge_id, batch_index, requested_by, reason, purged_at, stream_ids, row_counts) " +
      "VALUES (@old, 0, 'ops', 'test', NOW() - INTERVAL '400 days', '{}', '{}'), (@recent, 0, 'ops', 'test', NOW() - INTERVAL '5 days', '{}', '{}')",
      new { old = Guid.CreateVersion7(), recent = Guid.CreateVersion7() });

    await _maintainAsync(connection);
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_stream_purge_audit")).IsEqualTo(2L)
      .Because("the purge audit is a record of what was destroyed, so by default it is never removed");

    await _settingAsync(connection, "stream_purge_audit_retention_days", "30");
    await _maintainAsync(connection);
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_stream_purge_audit")).IsEqualTo(1L)
      .Because("an operator who sets a window opts in to bounding it");
  }

  [Test]
  public async Task IntegrityLedger_IsTrimmedToTheConfiguredCount_NewestKeptAsync() {
    await using var connection = await _openAsync();
    await _settingAsync(connection, "integrity_ledger_max_entries", "2");
    var origin = Guid.CreateVersion7();
    foreach (var age in new[] { 3, 2, 1 }) {
      await connection.ExecuteAsync(
        "INSERT INTO wh_integrity_ledger (origin_service_id, event_type, stream_id, origin_lo, origin_hi, local_lo, local_hi, last_touched) " +
        "VALUES (@origin, @type, @stream, 0, 0, 0, 0, NOW() - make_interval(hours => @age))",
        new { origin, type = $"T{age}", stream = Guid.CreateVersion7(), age });
    }

    var results = await _maintainAsync(connection);

    await Assert.That(await connection.QueryAsync<string>("SELECT event_type FROM wh_integrity_ledger WHERE origin_service_id = @origin", new { origin }))
      .IsEquivalentTo(["T1", "T2"])
      .Because("the trim keeps the configured number of entries, the most recently touched first");
    await Assert.That(results["trim_integrity_ledger"].Rows).IsEqualTo(1L);
  }

  [Test]
  public async Task DebugMode_KeepsEveryLedgerRowAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(
      "INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES ('debug_mode', 'true', 'boolean', 'test') " +
      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = 'true'");
    await _logAsync(connection, "old", ageDays: 30);
    await _runAsync(connection, Guid.CreateVersion7(), ageDays: 90);
    await _runAsync(connection, Guid.Empty, ageDays: 80);

    var results = await _maintainAsync(connection);

    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_log WHERE source = 'test'")).IsEqualTo(1L);
    await Assert.That(results["purge_old_log_entries"].Status).IsEqualTo("skipped (debug_mode=true)");
    await Assert.That(results["purge_old_schedule_runs"].Status).IsEqualTo("skipped (debug_mode=true)");
    await Assert.That(results["trim_integrity_ledger"].Status).IsEqualTo("skipped (debug_mode=true)");
  }
}
