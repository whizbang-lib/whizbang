// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1611 on a database the Dapper driver migrated: <c>wh_log</c> converts to weekly partitions, its writers keep
/// inserting, the manager creates partitions ahead, and a partition past the window is dropped. The EF Core twins
/// (<c>PartitionMaintainSqlTests</c>, <c>LogPartitionConversionTests</c>) cover each path. Issue #1646: so do
/// <c>wh_signals</c>, daily by <c>created_at</c>, and <c>wh_lifecycle_completions</c>, daily by <c>event_id</c>
/// (twins: <c>SignalPartitionConversionTests</c>, <c>LifecycleCompletionPartitionConversionTests</c>).
/// </summary>
/// <docs>operations/infrastructure/partitioned-ledgers</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/226_PartitionManager.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/233_PartitionSignalsAndLifecycleCompletions.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/237_PartitionsCarryTheLedgersAutovacuumSettings.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/238_LogSweepClearsTheDefaultPartition.sql</code-under-test>
[NotInParallel("PostgreSQL")]
[Category("Integration")]
public class DapperPartitionManagerSqlTests : PostgresTestBase {

  [Test]
  public async Task TheLog_Converts_KeepsItsWriters_AndIsMaintainedByPartitionAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(
      "INSERT INTO wh_log (logged_at, log_level, source, error_message) VALUES (NOW() - INTERVAL '20 days', 2, 'test', 'old')");

    var outcome = await connection.ExecuteScalarAsync<string>(
      "SELECT wh_partition_convert('wh_log', 'logged_at', '7 days')");
    await connection.ExecuteAsync("SELECT log_event(3, 'test', 'after-conversion')");
    var created = (await connection.QueryAsync<string>(
      "SELECT partition_name FROM wh_partition_maintain('wh_log', '7 days', 3, '14 days') WHERE action = 'created'")).ToList();
    var dropped = (await connection.QueryAsync<string>(
      "SELECT partition_name FROM wh_partition_maintain('wh_log', '7 days', 3, '14 days', NOW() + INTERVAL '30 days') " +
      "WHERE action = 'dropped'")).ToList();

