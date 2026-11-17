// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper driver's side of the dropped <c>wh_receptor_processing</c> (migration 477): its bootstrap creates the table
/// only until the drop migration has recorded its current hash, so a database migrating from empty ends without it, and
/// a pass that runs migration 031 again (which indexes it) gets it in time and loses it again.
/// </summary>
/// <docs>operations/infrastructure/migrations#unused-tables-are-dropped</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/RetiredReceptorTable.cs</code-under-test>
[Category("Integration")]
public class DapperRetiredReceptorTableTests : IAsyncDisposable {
  private const string DROP = "%ReceptorProcessingTableIsDropped%";
  private string? _databaseName;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"retired_{Guid.NewGuid():N}";
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await admin.ExecuteAsync($"CREATE DATABASE {_databaseName}");
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
    }.ConnectionString;
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
      } catch (NpgsqlException) {
        // The container is shared and the database is per-test; a failed drop is not a test failure.
      }
    }
    GC.SuppressFinalize(this);
  }

  [Test]
  public async Task AFreshDatabase_EndsWithNoReceptorProcessingTableAsync() {
    await new PostgresSchemaInitializer(_connectionString).InitializeSchemaAsync();

    await using var conn = new NpgsqlConnection(_connectionString);
    await Assert.That(await conn.ExecuteScalarAsync<bool>("SELECT to_regclass('wh_receptor_processing') IS NULL")).IsTrue()
      .Because("the bootstrap created it for migration 031, and the drop migration removed it");
    await Assert.That(await conn.ExecuteScalarAsync<long>($"SELECT count(*) FROM wh_schema_migrations WHERE file_name LIKE '{DROP}'"))
      .IsEqualTo(1L).Because("positive control: the drop ran and is recorded");
  }

  [Test]
  public async Task APassThatRunsMigration031Again_GetsTheTableForIt_AndEndsWithoutItAsync() {
    await new PostgresSchemaInitializer(_connectionString).InitializeSchemaAsync();
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.ExecuteAsync(
      $"UPDATE wh_schema_migrations SET content_hash = 'an-earlier-release' WHERE file_name LIKE '031\\_%' OR file_name LIKE '{DROP}'");

    await new PostgresSchemaInitializer(_connectionString).InitializeSchemaAsync();

    await Assert.That(await conn.ExecuteScalarAsync<long>(
        $"SELECT count(*) FROM wh_schema_migrations WHERE (file_name LIKE '031\\_%' OR file_name LIKE '{DROP}') AND content_hash = 'an-earlier-release'"))
      .IsEqualTo(0L).Because("031 found the table it indexes, and the drop ran after it");
    await Assert.That(await conn.ExecuteScalarAsync<bool>("SELECT to_regclass('wh_receptor_processing') IS NULL")).IsTrue();
  }

  [Test]
  public async Task ReRunningTheBootstrapAfterTheDrop_DoesNotRecreateItAsync() {
    await new PostgresSchemaInitializer(_connectionString).InitializeSchemaAsync();
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.ExecuteAsync("UPDATE wh_schema_migrations SET content_hash = 'an-earlier-release' WHERE file_name LIKE '459\\_%'");

    await new PostgresSchemaInitializer(_connectionString).InitializeSchemaAsync();

    await Assert.That(await conn.ExecuteScalarAsync<bool>("SELECT to_regclass('wh_receptor_processing') IS NULL")).IsTrue()
      .Because("the drop is recorded with its current hash, so the bootstrap no longer creates the table");
  }
}
