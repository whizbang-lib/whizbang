// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Partitioning;

/// <summary>
/// Issue #1611: <c>wh_partition_maintain</c> creates a range-partitioned table's partitions ahead, keeps a default
/// partition, and removes partitions wholly past the retention window, without ever waiting for a lock.
/// </summary>
/// <remarks>
/// Each test owns a partitioned probe table and passes the moment explicitly, so no test reads a clock. The lock
/// tests hold a transaction open on one connection and call the manager on another; the manager never waits, so the
/// statement timeout on the calling connection is only there to turn a regression into a failure rather than a hang.
/// </remarks>
/// <docs>operations/infrastructure/partitioned-ledgers</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/226_PartitionManager.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class PartitionMaintainSqlTests : EFCoreTestBase {
  private const string TABLE = "wh_partition_probe";
  private const string NOW = "2026-10-21 12:00:00+00";
  private const string WEEK = "7 days";
  private const string CREATED = "created";
  private const string DROPPED = "dropped";
  private const string DEFAULT_PARTITION = TABLE + "_default";

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync("SET statement_timeout = '30s'");
    return connection;
  }

  private static Task _createProbeAsync(NpgsqlConnection connection) =>
    connection.ExecuteAsync(
      $"CREATE TABLE {TABLE} (id BIGSERIAL, at TIMESTAMPTZ NOT NULL, note TEXT, PRIMARY KEY (id, at)) PARTITION BY RANGE (at)");

  private static async Task<List<(string Action, string Name)>> _maintainAsync(
      NpgsqlConnection connection, string now, int ahead = 3, string? keep = null, string interval = WEEK, string table = TABLE) {
    var results = new List<(string Action, string Name)>();
    await using var command = connection.CreateCommand();
    command.CommandText =
      "SELECT action, partition_name FROM wh_partition_maintain(@table, @interval::interval, @ahead, @keep::interval, @now::timestamptz)";
    command.Parameters.AddWithValue(nameof(table), table);
    command.Parameters.AddWithValue(nameof(interval), interval);
    command.Parameters.AddWithValue(nameof(ahead), ahead);
    command.Parameters.Add(new NpgsqlParameter(nameof(keep), NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)keep ?? DBNull.Value });
    command.Parameters.AddWithValue(nameof(now), now);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      results.Add((reader.GetString(0), reader.GetString(1)));
    }
    return results;
  }

  private static Task<List<string>> _partitionsAsync(NpgsqlConnection connection, string table = TABLE) =>
    connection.QueryAsync<string>(
      $"SELECT c.relname::text FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid WHERE i.inhparent = '{table}'::regclass ORDER BY 1");

  private static Task _insertAsync(NpgsqlConnection connection, string at, string note) =>
    connection.ExecuteAsync($"INSERT INTO {TABLE} (at, note) VALUES (@at::timestamptz, @note)", new { at, note });

  private static List<string> _named(List<(string Action, string Name)> results, string action) =>
    [.. results.Where(r => r.Action == action).Select(r => r.Name)];

  [Test]
  public async Task ACycle_CreatesTheCurrentAndAheadPartitions_AndASecondCreatesNothingAsync() {
    await using var connection = await _openAsync();
    await _createProbeAsync(connection);

    var first = await _maintainAsync(connection, NOW, ahead: 3);
    var second = await _maintainAsync(connection, NOW, ahead: 3);

    string[] expected = [DEFAULT_PARTITION, TABLE + "_p20261019", TABLE + "_p20261026", TABLE + "_p20261102", TABLE + "_p20261109"];
    await Assert.That(_named(first, CREATED)).IsEquivalentTo(expected)
      .Because("the week holding the moment, three weeks after it, and a default partition for anything else");
    await Assert.That(await _partitionsAsync(connection)).IsEquivalentTo(expected);
    await Assert.That(second).IsEmpty()
      .Because("a cycle that finds every partition in place changes nothing");
  }

  [Test]
  public async Task WeeklyPartitions_StartOnMondayAsync() {
    await using var connection = await _openAsync();

    var sunday = await connection.ExecuteScalarAsync<DateTime>(
      "SELECT _wh_partition_start('2026-10-25 23:59:59+00'::timestamptz, '7 days'::interval)");
    var monday = await connection.ExecuteScalarAsync<DateTime>(
      "SELECT _wh_partition_start('2026-10-26 00:00:00+00'::timestamptz, '7 days'::interval)");
    var day = await connection.ExecuteScalarAsync<DateTime>(
      "SELECT _wh_partition_start('2026-10-21 23:30:00+00'::timestamptz, '1 day'::interval)");

    await Assert.That(sunday).IsEqualTo(new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc));
    await Assert.That(monday).IsEqualTo(new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));
    await Assert.That(day).IsEqualTo(new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc));
  }

  [Test]
  public async Task HourlyPartitions_AreNamedByTheHourAsync() {
    await using var connection = await _openAsync();
    await _createProbeAsync(connection);

    var results = await _maintainAsync(connection, "2026-10-21 12:30:00+00", ahead: 1, interval: "1 hour");

    await Assert.That(_named(results, CREATED))
      .IsEquivalentTo([DEFAULT_PARTITION, TABLE + "_p20261021_1200", TABLE + "_p20261021_1300"]);
  }

  [Test]
  [Arguments("1 month")]
  [Arguments("90 minutes")]
  [Arguments("30 minutes")]
  public async Task AnIntervalThatIsNotWholeHours_IsRejectedAsync(string interval) {
    await using var connection = await _openAsync();
    await _createProbeAsync(connection);

    var error = await Assert.That(async () => await _maintainAsync(connection, NOW, interval: interval)).Throws<PostgresException>();

    await Assert.That(error!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidParameterValue);
  }

  [Test]
  public async Task ANegativeAhead_IsRejectedAsync() {
    await using var connection = await _openAsync();
    await _createProbeAsync(connection);

    var error = await Assert.That(async () => await _maintainAsync(connection, NOW, ahead: -1)).Throws<PostgresException>();

    await Assert.That(error!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidParameterValue);
  }

  [Test]
  public async Task ATableNotRangePartitionedOnATimestamp_IsRejectedAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync($"CREATE TABLE {TABLE} (id BIGINT NOT NULL) PARTITION BY RANGE (id)");

    var error = await Assert.That(async () => await _maintainAsync(connection, NOW)).Throws<PostgresException>();

    await Assert.That(error!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidParameterValue);
  }

  [Test]
  public async Task ATableThatIsNotPartitioned_OrDoesNotExist_IsSkippedAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync($"CREATE TABLE {TABLE} (at TIMESTAMPTZ NOT NULL)");

    var plain = await _maintainAsync(connection, NOW);
    var missing = await _maintainAsync(connection, NOW, table: "wh_partition_absent");

    await Assert.That(plain).IsEquivalentTo([("skipped", TABLE)]);
    await Assert.That(missing).IsEquivalentTo([("skipped", "wh_partition_absent")]);
  }

  [Test]
  public async Task APartitionWhollyPastTheWindow_IsDropped_OneStraddlingItIsKeptAsync() {
    await using var connection = await _openAsync();
    await _createProbeAsync(connection);
    await _maintainAsync(connection, "2026-09-28 12:00:00+00", ahead: 3);
    await _insertAsync(connection, "2026-10-01 00:00:00+00", "expired");
    await _insertAsync(connection, "2026-10-08 00:00:00+00", "straddling");
    await _insertAsync(connection, "2027-01-01 00:00:00+00", "far-future");

    // Fourteen days before the moment is 2026-10-07 12:00: the week ending 10-05 is wholly older, the week of
    // 10-05 to 10-12 straddles it.
    var results = await _maintainAsync(connection, NOW, ahead: 0, keep: "14 days");

    await Assert.That(_named(results, DROPPED)).IsEquivalentTo([TABLE + "_p20260928"]);
    var partitions = await _partitionsAsync(connection);
    await Assert.That(partitions).Contains(TABLE + "_p20261005")
      .Because("a partition holding rows still inside the window is kept whole");
    await Assert.That(partitions).Contains(DEFAULT_PARTITION)
      .Because("the default partition is never a candidate");
    await Assert.That(await connection.QueryAsync<string>($"SELECT note FROM {TABLE} ORDER BY at"))
      .IsEquivalentTo(["straddling", "far-future"]);
  }

  [Test]
  public async Task TheDefaultPartition_IsNeverDroppedAsync() {
    await using var connection = await _openAsync();
    await _createProbeAsync(connection);
    await _maintainAsync(connection, "2026-09-28 12:00:00+00", ahead: 0);
    await _insertAsync(connection, "2020-01-01 00:00:00+00", "caught-by-default");

    var results = await _maintainAsync(connection, NOW, ahead: 0, keep: "0 days");

    await Assert.That(_named(results, DROPPED)).IsEquivalentTo([TABLE + "_p20260928"]);
    await Assert.That(await _partitionsAsync(connection)).Contains(DEFAULT_PARTITION);
    await Assert.That(await connection.QueryAsync<string>($"SELECT note FROM {TABLE}"))
      .IsEquivalentTo(["caught-by-default"]);
  }

  [Test]
  public async Task ANullKeep_RemovesNothingAsync() {
    await using var connection = await _openAsync();
    await _createProbeAsync(connection);
    await _maintainAsync(connection, "2026-01-05 12:00:00+00", ahead: 0);

    var results = await _maintainAsync(connection, NOW, ahead: 0, keep: null);

    await Assert.That(_named(results, DROPPED)).IsEmpty();
    await Assert.That(await _partitionsAsync(connection)).Contains(TABLE + "_p20260105");
  }

  [Test]
  public async Task DebugMode_KeepsExpiredPartitionsAsync() {
    await using var connection = await _openAsync();
    await _createProbeAsync(connection);
    await _maintainAsync(connection, "2026-01-05 12:00:00+00", ahead: 0);
    await connection.ExecuteAsync(
      "INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('debug_mode', 'true', 'boolean') " +
      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = 'true'");

    var results = await _maintainAsync(connection, NOW, ahead: 0, keep: "14 days");

    await Assert.That(_named(results, "kept")).IsEquivalentTo([TABLE + "_p20260105"]);
    await Assert.That(await _partitionsAsync(connection)).Contains(TABLE + "_p20260105");
  }

  [Test]
  public async Task RowsInTheDefaultPartition_MoveIntoThePartitionCreatedForThemAsync() {
    await using var connection = await _openAsync();
    await _createProbeAsync(connection);
    await _maintainAsync(connection, NOW, ahead: 0);
    await _insertAsync(connection, "2026-11-03 09:00:00+00", "early");

    var results = await _maintainAsync(connection, "2026-11-03 12:00:00+00", ahead: 0);

    await Assert.That(_named(results, CREATED)).IsEquivalentTo([TABLE + "_p20261102"]);
    await Assert.That(await connection.ExecuteScalarAsync<string>($"SELECT tableoid::regclass::text FROM {TABLE} WHERE note = 'early'"))
      .IsEqualTo(TABLE + "_p20261102");
  }

  [Test]
  public async Task AnExpiredPartition_IsEmptiedWithoutBlockingAnInsertInFlightAsync() {
    await using var setup = await _openAsync();
    await _createProbeAsync(setup);
    await _maintainAsync(setup, "2026-09-28 12:00:00+00", ahead: 0);
    await _maintainAsync(setup, NOW, ahead: 0);
    await _insertAsync(setup, "2026-09-30 00:00:00+00", "expired");

    // A writer mid-transaction in the current partition holds ROW EXCLUSIVE on the parent.
    await using var writer = await _openAsync();
    await using var writing = await writer.BeginTransactionAsync();
    await _insertAsync(writer, "2026-10-21 12:00:00+00", "first");

    await using var manager = await _openAsync();
    await using var managing = await manager.BeginTransactionAsync();
    var results = await _maintainAsync(manager, NOW, ahead: 0, keep: "14 days");
    var managerPid = manager.ProcessID;
    var heldOnParent = await setup.QueryAsync<string>(
      $"SELECT mode FROM pg_locks WHERE pid = {managerPid} AND relation = '{TABLE}'::regclass");

    // Still inside the manager's transaction: an insert into the current partition goes straight through.
    await _insertAsync(writer, "2026-10-21 12:00:01+00", "second");
    await writing.CommitAsync();
    await managing.CommitAsync();

    await Assert.That(_named(results, "emptied")).IsEquivalentTo([TABLE + "_p20260928"])
      .Because("with a writer on the table the parent's exclusive lock is not free, so the partition is only emptied");
    await Assert.That(heldOnParent).DoesNotContain("AccessExclusiveLock");
    await Assert.That(await setup.QueryAsync<string>($"SELECT note FROM {TABLE} ORDER BY at"))
      .IsEquivalentTo(["first", "second"]);

    var later = await _maintainAsync(setup, NOW, ahead: 0, keep: "14 days");
    await Assert.That(_named(later, DROPPED)).IsEquivalentTo([TABLE + "_p20260928"])
      .Because("the emptied partition is detached once the table is free");
  }

  [Test]
  public async Task AnExpiredPartitionInUse_IsDeferredAsync() {
    await using var setup = await _openAsync();
    await _createProbeAsync(setup);
    await _maintainAsync(setup, "2026-09-28 12:00:00+00", ahead: 0);
    await _maintainAsync(setup, NOW, ahead: 0);

    await using var reader = await _openAsync();
    await using var reading = await reader.BeginTransactionAsync();
    await reader.ExecuteScalarAsync<long>($"SELECT count(*) FROM {TABLE}_p20260928");

    var results = await _maintainAsync(setup, NOW, ahead: 0, keep: "14 days");
    await reading.RollbackAsync();

    await Assert.That(_named(results, "deferred")).IsEquivalentTo([TABLE + "_p20260928"]);
    await Assert.That(await _partitionsAsync(setup)).Contains(TABLE + "_p20260928");
  }

  [Test]
  public async Task ANewPartition_IsDeferredWhileARowIsBeingWrittenToTheDefaultAsync() {
    await using var setup = await _openAsync();
    await _createProbeAsync(setup);
    await _maintainAsync(setup, NOW, ahead: 0);

    await using var writer = await _openAsync();
    await using var writing = await writer.BeginTransactionAsync();
    await _insertAsync(writer, "2030-01-01 00:00:00+00", "in-default");

    var results = await _maintainAsync(setup, NOW, ahead: 1);
    await writing.RollbackAsync();

    await Assert.That(_named(results, "deferred")).IsEquivalentTo([TABLE + "_p20261026"]);
    await Assert.That(await _partitionsAsync(setup)).DoesNotContain(TABLE + "_p20261026");
  }

  [Test]
  public async Task AMissingDefaultPartition_IsDeferredWhileTheTableIsInUseAsync() {
    await using var setup = await _openAsync();
    await _createProbeAsync(setup);
    await setup.ExecuteAsync(
      $"CREATE TABLE {TABLE}_manual PARTITION OF {TABLE} FOR VALUES FROM ('2026-10-19 00:00:00+00') TO ('2026-10-26 00:00:00+00')");

    await using var writer = await _openAsync();
    await using var writing = await writer.BeginTransactionAsync();
    await _insertAsync(writer, "2026-10-21 00:00:00+00", "in-flight");

    var results = await _maintainAsync(setup, NOW, ahead: 0);
    await writing.RollbackAsync();

    await Assert.That(_named(results, "deferred")).IsEquivalentTo([DEFAULT_PARTITION]);
    await Assert.That(await _partitionsAsync(setup)).IsEquivalentTo([TABLE + "_manual"]);
  }

  [Test]
  public async Task AnotherCycleInFlightOnTheTable_SkipsItAsync() {
    await using var setup = await _openAsync();
    await _createProbeAsync(setup);

    await using var other = await _openAsync();
    await using var holding = await other.BeginTransactionAsync();
    await other.ExecuteAsync($"SELECT pg_advisory_xact_lock(hashtextextended('wh_partition:public.{TABLE}', 0))");

    var results = await _maintainAsync(setup, NOW);
    await holding.RollbackAsync();

    await Assert.That(results).IsEquivalentTo([("skipped", TABLE)]);
    await Assert.That(await _partitionsAsync(setup)).IsEmpty();
  }
}
