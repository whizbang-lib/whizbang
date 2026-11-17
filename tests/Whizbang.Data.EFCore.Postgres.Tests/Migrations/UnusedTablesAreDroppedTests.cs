// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Data.Schema;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Tables nothing reads or writes are not created, and an upgraded database loses them: <c>wh_partition_assignments</c>
/// (the claim ranks from the live instances and the published assignment, never from it) is created by no bootstrap
/// and dropped by migration 476. <c>wh_receptor_processing</c> has had no writer since receptors stopped recording
/// per-event progress, yet the claim probed it on every poll and the instance, purge and statistics functions touched
/// it: after migration 476 no framework function reads or writes it, and migration 477 drops it (see the partial
/// <c>UnusedTablesAreDroppedTests.ReceptorTable.cs</c>).
/// </summary>
/// <docs>operations/infrastructure/migrations#unused-tables-are-dropped</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/476_UnusedCoordinationTablesAreDropped.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Schema/PostgresSchemaBuilder.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/477_ReceptorProcessingTableIsDropped.sql</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public partial class UnusedTablesAreDroppedTests : EFCoreTestBase {
  private const string DROP_MIGRATION = "476_";

  [Test]
  public async Task AfterTheMigrations_ThePartitionAssignmentsTable_DoesNotExistAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);

    await Assert.That(await _existsAsync(conn, "wh_partition_assignments")).IsFalse()
      .Because("nothing reads or writes it: the claim ranks from the live instances and the published partition assignment");
  }

  [Test]
  public async Task AnUpgradedDatabaseThatStillHasThePartitionAssignmentsTable_LosesItAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    // As an earlier release's bootstrap left it.
    await _executeAsync(conn, """
      CREATE TABLE IF NOT EXISTS wh_partition_assignments (
        partition_number INTEGER NOT NULL PRIMARY KEY, instance_id UUID NOT NULL,
        assigned_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), last_heartbeat TIMESTAMPTZ NOT NULL DEFAULT NOW());
      CREATE INDEX IF NOT EXISTS idx_partition_assignments_instance ON wh_partition_assignments (instance_id, last_heartbeat);
      INSERT INTO wh_partition_assignments (partition_number, instance_id) VALUES (0, gen_random_uuid());
      """);

    await _executeAsync(conn, _dropMigrationSql());
    await _executeAsync(conn, _dropMigrationSql());

    await Assert.That(await _existsAsync(conn, "wh_partition_assignments")).IsFalse()
      .Because("the migration drops it whatever it holds, and running it again finds nothing to drop");
  }

  [Test]
  public async Task TheInfrastructureSchema_DeclaresNoPartitionAssignmentsTableAsync() {
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(new SchemaConfiguration());

    await Assert.That(sql).DoesNotContain("partition_assignments")
      .Because("a table the bootstrap declares is created again on every start that runs it, after the migration dropped it");
    await Assert.That(OperationalTableRegistry.Find("wh_partition_assignments")).IsNull()
      .Because("the registry lists the tables that exist");
  }

  [Test]
  public async Task AfterTheMigrations_NoFrameworkFunctionReadsOrWritesTheReceptorProcessingTableAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT string_agg(p.proname, ', ' ORDER BY p.proname)
      FROM pg_proc p
      WHERE p.pronamespace = 'public'::regnamespace
        AND p.prosrc ~ '\mwh_receptor_processing\M'
      """;
    var readers = await cmd.ExecuteScalarAsync() as string;

    await Assert.That(readers).IsNull()
      .Because($"nothing writes the table, so a function that reads it reads nothing and one that writes it keeps a dead table busy ({readers})");
  }

  [Test]
  public async Task TheDropReverses_AndAppliesAgainAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);

    // Rolled back newest first, as a rollback runs: 477's reverse restores the table the restored functions read.
    await _executeAsync(conn, _migrationSql("477_", down: true));
    await _executeAsync(conn, _migrationSql(DROP_MIGRATION, down: true));

    await Assert.That(await _existsAsync(conn, "wh_partition_assignments")).IsTrue()
      .Because("the reverse re-creates the table, empty, as an earlier release's bootstrap does");
    await using (var claim = conn.CreateCommand()) {
      claim.CommandText = "SELECT count(*) FROM claim_orphaned_receptor_work(gen_random_uuid(), 0, 1, NOW() + INTERVAL '5 minutes', NOW())";
      await Assert.That(Convert.ToInt64(await claim.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(0L)
        .Because("the reverse restores the receptor-work claim an earlier release's claim_work calls, and it claims nothing");
    }

    await _executeAsync(conn, _dropMigrationSql());
    await _executeAsync(conn, _migrationSql("477_", down: false));

    await Assert.That(await _existsAsync(conn, "wh_partition_assignments")).IsFalse();
    await Assert.That(await _existsAsync(conn, "wh_receptor_processing")).IsFalse();
  }

  private static string _dropMigrationSql() => _migrationSql(DROP_MIGRATION, down: false);

  private static string _migrationSql(string prefix, bool down) {
    if (!down) {
      return new PostgresMigrationProvider().GetMigrations().Single(m => m.Name.StartsWith(prefix, StringComparison.Ordinal)).Sql;
    }
    var assembly = typeof(PostgresMigrationProvider).Assembly;
    var resource = assembly.GetManifestResourceNames()
      .Single(n => n.StartsWith("Whizbang.Data.Postgres.MigrationReversals." + prefix, StringComparison.Ordinal));
    using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
    return MigrationConstants.Apply(reader.ReadToEnd().Replace("__SCHEMA__", "public", StringComparison.Ordinal));
  }

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<bool> _existsAsync(NpgsqlConnection conn, string table) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT to_regclass('public.{table}') IS NOT NULL";
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }
}
