// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Partitioning;

/// <summary>
/// Issue #1611: <c>wh_partition_convert</c> turns <c>wh_log</c> into a table partitioned weekly by <c>logged_at</c>,
/// once, keeping every row and every writer working, and retention then drops partitions instead of deleting rows.
/// </summary>
/// <remarks>
/// The conversion is called with the database's own clock (its <c>p_now</c> default), the clock the writers stamp
/// <c>logged_at</c> with, so the rows a writer adds land where the conversion expects them; later moments are written
/// relative to it in SQL.
/// </remarks>
/// <docs>operations/infrastructure/partitioned-ledgers#converting-an-existing-table</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/226_PartitionManager.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class LogPartitionConversionTests : EFCoreTestBase {
  private const string CONVERT = "SELECT wh_partition_convert('wh_log', 'logged_at', '7 days')";
  private const string CONVERTED = "converted";
  private const string RELKIND = "SELECT relkind::text FROM pg_class WHERE oid = 'wh_log'::regclass";
  private const string MARKER = "SELECT setting_value FROM wh_settings WHERE setting_key = 'wh_log_partition_version'";
  private const string MANAGED_STATUS = "ok (partitioned: the default partition only; expired partitions are dropped)";

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync("SET statement_timeout = '30s'");
    return connection;
  }

  private static Task _logAsync(NpgsqlConnection connection, string message, int ageDays) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_log (logged_at, log_level, source, error_message) VALUES (NOW() - make_interval(days => @ageDays), 2, 'test', @message)",
      new { message, ageDays });

  private static Task _logAtAsync(NpgsqlConnection connection, DateTime now, string message, int ageDays) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_log (logged_at, log_level, source, error_message) VALUES (@now::timestamptz - make_interval(days => @ageDays), 2, 'test', @message)",
      new { now, message, ageDays });

  private static Task<List<string>> _messagesAsync(NpgsqlConnection connection) =>
    connection.QueryAsync<string>("SELECT error_message FROM wh_log WHERE source = 'test' ORDER BY log_id");

  private static Task _enablePartitionMaintenanceAsync(NpgsqlConnection connection) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('partition_maintenance_enabled', 'true', 'boolean') " +
      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = 'true'");

  private static async Task<(long Rows, string Status)> _purgeLogAsync(NpgsqlConnection connection) {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT rows_affected, status FROM perform_maintenance() WHERE task_name = 'purge_old_log_entries'";
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetString(1));
  }

  [Test]
  public async Task Conversion_KeepsEveryRow_InTheLegacyPartitionAsync() {
    await using var connection = await _openAsync();
    await _logAsync(connection, "month-old", 30);
    await _logAsync(connection, "week-old", 7);
    await _logAsync(connection, "today", 0);
    var before = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_log");

    var outcome = await connection.ExecuteScalarAsync<string>(CONVERT);

    await Assert.That(outcome).IsEqualTo(CONVERTED);
    await Assert.That(await connection.ExecuteScalarAsync<string>(RELKIND)).IsEqualTo("p");
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_log")).IsEqualTo(before);
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_log_legacy")).IsEqualTo(before)
      .Because("the table itself becomes the oldest partition; nothing is copied");
    await Assert.That(await connection.QueryAsync<string>(
        "SELECT c.relname::text FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid WHERE i.inhparent = 'wh_log'::regclass ORDER BY 1"))
      .IsEquivalentTo(["wh_log_default", "wh_log_legacy"]);
    await Assert.That(await connection.ExecuteScalarAsync<string>(
        "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'wh_log'::regclass AND contype = 'p'"))
      .IsEqualTo("PRIMARY KEY (log_id, logged_at)");
    await Assert.That(await connection.QueryAsync<string>(
        "SELECT indexrelid::regclass::text FROM pg_index WHERE indrelid = 'wh_log'::regclass AND NOT indisprimary ORDER BY 1"))
      .IsEquivalentTo(["idx_wh_log_event_id", "idx_wh_log_level", "idx_wh_log_logged_at", "idx_wh_log_message_id"])
      .Because("the parent keeps the index names migration 028 gave them, so re-running 028 finds them in place");
    await Assert.That(await connection.ExecuteScalarAsync<string>(MARKER)).IsEqualTo("1");
  }

  [Test]
  public async Task EveryWriter_StillInserts_IncludingASessionThatWroteBeforeTheConversionAsync() {
    await using var writer = await _openAsync();
    await writer.ExecuteAsync("SELECT log_event(3, 'test', 'before-conversion')");
    var lastId = await writer.ExecuteScalarAsync<long>("SELECT max(log_id) FROM wh_log");

    await using (var converter = await _openAsync()) {
      await Assert.That(await converter.ExecuteScalarAsync<string>(CONVERT)).IsEqualTo(CONVERTED);
    }

    var instance = Guid.CreateVersion7();
    var stale = Guid.CreateVersion7();
    await writer.ExecuteAsync("SELECT log_event(3, 'test', 'after-conversion')");
    await writer.ExecuteAsync(
      "INSERT INTO wh_log (log_level, source, error_message) VALUES (2, 'test', 'plain-insert')");
    await writer.ExecuteAsync(
      "INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata) " +
      "VALUES (@instance, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb), " +
      "(@stale, 'test-svc', 'test-host', 2, NOW() - INTERVAL '1 day', NOW() - INTERVAL '1 day', '{}'::jsonb)",
      new { instance, stale });
    await writer.ExecuteAsync("SELECT deregister_instance(@instance, 'test-svc', 'test-host')", new { instance });
    await writer.ExecuteAsync(
      "SELECT * FROM cleanup_stale_instances(NOW() - INTERVAL '1 hour', NOW() - INTERVAL '2 hours')");

    await Assert.That(await _messagesAsync(writer))
      .IsEquivalentTo(["before-conversion", "after-conversion", "plain-insert"]);
    await Assert.That(await writer.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM wh_log WHERE source = 'shutdown' AND message_id = @instance", new { instance })).IsEqualTo(1L);
    await Assert.That(await writer.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM wh_log WHERE source = 'stale_cleanup' AND message_id = @stale", new { stale })).IsEqualTo(1L);
    await Assert.That(await writer.ExecuteScalarAsync<long>(
        "SELECT min(log_id) FROM wh_log WHERE error_message = 'after-conversion'")).IsGreaterThan(lastId)
      .Because("the parent draws its ids from the legacy table's sequence, so ids keep rising");
  }

  [Test]
  public async Task Conversion_InterruptedPartway_LeavesTheTableAsItWas_AndTheNextCycleCompletesItAsync() {
    await using var connection = await _openAsync();
    await _logAsync(connection, "kept", 1);
    // A table already holding the default partition's name fails the conversion at its last statement.
    await connection.ExecuteAsync("CREATE TABLE wh_log_default (id INT)");

    var error = await Assert.That(async () => await connection.ExecuteScalarAsync<string>(CONVERT)).Throws<PostgresException>();

    await Assert.That(error!.SqlState).IsEqualTo(PostgresErrorCodes.DuplicateTable);
    await Assert.That(await connection.ExecuteScalarAsync<string>(RELKIND)).IsEqualTo("r")
      .Because("the conversion is one transaction, so a failure anywhere leaves the plain table");
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM pg_class WHERE relname = 'wh_log_legacy'")).IsEqualTo(0L);
    await Assert.That(await connection.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_settings WHERE setting_key = 'wh_log_partition_version'")).IsEqualTo(0L);
    await _logAsync(connection, "written-between", 0);

    await connection.ExecuteAsync("DROP TABLE wh_log_default");
    var outcome = await connection.ExecuteScalarAsync<string>(CONVERT);

    await Assert.That(outcome).IsEqualTo(CONVERTED);
    await Assert.That(await _messagesAsync(connection)).IsEquivalentTo(["kept", "written-between"]);
  }

  [Test]
  public async Task Conversion_WhileAWriterHoldsTheTable_IsDeferredAsync() {
    await using var connection = await _openAsync();
    await using var writer = await _openAsync();
    await using var writing = await writer.BeginTransactionAsync();
    await _logAsync(writer, "in-flight", 0);

    var deferred = await connection.ExecuteScalarAsync<string>(CONVERT);
    await writing.CommitAsync();
    var outcome = await connection.ExecuteScalarAsync<string>(CONVERT);

    await Assert.That(deferred).IsEqualTo("deferred")
      .Because("the conversion takes the table's exclusive lock only when it is free, never queueing writers");
    await Assert.That(outcome).IsEqualTo(CONVERTED);
    await Assert.That(await _messagesAsync(connection)).IsEquivalentTo(["in-flight"]);
  }

  [Test]
  public async Task AnotherConversionInFlight_IsReportedBusyAsync() {
    await using var connection = await _openAsync();
    await using var other = await _openAsync();
    await using var holding = await other.BeginTransactionAsync();
    await other.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('wh_partition:public.wh_log', 0))");

    var outcome = await connection.ExecuteScalarAsync<string>(CONVERT);
    await holding.RollbackAsync();

    await Assert.That(outcome).IsEqualTo("busy");
    await Assert.That(await connection.ExecuteScalarAsync<string>(RELKIND)).IsEqualTo("r");
  }

  [Test]
  public async Task TheMarker_StopsTheConversionRunningAgainAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(
      "INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('wh_log_partition_version', '1', 'integer')");

    var outcome = await connection.ExecuteScalarAsync<string>(CONVERT);

    await Assert.That(outcome).IsEqualTo("already-converted");
    await Assert.That(await connection.ExecuteScalarAsync<string>(RELKIND)).IsEqualTo("r")
      .Because("the marker, not the table's shape, decides whether the conversion runs");
  }

  [Test]
  public async Task AnAlreadyPartitionedTable_IsMarkedWithoutChangeAsync() {
    await using var connection = await _openAsync();
    await Assert.That(await connection.ExecuteScalarAsync<string>(CONVERT)).IsEqualTo(CONVERTED);
    await connection.ExecuteAsync("DELETE FROM wh_settings WHERE setting_key = 'wh_log_partition_version'");

    var outcome = await connection.ExecuteScalarAsync<string>(CONVERT);

    await Assert.That(outcome).IsEqualTo("already-converted");
    await Assert.That(await connection.ExecuteScalarAsync<string>(MARKER)).IsEqualTo("1");
  }

  [Test]
  public async Task AMissingTable_IsReportedMissingAsync() {
    await using var connection = await _openAsync();

    var outcome = await connection.ExecuteScalarAsync<string>(
      "SELECT wh_partition_convert('wh_partition_absent', 'at', '1 day')");

    await Assert.That(outcome).IsEqualTo("missing");
  }

  [Test]
  public async Task ATableWithoutANotNullTimestampColumn_IsRejectedAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync("CREATE TABLE wh_partition_candidate (id BIGINT PRIMARY KEY, at TIMESTAMPTZ)");

    var error = await Assert.That(async () =>
      await connection.ExecuteScalarAsync<string>("SELECT wh_partition_convert('wh_partition_candidate', 'at', '1 day')"))
      .Throws<PostgresException>();

    await Assert.That(error!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidParameterValue);
  }

  [Test]
  public async Task ATableWithAUniqueIndexWithoutThePartitionColumn_IsRejectedAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(
      "CREATE TABLE wh_partition_candidate (id BIGINT PRIMARY KEY, code TEXT, at TIMESTAMPTZ NOT NULL); " +
      "CREATE UNIQUE INDEX wh_partition_candidate_code ON wh_partition_candidate (code)");

    var error = await Assert.That(async () =>
      await connection.ExecuteScalarAsync<string>("SELECT wh_partition_convert('wh_partition_candidate', 'at', '1 day')"))
      .Throws<PostgresException>();

    await Assert.That(error!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidParameterValue);
  }

  [Test]
  public async Task ATableWhoseKeyAlreadyHoldsThePartitionColumn_KeepsItsKeyAndUniqueIndexesAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(
      "CREATE TABLE wh_partition_candidate (id BIGINT, at TIMESTAMPTZ NOT NULL, code TEXT, PRIMARY KEY (id, at)); " +
      "CREATE UNIQUE INDEX wh_partition_candidate_code ON wh_partition_candidate (code, at); " +
      "COMMENT ON TABLE wh_partition_candidate IS 'candidate'; " +
      "INSERT INTO wh_partition_candidate VALUES (1, NOW(), 'a')");

    var outcome = await connection.ExecuteScalarAsync<string>(
      "SELECT wh_partition_convert('wh_partition_candidate', 'at', '1 day')");

    await Assert.That(outcome).IsEqualTo(CONVERTED);
    await Assert.That(await connection.ExecuteScalarAsync<string>(
        "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'wh_partition_candidate'::regclass AND contype = 'p'"))
      .IsEqualTo("PRIMARY KEY (id, at)");
    await Assert.That(await connection.ExecuteScalarAsync<bool>(
        "SELECT indisunique FROM pg_index WHERE indexrelid = 'wh_partition_candidate_code'::regclass")).IsTrue();
    await Assert.That(await connection.ExecuteScalarAsync<string>(
        "SELECT obj_description('wh_partition_candidate'::regclass, 'pg_class')")).IsEqualTo("candidate");
  }

  [Test]
  public async Task Conversion_CarriesTheTablesGrantsToTheParentAsync() {
    await using var connection = await _openAsync();
    var role = $"wh_grant_probe_{Guid.CreateVersion7():N}";
    await connection.ExecuteAsync($"CREATE ROLE {role}");
    try {
      await connection.ExecuteAsync($"GRANT SELECT ON wh_log TO {role} WITH GRANT OPTION; GRANT INSERT ON wh_log TO PUBLIC");

      await connection.ExecuteScalarAsync<string>(CONVERT);

      await Assert.That(await connection.ExecuteScalarAsync<bool>(
        $"SELECT has_table_privilege('{role}', 'wh_log', 'SELECT WITH GRANT OPTION')")).IsTrue();
      await Assert.That(await connection.ExecuteScalarAsync<bool>("SELECT has_table_privilege('public', 'wh_log', 'INSERT')"))
        .IsTrue();
    } finally {
      await connection.ExecuteAsync($"DROP OWNED BY {role}; DROP ROLE {role}");
    }
  }

  [Test]
  public async Task OnceConverted_RetentionDropsPartitions_InsteadOfDeletingRowsAsync() {
    await using var connection = await _openAsync();
    await _logAsync(connection, "old", 20);
    await connection.ExecuteScalarAsync<string>(CONVERT);
    await _enablePartitionMaintenanceAsync(connection);

    var (rows, status) = await _purgeLogAsync(connection);

    await Assert.That(rows).IsEqualTo(0L);
    await Assert.That(status).IsEqualTo(MANAGED_STATUS);
    await Assert.That(await _messagesAsync(connection)).IsEquivalentTo(["old"])
      .Because("no row is deleted from a partitioned log; the partition holding it goes when it ends past the window");

    var dropped = await connection.QueryAsync<string>(
      "SELECT partition_name FROM wh_partition_maintain('wh_log', '7 days', 3, '14 days', NOW() + INTERVAL '30 days') " +
      "WHERE action = 'dropped'");

    await Assert.That(dropped).Contains("wh_log_legacy");
    await Assert.That(await _messagesAsync(connection)).IsEmpty();
  }

  [Test]
  public async Task AConvertedLog_WithPartitioningSwitchedOff_IsStillBoundedByTheRowDeleteAsync() {
    await using var connection = await _openAsync();
    await _logAsync(connection, "old", 20);
    await _logAsync(connection, "young", 1);
    await connection.ExecuteScalarAsync<string>(CONVERT);

    var (_, status) = await _purgeLogAsync(connection);

    await Assert.That(status).IsEqualTo("ok");
    await Assert.That(await _messagesAsync(connection)).IsEquivalentTo(["young"]);
  }

  [Test]
  public async Task OnceManaged_TheRowDeleteSweepsOnlyTheDefaultPartition_AndLeavesPartitionsToTheDropAsync() {
    await using var connection = await _openAsync();
    // One moment read from the database, so every partition and row below is placed against the same instant.
    var now = await connection.ExecuteScalarAsync<DateTime>("SELECT NOW()");
    await connection.ExecuteScalarAsync<string>(CONVERT);
    await _enablePartitionMaintenanceAsync(connection);
    // Thirty days on, the legacy partition has ended past the 14-day window and is dropped, so nothing covers the
    // weeks before then; a partition is then made for the week 20 days back, and nothing else covers the past.
    await connection.ExecuteAsync(
      "SELECT * FROM wh_partition_maintain('wh_log', '7 days', 0, '14 days', @now::timestamptz + INTERVAL '30 days')", new { now });
    await connection.ExecuteAsync(
      "SELECT * FROM wh_partition_maintain('wh_log', '7 days', 0, NULL, @now::timestamptz - INTERVAL '20 days')", new { now });
    await _logAtAsync(connection, now, "old-in-a-partition", 20);
    await _logAtAsync(connection, now, "old-in-the-default", 40);
    await _logAtAsync(connection, now, "young-in-the-default", 1);
    var placedIn = await connection.QueryAsync<string>(
      "SELECT error_message || CASE WHEN tableoid = 'wh_log_default'::regclass THEN ' default' ELSE ' partition' END " +
      "FROM wh_log WHERE source = 'test' ORDER BY error_message");

    var (rows, status) = await _purgeLogAsync(connection);

    await Assert.That(placedIn).IsEquivalentTo(
      ["old-in-a-partition partition", "old-in-the-default default", "young-in-the-default default"]);
    await Assert.That(rows).IsEqualTo(1L);
    await Assert.That(status).IsEqualTo(MANAGED_STATUS);
    await Assert.That(await _messagesAsync(connection)).IsEquivalentTo(["old-in-a-partition", "young-in-the-default"])
      .Because("an expired partition goes whole when the manager drops it; only the default partition, which no drop reaches, is swept by row");
  }

  [Test]
  public async Task OnceManaged_WithoutADefaultPartition_TheRowDeleteSkipsAsync() {
    await using var connection = await _openAsync();
    await _logAsync(connection, "old", 20);
    await connection.ExecuteScalarAsync<string>(CONVERT);
    await _enablePartitionMaintenanceAsync(connection);
    await connection.ExecuteAsync("DROP TABLE wh_log_default");

    var (rows, status) = await _purgeLogAsync(connection);

    await Assert.That(rows).IsEqualTo(0L);
    await Assert.That(status).IsEqualTo("skipped (partitioned: no default partition)");
    await Assert.That(await _messagesAsync(connection)).IsEquivalentTo(["old"]);
  }
}
