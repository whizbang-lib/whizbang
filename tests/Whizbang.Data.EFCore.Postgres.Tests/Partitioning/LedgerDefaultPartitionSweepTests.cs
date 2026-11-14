// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Partitioning;

/// <summary>
/// #2321: once partition maintenance is on, a partitioned ledger is swept by row only in its default partition. On a
/// server where tracing ran before day partitions existed that partition holds weeks of traces, and the sweep was one loop
/// that ran until nothing was left, and deleting the rows never returned the partition's space. Each looping task now
/// stops after <c>ledger_sweep_max_batches</c> batches and says so, and a default partition its sweep has drained is
/// truncated, under a lock taken without waiting and an emptiness check under that lock.
/// </summary>
/// <docs>fundamentals/workers/maintenance-steps#ledger-retention</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/440_LedgerSweepsAreBoundedAndReturnDrainedDefaults.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class LedgerDefaultPartitionSweepTests : EFCoreTestBase {
  private const string TRACES = "SELECT rows_affected, status FROM reap_unbounded_ledgers(2) WHERE task_name = 'purge_old_traces'";
  private const string TRUNCATED = "(drained default partition truncated)";

  [Test]
  public async Task TheTraceSweep_IsBoundedPerCall_AndTheNextCallGoesOnAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, "partition_maintenance_enabled", "true");
    await _settingAsync(conn, "ledger_sweep_max_batches", "2");
    for (var i = 0; i < 7; i++) {
      await _traceAsync(conn, "40 days");
    }
    await _traceAsync(conn, "1 day");

    var (first, firstStatus) = await _sweepAsync(conn);
    var (second, secondStatus) = await _sweepAsync(conn);

    await Assert.That(first).IsEqualTo(4L).Because("two batches of two is the call's limit");
    await Assert.That(firstStatus).IsEqualTo("ok (capped: more past the window, next cycle)");
    await Assert.That(second).IsEqualTo(3L).Because("the next call takes what the first left");
    await Assert.That(secondStatus).IsEqualTo("ok");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_traces_default")).IsEqualTo(1L)
      .Because("a trace inside the window stays");
  }

  [Test]
  public async Task ADrainedDefaultPartition_IsTruncated_AndAnotherIsLeftAloneAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, "partition_maintenance_enabled", "true");
    for (var i = 0; i < 3; i++) {
      await _traceAsync(conn, "40 days");
    }
    var (removed, status) = await _sweepAsync(conn);
    await Assert.That(removed).IsEqualTo(3L);
    await Assert.That(status).IsEqualTo("ok").Because("the statistics have not yet seen the partition empty");
    await _executeAsync(conn, "ANALYZE wh_traces_default");
    await Assert.That(await _scalarAsync(conn, "SELECT pg_relation_size('wh_traces_default')")).IsGreaterThan(0L)
      .Because("the control: deleting the rows left the pages in place");

    // Statistics that say empty are not trusted alone: a row written since is found under the lock.
    await _traceAsync(conn, "1 day");
    var (_, withARow) = await _sweepAsync(conn);
    await Assert.That(withARow).IsEqualTo("ok");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_traces_default")).IsEqualTo(1L);

    await _executeAsync(conn, "DELETE FROM wh_traces_default");
    await _executeAsync(conn, "ANALYZE wh_traces_default");
    var (_, drained) = await _sweepAsync(conn);

    await Assert.That(drained).IsEqualTo("ok " + TRUNCATED);
    await Assert.That(await _scalarAsync(conn, "SELECT pg_relation_size('wh_traces_default')")).IsEqualTo(0L);
    await Assert.That(await _boolAsync(conn, "SELECT _wh_truncate_drained_default_partition(NULL)")).IsFalse();
    await Assert.That(await _boolAsync(conn, "SELECT _wh_truncate_drained_default_partition('wh_settings')")).IsFalse()
      .Because("a relation that is not a default partition is never truncated");
    await Assert.That(await _boolAsync(conn, "SELECT _wh_truncate_drained_default_partition('wh_traces_default')")).IsFalse()
      .Because("a partition with no pages has nothing to give back");
  }

  [Test]
  public async Task ADefaultPartitionAnotherTransactionHolds_IsLeftForALaterCycleAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, "partition_maintenance_enabled", "true");
    await _traceAsync(conn, "40 days");
    await _sweepAsync(conn);
    await _executeAsync(conn, "ANALYZE wh_traces_default");
    await using var reader = await _openAsync();
    await using (var hold = await reader.BeginTransactionAsync()) {
      await _executeAsync(reader, "LOCK TABLE wh_traces_default IN ACCESS SHARE MODE");

      var (_, held) = await _sweepAsync(conn);

      await Assert.That(held).IsEqualTo("ok").Because("the lock is not granted at once, so the sweep does not wait for it");
      await Assert.That(await _scalarAsync(conn, "SELECT pg_relation_size('wh_traces_default')")).IsGreaterThan(0L);
      await hold.RollbackAsync();
    }

    var (_, released) = await _sweepAsync(conn);

    await Assert.That(released).IsEqualTo("ok " + TRUNCATED);
  }

  [Test]
  public async Task UnderDebugMode_NothingIsSwept_AndNoDefaultPartitionIsTruncatedAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, "partition_maintenance_enabled", "true");
    await _traceAsync(conn, "40 days");
    await _executeAsync(conn, "ANALYZE wh_traces_default");
    await _settingAsync(conn, "debug_mode", "true");

    var (removed, status) = await _sweepAsync(conn);

    await Assert.That(removed).IsEqualTo(0L);
    await Assert.That(status).IsEqualTo("skipped (debug_mode=true)");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_traces_default")).IsEqualTo(1L);
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static Task _traceAsync(NpgsqlConnection conn, string age) =>
    _executeAsync(conn,
      "INSERT INTO wh_traces (event_id, recorded_at, route_id, hops) VALUES (gen_random_uuid(), now() - @age::interval, gen_random_uuid(), '[]'::jsonb)",
      ("age", age));

  private static async Task<(long Rows, string Status)> _sweepAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = TRACES;
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetString(1));
  }

  private static Task _settingAsync(NpgsqlConnection conn, string key, string value) =>
    _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES (@k, @v, 'string')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """, ("k", key), ("v", value));

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<bool> _boolAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
