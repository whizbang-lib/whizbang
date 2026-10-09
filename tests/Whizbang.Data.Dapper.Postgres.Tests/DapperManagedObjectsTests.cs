// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper driver runs the same managed-object reconcile as the EF Core driver: an index its perspectives no
/// longer declare is recorded on the first start that sees it and dropped on the next; a foreign one is kept.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
public class DapperManagedObjectsTests {
  private const string TABLE = "wh_per_probe";
  private const string ENTRY = $"""
    CREATE TABLE IF NOT EXISTS {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_{TABLE}_status ON {TABLE} ((data ->> 'Status'));
    """;

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"test_{Guid.NewGuid():N}";
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await admin.ExecuteAsync($"CREATE DATABASE {_databaseName}");
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
    }.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
  }

  private Task _initializeAsync() => new PostgresSchemaInitializer(
      _connectionString, [new KeyValuePair<string, string>("ProbePerspective", ENTRY)]) {
    ManagedObjects = ManagedSchemaObjectSet.FromDeclarations([(TABLE, "index", $"ix_{TABLE}_status", "ProbeModel")]),
  }.InitializeSchemaAsync();

  private async Task<List<string>> _indexesAsync() {
    await using var db = new NpgsqlConnection(_connectionString);
    return [.. await db.QueryAsync<string>(
      "SELECT indexname FROM pg_indexes WHERE tablename = @t AND indexname <> @pk ORDER BY indexname",
      new { t = TABLE, pk = $"{TABLE}_pkey" })];
  }

  [Test]
  public async Task AnIndexNoLongerDeclared_IsDroppedOnTheStartAfterItIsSeen_AndAForeignOneIsKeptAsync() {
    await _initializeAsync();
    await using (var db = new NpgsqlConnection(_connectionString)) {
      await db.ExecuteAsync($"""
        CREATE INDEX ix_{TABLE}_old ON {TABLE} USING gin (data);
        CREATE INDEX reporting_probe ON {TABLE} ((data ->> 'Code'));
        """);
    }

    await _initializeAsync();
    await Assert.That(await _indexesAsync()).Contains($"ix_{TABLE}_old")
      .Because("the start that first sees it records it and drops nothing");

    await _initializeAsync();

    await Assert.That(await _indexesAsync()).IsEquivalentTo([$"ix_{TABLE}_status", "reporting_probe"]);
  }

  [Test]
  public async Task WithoutADeclaration_NothingIsReconciledAsync() {
    await new PostgresSchemaInitializer(_connectionString, [new KeyValuePair<string, string>("ProbePerspective", ENTRY)])
      .InitializeSchemaAsync();

    await using var db = new NpgsqlConnection(_connectionString);
    await Assert.That(await db.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_managed_objects")).IsEqualTo(0L);
  }
}