    await Assert.That(outcome).IsEqualTo("converted");
    await Assert.That(created.Count).IsEqualTo(3)
      .Because("the legacy partition covers the current week, so the three weeks after it are created");
    await Assert.That(dropped).Contains("wh_log_legacy");
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_log WHERE source = 'test'"))
      .IsEqualTo(0L);
  }

  [Test]
  public async Task TheSignals_Convert_KeepTheirWriters_AndAreMaintainedByPartitionAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync("INSERT INTO wh_signals (wire_name, created_at) VALUES ('old', NOW() - INTERVAL '20 days')");

    var outcome = await connection.ExecuteScalarAsync<string>(
      "SELECT wh_partition_convert('wh_signals', 'created_at', '1 day')");
    await connection.ExecuteAsync("INSERT INTO wh_signals (wire_name, target_instance_id) VALUES ('after-conversion', NULL)");
    var created = (await connection.QueryAsync<string>(
      "SELECT partition_name FROM wh_partition_maintain('wh_signals', '1 day', 3, '7 days') WHERE action = 'created'")).ToList();
    var dropped = (await connection.QueryAsync<string>(
      "SELECT partition_name FROM wh_partition_maintain('wh_signals', '1 day', 3, '7 days', NOW() + INTERVAL '30 days') " +
      "WHERE action = 'dropped'")).ToList();

    await Assert.That(outcome).IsEqualTo("converted");
    await Assert.That(await connection.ExecuteScalarAsync<string>(
        "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'wh_signals'::regclass AND contype = 'p'"))
      .IsEqualTo("PRIMARY KEY (id, created_at)");
    await Assert.That(created.Count).IsEqualTo(3)
      .Because("the legacy partition covers today, so the three days after it are created");
    await Assert.That(dropped).Contains("wh_signals_legacy");
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_signals WHERE wire_name = 'old'")).IsEqualTo(0L);
  }

  [Test]
  public async Task TheLifecycleCompletions_Convert_ByEventIdRange_AndAreMaintainedByPartitionAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    // An id from an hour before the database's clock, which decides the conversion's bound.
    var recent = Guid.CreateVersion7(new DateTimeOffset(await connection.ExecuteScalarAsync<DateTime>("SELECT NOW() - INTERVAL '1 hour'")));
    await connection.ExecuteAsync(
      "INSERT INTO wh_lifecycle_completions (event_id, instance_id) VALUES (@recent, @recent)", new { recent });

    var outcome = await connection.ExecuteScalarAsync<string>(
      "SELECT wh_partition_convert('wh_lifecycle_completions', 'event_id', '1 day')");
    var duplicate = await connection.ExecuteAsync(
      "INSERT INTO wh_lifecycle_completions (event_id, instance_id) VALUES (@recent, @recent) ON CONFLICT DO NOTHING", new { recent });
    var created = (await connection.QueryAsync<string>(
      "SELECT partition_name FROM wh_partition_maintain('wh_lifecycle_completions', '1 day', 3, '7 days') WHERE action = 'created'")).ToList();
    var dropped = (await connection.QueryAsync<string>(
      "SELECT partition_name FROM wh_partition_maintain('wh_lifecycle_completions', '1 day', 3, '7 days', NOW() + INTERVAL '30 days') " +
      "WHERE action = 'dropped'")).ToList();

    await Assert.That(outcome).IsEqualTo("converted");
    await Assert.That(duplicate).IsEqualTo(0)
      .Because("the key is still the event id, so a second completion for one event is still refused");
    await Assert.That(created.Count).IsEqualTo(3);
    await Assert.That(dropped).Contains("wh_lifecycle_completions_legacy")
      .Because("the legacy partition ends at the first id of tomorrow, which is past the window thirty days on");
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_lifecycle_completions")).IsEqualTo(0L);
  }

  [Test]
  public async Task TheLifecycleCompletionsPartitions_CarryTheLedgersAutovacuumSettingsAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteScalarAsync<string>("SELECT wh_partition_convert('wh_lifecycle_completions', 'event_id', '1 day')");

    var cycle = (await connection.QueryAsync<string>(
      "SELECT action || ' ' || partition_name FROM wh_partition_maintain('wh_lifecycle_completions', '1 day', 3, NULL)")).ToList();
    var settings = (await connection.QueryAsync<string>(
      "SELECT COALESCE(array_to_string(c.reloptions, ','), '') FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid " +
      "WHERE i.inhparent = 'wh_lifecycle_completions'::regclass")).ToList();

    await Assert.That(cycle).Contains("tuned wh_lifecycle_completions_default")
      .Because("the conversion's default partition was made without the settings, so the next cycle gives it them");
    await Assert.That(settings.Count).IsEqualTo(5);
    await Assert.That(settings.Distinct().ToList())
      .IsEquivalentTo(["autovacuum_vacuum_scale_factor=0,autovacuum_vacuum_threshold=10000"]);
  }

  [Test]
  public async Task TheLog_OnceManaged_IsSweptInItsDefaultPartitionOnlyAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var now = await connection.ExecuteScalarAsync<DateTime>("SELECT NOW()");
    await connection.ExecuteScalarAsync<string>("SELECT wh_partition_convert('wh_log', 'logged_at', '7 days')");
    await connection.ExecuteAsync(
      "INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('partition_maintenance_enabled', 'true', 'boolean') " +
      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = 'true'");
    // The legacy partition is dropped as seen from 30 days on; then only the week 20 days back has a partition.
    await connection.ExecuteAsync(
      "SELECT * FROM wh_partition_maintain('wh_log', '7 days', 0, '14 days', @now::timestamptz + INTERVAL '30 days')", new { now });
    await connection.ExecuteAsync(
      "SELECT * FROM wh_partition_maintain('wh_log', '7 days', 0, NULL, @now::timestamptz - INTERVAL '20 days')", new { now });
    await connection.ExecuteAsync(
      "INSERT INTO wh_log (logged_at, log_level, source, error_message) VALUES " +
      "(@now::timestamptz - INTERVAL '20 days', 2, 'test', 'old-in-a-partition'), " +
      "(@now::timestamptz - INTERVAL '40 days', 2, 'test', 'old-in-the-default'), " +
      "(@now::timestamptz - INTERVAL '1 day', 2, 'test', 'young-in-the-default')",
      new { now });

    var status = await connection.ExecuteScalarAsync<string>(
      "SELECT status FROM perform_maintenance() WHERE task_name = 'purge_old_log_entries'");
    var left = (await connection.QueryAsync<string>("SELECT error_message FROM wh_log WHERE source = 'test'")).ToList();

    await Assert.That(status).IsEqualTo("ok (partitioned: the default partition only; expired partitions are dropped)");
    await Assert.That(left).IsEquivalentTo(["old-in-a-partition", "young-in-the-default"])
      .Because("a partition is left to the partition drop; only the default partition is swept by row");
  }
}
