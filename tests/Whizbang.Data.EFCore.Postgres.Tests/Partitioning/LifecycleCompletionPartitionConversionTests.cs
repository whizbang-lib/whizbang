// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Partitioning;

/// <summary>
/// Issue #1646: <c>wh_lifecycle_completions</c> converts to daily partitions by <c>event_id</c> range, keeping its
/// key and every row, and a row sweep finds the relation it may delete from with <c>wh_partition_sweep_relation</c>:
/// the table while it is plain or unmanaged, its default partition once the manager keeps it.
/// </summary>
/// <remarks>
/// Ids for rows that must land in the legacy partition are made for an hour before the database's clock, the clock
/// the conversion's bound is taken from, so a host clock running ahead never moves them.
/// </remarks>
/// <docs>operations/infrastructure/partitioned-ledgers#lifecycle-completions</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/233_PartitionSignalsAndLifecycleCompletions.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class LifecycleCompletionPartitionConversionTests : EFCoreTestBase {
  private const string TABLE = "wh_lifecycle_completions";
  private const string CONVERT = "SELECT wh_partition_convert('wh_lifecycle_completions', 'event_id', '1 day')";
  private const string CONVERTED = "converted";
  private const string SWEEP_RELATION = "SELECT wh_partition_sweep_relation(@table)";

  // A version-4 id: its first 48 bits read as a moment thousands of years out, so no daily partition covers it.
  private static readonly Guid _version4 = Guid.Parse("f47ac10b-58cc-4372-a567-0e02b2c3d479");

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync("SET statement_timeout = '30s'");
    return connection;
  }

  private static async Task<Guid> _recentIdAsync(NpgsqlConnection connection) =>
    Guid.CreateVersion7(new DateTimeOffset(await connection.ExecuteScalarAsync<DateTime>("SELECT NOW() - INTERVAL '1 hour'")));

  private static Task _completeAsync(NpgsqlConnection connection, Guid eventId) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_lifecycle_completions (event_id, instance_id) VALUES (@eventId, @eventId)", new { eventId });

  private static Task<string> _partitionOfAsync(NpgsqlConnection connection, Guid eventId) =>
    connection.ExecuteScalarAsync<string>(
      "SELECT tableoid::regclass::text FROM wh_lifecycle_completions WHERE event_id = @eventId", new { eventId });

  private static Task _enablePartitionMaintenanceAsync(NpgsqlConnection connection, string value) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('partition_maintenance_enabled', @value, 'boolean') " +
      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value", new { value });

  [Test]
  public async Task Conversion_KeepsEveryCompletion_AndAVersion4IdMovesToTheDefaultPartitionAsync() {
    await using var connection = await _openAsync();
    var recent = await _recentIdAsync(connection);
    await _completeAsync(connection, recent);
    await _completeAsync(connection, _version4);

    var outcome = await connection.ExecuteScalarAsync<string>(CONVERT);

    await Assert.That(outcome).IsEqualTo(CONVERTED);
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_lifecycle_completions")).IsEqualTo(2L);
    await Assert.That(await _partitionOfAsync(connection, recent)).IsEqualTo(TABLE + "_legacy");
    await Assert.That(await _partitionOfAsync(connection, _version4)).IsEqualTo(TABLE + "_default")
      .Because("an id that is not time-ordered sorts past the legacy partition's bound, so it waits in the default partition");
    await Assert.That(await connection.ExecuteScalarAsync<string>(
        "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'wh_lifecycle_completions'::regclass AND contype = 'p'"))
      .IsEqualTo("PRIMARY KEY (event_id)")
      .Because("the key is the partition column already, so a second completion for one event is still refused");
    await Assert.That(await connection.QueryAsync<string>(
        "SELECT indexrelid::regclass::text FROM pg_index WHERE indrelid = 'wh_lifecycle_completions'::regclass AND NOT indisprimary"))
      .IsEquivalentTo(["idx_lifecycle_completions_completed_at"]);
    await Assert.That(await connection.ExecuteScalarAsync<string>(
      "SELECT setting_value FROM wh_settings WHERE setting_key = 'wh_lifecycle_completions_partition_version'")).IsEqualTo("1");
  }

  [Test]
  public async Task Conversion_InterruptedPartway_LeavesTheTableAsItWas_AndTheNextCycleCompletesItAsync() {
    await using var connection = await _openAsync();
    var recent = await _recentIdAsync(connection);
    await _completeAsync(connection, recent);
    await connection.ExecuteAsync("CREATE TABLE wh_lifecycle_completions_default (id INT)");

    var error = await Assert.That(async () => await connection.ExecuteScalarAsync<string>(CONVERT)).Throws<PostgresException>();

    await Assert.That(error!.SqlState).IsEqualTo(PostgresErrorCodes.DuplicateTable);
    await Assert.That(await connection.ExecuteScalarAsync<string>(
      "SELECT relkind::text FROM pg_class WHERE oid = 'wh_lifecycle_completions'::regclass")).IsEqualTo("r");
    await connection.ExecuteAsync("DROP TABLE wh_lifecycle_completions_default");
    await Assert.That(await connection.ExecuteScalarAsync<string>(CONVERT)).IsEqualTo(CONVERTED);
    await Assert.That(await _partitionOfAsync(connection, recent)).IsEqualTo(TABLE + "_legacy");
  }

  [Test]
  public async Task TheSweepRelation_IsTheTable_UntilItIsPartitionedAndManaged_ThenItsDefaultPartitionAsync() {
    await using var connection = await _openAsync();

    var plain = await connection.ExecuteScalarAsync<string>(SWEEP_RELATION, new { table = TABLE });
    await connection.ExecuteScalarAsync<string>(CONVERT);
    var unmanaged = await connection.ExecuteScalarAsync<string>(SWEEP_RELATION, new { table = TABLE });
    await _enablePartitionMaintenanceAsync(connection, "false");
    var switchedOff = await connection.ExecuteScalarAsync<string>(SWEEP_RELATION, new { table = TABLE });
    await _enablePartitionMaintenanceAsync(connection, "true");
    var managed = await connection.ExecuteScalarAsync<string>(SWEEP_RELATION, new { table = TABLE });
    await connection.ExecuteAsync("DROP TABLE wh_lifecycle_completions_default");
    var noDefault = await connection.ExecuteScalarAsync<string>(SWEEP_RELATION, new { table = TABLE });
    var missing = await connection.ExecuteScalarAsync<string>(SWEEP_RELATION, new { table = "wh_partition_absent" });

    await Assert.That(plain).IsEqualTo("public.wh_lifecycle_completions");
    await Assert.That(unmanaged).IsEqualTo("public.wh_lifecycle_completions")
      .Because("with no partition_maintenance_enabled row, nothing drops partitions, so the row sweep keeps the table");
    await Assert.That(switchedOff).IsEqualTo("public.wh_lifecycle_completions");
    await Assert.That(managed).IsEqualTo("public.wh_lifecycle_completions_default");
    await Assert.That(noDefault).IsNull()
      .Because("a managed table with no default partition has nothing for a row sweep to do");
    await Assert.That(missing).IsNull();
  }

  [Test]
  public async Task ACycle_DropsAnEventIdRange_WhollyPastTheWindowAsync() {
    await using var connection = await _openAsync();
    await _completeAsync(connection, await _recentIdAsync(connection));
    await _completeAsync(connection, _version4);
    await connection.ExecuteScalarAsync<string>(CONVERT);

    var dropped = await connection.QueryAsync<string>(
      "SELECT partition_name FROM wh_partition_maintain('wh_lifecycle_completions', '1 day', 3, '7 days', NOW() + INTERVAL '30 days') " +
      "WHERE action = 'dropped'");

    await Assert.That(dropped).Contains(TABLE + "_legacy");
    await Assert.That(await connection.QueryAsync<Guid>("SELECT event_id FROM wh_lifecycle_completions")).IsEquivalentTo([_version4])
      .Because("the default partition is never dropped; the row sweep clears it by completed_at");
  }
}
