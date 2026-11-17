// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// <para>
/// <c>wh_receptor_processing</c> is dropped (migration 477), though migration 031 indexes it at the top level. The
/// bootstrap creates the table only until the drop migration has recorded its current hash: a database migrating from
/// empty gets it in time for 031 and loses it at 477, and a pass that re-runs 031 (the drop's hash is stale then too)
/// gets it again and loses it again. Once the drop is recorded, the bootstrap leaves it alone.
/// </para>
/// </summary>
/// <docs>operations/infrastructure/migrations#unused-tables-are-dropped</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/RetiredReceptorTable.cs</code-under-test>
public partial class UnusedTablesAreDroppedTests {
  private const string RECEPTOR_TABLE = "wh_receptor_processing";
  private const string RECEPTOR_DROP = "%ReceptorProcessingTableIsDropped%";

  [Test]
  public async Task AFreshDatabase_EndsWithNoReceptorProcessingTableAsync(CancellationToken cancellationToken) {
    await using var db = await FreshDatabase.CreateAsync(cancellationToken);

    await Assert.That(await db.ScalarAsync<bool>($"SELECT to_regclass('public.{RECEPTOR_TABLE}') IS NULL")).IsTrue()
      .Because("the bootstrap created it for migration 031, and the drop migration removed it");
    await Assert.That(await db.ScalarAsync<long>(
        $"SELECT count(*) FROM wh_schema_migrations WHERE file_name LIKE '{RECEPTOR_DROP}'")).IsEqualTo(1L)
      .Because("positive control: the drop migration ran and is recorded");
  }

  [Test]
  public async Task AnUpgradedDatabase_EndsWithNoReceptorProcessingTableAsync(CancellationToken cancellationToken) {
    await using var db = await FreshDatabase.CreateAsync(cancellationToken);
    // As a release before the drop left it: the table with its rows, and no ledger row for the drop.
    await db.ExecuteAsync($"""
      CREATE TABLE {RECEPTOR_TABLE} (id UUID PRIMARY KEY, event_id UUID NOT NULL, receptor_name TEXT NOT NULL,
        stream_id UUID NOT NULL, instance_id UUID, lease_expiry TIMESTAMPTZ, completed_at TIMESTAMPTZ);
      INSERT INTO {RECEPTOR_TABLE} (id, event_id, receptor_name, stream_id) VALUES (gen_random_uuid(), gen_random_uuid(), 'R', gen_random_uuid());
      DELETE FROM wh_schema_migrations WHERE file_name LIKE '{RECEPTOR_DROP}';
      """);

    await db.InitializeAsync(cancellationToken);

    await Assert.That(await db.ScalarAsync<bool>($"SELECT to_regclass('public.{RECEPTOR_TABLE}') IS NULL")).IsTrue()
      .Because("the upgrade's pass runs the drop migration, which drops the table whatever it holds");
  }

  [Test]
  public async Task ReRunningTheBootstrapAfterTheDrop_DoesNotRecreateTheReceptorProcessingTableAsync(CancellationToken cancellationToken) {
    await using var db = await FreshDatabase.CreateAsync(cancellationToken);
    // A later release changed an unrelated migration, so a pass runs: the bootstrap first, then that one migration.
    await db.ExecuteAsync("UPDATE wh_schema_migrations SET content_hash = 'an-earlier-release' WHERE file_name LIKE '459\\_%'");

    await db.InitializeAsync(cancellationToken);

    await Assert.That(await db.ScalarAsync<string>("SELECT status_description FROM wh_schema_migrations WHERE file_name LIKE '459\\_%'"))
      .StartsWith("Updated from hash").Because("positive control: the pass ran, and the bootstrap with it");
    await Assert.That(await db.ScalarAsync<bool>($"SELECT to_regclass('public.{RECEPTOR_TABLE}') IS NULL")).IsTrue()
      .Because("the drop is recorded with its current hash, so the bootstrap no longer creates the table, and nothing drops it again");
  }

  [Test]
  public async Task AReplayThatRunsMigration031Again_GetsTheTableForIt_AndEndsWithoutItAsync(CancellationToken cancellationToken) {
    await using var db = await FreshDatabase.CreateAsync(cancellationToken);
    // The upgrade shape for 031 and the drop at once: both hashes are stale, so both run.
    await db.ExecuteAsync($"UPDATE wh_schema_migrations SET content_hash = 'an-earlier-release' WHERE file_name LIKE '031\\_%' OR file_name LIKE '{RECEPTOR_DROP}'");

    await db.InitializeAsync(cancellationToken);

    await Assert.That(await db.ScalarAsync<long>(
        "SELECT count(*) FROM wh_schema_migrations WHERE (file_name LIKE '031\\_%' OR file_name LIKE '" + RECEPTOR_DROP + "') AND content_hash = 'an-earlier-release'"))
      .IsEqualTo(0L).Because("031 found the table it indexes, and the drop ran after it");
    await Assert.That(await db.ScalarAsync<bool>($"SELECT to_regclass('public.{RECEPTOR_TABLE}') IS NULL")).IsTrue();
  }

  [Test]
  public async Task TheReceptorTableDropReverses_AndAppliesAgainAsync(CancellationToken cancellationToken) {
    await using var db = await FreshDatabase.CreateAsync(cancellationToken);

    await db.ExecuteAsync(_migrationSql("477_", down: true));

    await Assert.That(await db.ScalarAsync<long>(
        $"SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{RECEPTOR_TABLE}'"))
      .IsEqualTo(14L).Because("the reverse restores the table as the bootstrap of an earlier release created it");
    await Assert.That(await db.ScalarAsync<bool>("SELECT to_regclass('public.idx_receptor_processing_claim') IS NOT NULL")).IsTrue()
      .Because("and the index migration 031 gave it");

    await db.ExecuteAsync(_migrationSql("477_", down: false));

    await Assert.That(await db.ScalarAsync<bool>($"SELECT to_regclass('public.{RECEPTOR_TABLE}') IS NULL")).IsTrue();
  }

  /// <summary>A database of its own, migrated from empty through the EF Core driver in <c>public</c>.</summary>
  private sealed class FreshDatabase : IAsyncDisposable {
    private readonly string _name;
    private readonly string _connectionString;

    public FreshDatabase(string name, string connectionString) {
      _name = name;
      _connectionString = connectionString;
    }

    public static async Task<FreshDatabase> CreateAsync(CancellationToken cancellationToken) {
      var database = await PerTestDatabaseFactory.CreateAsync("unusedtables", cancellationToken: cancellationToken);
      var fresh = new FreshDatabase(database.Name, database.ConnectionString);
      await fresh.InitializeAsync(cancellationToken);
      return fresh;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken) {
      await using var context = new MinimalDbContext(new DbContextOptionsBuilder<MinimalDbContext>()
        .UseNpgsql(_connectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
        .Options);
      await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
    }

    public async Task ExecuteAsync(string sql) {
      await using var conn = new NpgsqlConnection(_connectionString);
      await conn.OpenAsync();
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = sql;
      await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql) {
      await using var conn = new NpgsqlConnection(_connectionString);
      await conn.OpenAsync();
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = sql;
      return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync() => await PerTestDatabaseFactory.DropAsync(_name);
  }
}
