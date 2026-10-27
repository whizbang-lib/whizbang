// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// How the Dapper driver classifies a changed perspective table when the existing columns are spelled the way
/// the database reports them and the new DDL spells them the way an author writes them (#1826). Adding a column
/// to a table with timestamp, varchar, integer, boolean, numeric and array columns is additive (column copy),
/// re-spelling the same types as their aliases changes nothing structural (DDL re-executed in place), and a real
/// type change is still destructive (event replay).
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
public class PostgresSchemaInitializerTypeCompareTests : IAsyncDisposable {
  // The generator's perspective snapshot shape, plus physical columns in the types the generator and authors use.
  private const string BASE_COLUMNS =
    "id UUID PRIMARY KEY DEFAULT gen_random_uuid(), " +
    "data JSONB NOT NULL, " +
    "metadata JSONB NOT NULL, " +
    "scope JSONB, " +
    "created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), " +
    "updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), " +
    "version BIGINT NOT NULL DEFAULT 0, " +
    "name VARCHAR(100), " +
    "quantity INT, " +
    "active BOOL DEFAULT TRUE, " +
    "amount NUMERIC(10, 2), " +
    "seen_at TIMESTAMP, " +
    "tags TEXT[]";

  private string? _testDatabaseName;
  private string? _connectionString;
  private string _testConnectionString => _connectionString ?? throw new InvalidOperationException("Test not initialized");

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _testDatabaseName = $"test_{Guid.NewGuid():N}";

    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
#pragma warning disable S2077 // The database name is the test's own generated test_<guid>; a database name cannot be a query parameter.
    await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");
#pragma warning restore S2077

    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName
    };
    _connectionString = builder.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_testDatabaseName != null) {
      await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await adminConnection.OpenAsync();
#pragma warning disable S2077 // The database name is the test's own generated test_<guid>; a database name cannot be a query parameter.
      await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName} WITH (FORCE)");
#pragma warning restore S2077

      _testDatabaseName = null;
      _connectionString = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private static KeyValuePair<string, string>[] _perspective(string table, string columns) =>
    [new KeyValuePair<string, string>("TypeCompare", $"CREATE TABLE IF NOT EXISTS {table} ({columns});")];

  private async Task<dynamic> _recordAsync() {
    await using var connection = new NpgsqlConnection(_testConnectionString);
    await connection.OpenAsync();
    return await connection.QuerySingleAsync<dynamic>(
      "SELECT status, status_description FROM wh_schema_migrations WHERE file_name = 'perspective:TypeCompare'");
  }

  /// <summary>
  /// The issue's case: a perspective table with timestamp and varchar columns gains one column. The existing
  /// columns are read back as <c>timestamp with time zone</c> and <c>character varying(100)</c> while the DDL
  /// says <c>TIMESTAMPTZ</c> and <c>VARCHAR(100)</c>; they are the same types, so the change is additive and the
  /// table is migrated by column copy with its rows kept, not handed to an event replay.
  /// </summary>
  [Test]
  public async Task AddedColumn_OnTableWithAliasedTypes_IsColumnCopyNotEventReplayAsync() {
    await new PostgresSchemaInitializer(_testConnectionString, _perspective("wh_per_typecmp_add", BASE_COLUMNS))
      .InitializeSchemaAsync();
    await using (var seed = new NpgsqlConnection(_testConnectionString)) {
      await seed.OpenAsync();
      await seed.ExecuteAsync(
        "INSERT INTO wh_per_typecmp_add (data, metadata, name, quantity) VALUES ('{}', '{}', 'kept', 7)");
    }

    var changed = new PostgresSchemaInitializer(_testConnectionString,
      _perspective("wh_per_typecmp_add", BASE_COLUMNS + ", added_note TEXT"));

    var plan = await changed.PreviewAsync();
    var step = plan.Steps.Single(s => s.Name == "perspective:TypeCompare");
    await Assert.That(step.Action).IsEqualTo(MigrationAction.BlueGreenColumnCopy)
      .Because("every existing column keeps its type; only one column is added");
    await Assert.That(step.AddedColumns).IsEquivalentTo(["added_note"]);
    await Assert.That(step.RemovedColumns).IsNull();

    await changed.InitializeSchemaAsync();
    var record = await _recordAsync();
    await Assert.That((int)record.status).IsEqualTo(2);
    await Assert.That((string)record.status_description).Contains("ColumnCopy");

    await using var connection = new NpgsqlConnection(_testConnectionString);
    await connection.OpenAsync();
    var kept = await connection.QuerySingleAsync<dynamic>("SELECT name, quantity, added_note FROM wh_per_typecmp_add");
    await Assert.That((string)kept.name).IsEqualTo("kept");
    await Assert.That((int)kept.quantity).IsEqualTo(7);
    await Assert.That((string?)kept.added_note).IsNull();
  }

  /// <summary>
  /// The same columns re-spelled as their long forms or other aliases (plus an index, so the DDL hash changes)
  /// are the same structure: the DDL is re-executed in place, neither copied nor replayed.
  /// </summary>
  [Test]
  public async Task SameColumnsSpelledAsAliases_IsDirectDdlAsync() {
    await new PostgresSchemaInitializer(_testConnectionString, _perspective("wh_per_typecmp_alias", BASE_COLUMNS))
      .InitializeSchemaAsync();

    const string respelled =
      "id uuid NOT NULL PRIMARY KEY, " +
      "data jsonb NOT NULL, " +
      "metadata jsonb NOT NULL, " +
      "scope jsonb, " +
      "created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(), " +
      "updated_at timestamp with time zone NOT NULL DEFAULT NOW(), " +
      "version INT8 NOT NULL DEFAULT 0, " +
      "name CHARACTER VARYING(100), " +
      "quantity INTEGER, " +
      "active BOOLEAN DEFAULT TRUE, " +
      "amount DECIMAL(10,2), " +
      "seen_at TIMESTAMP WITHOUT TIME ZONE, " +
      "tags text[]";
    var changed = new PostgresSchemaInitializer(_testConnectionString, [
      new KeyValuePair<string, string>("TypeCompare",
        $"CREATE TABLE IF NOT EXISTS wh_per_typecmp_alias ({respelled}); " +
        "CREATE INDEX IF NOT EXISTS ix_typecmp_alias_name ON wh_per_typecmp_alias (name);")
    ]);

    var plan = await changed.PreviewAsync();
    var step = plan.Steps.Single(s => s.Name == "perspective:TypeCompare");
    await Assert.That(step.Action).IsEqualTo(MigrationAction.Update)
      .Because("an alias names the same type, so no column changed");

    await changed.InitializeSchemaAsync();
    var record = await _recordAsync();
    await Assert.That((string)record.status_description).Contains("DirectDdl");
  }

  /// <summary>A real type change (TEXT to INTEGER) is still destructive and handed to an event replay.</summary>
  [Test]
  public async Task TextToInteger_IsStillEventReplayAsync() {
    await new PostgresSchemaInitializer(_testConnectionString,
      _perspective("wh_per_typecmp_real", BASE_COLUMNS + ", code TEXT")).InitializeSchemaAsync();

    var changed = new PostgresSchemaInitializer(_testConnectionString,
      _perspective("wh_per_typecmp_real", BASE_COLUMNS + ", code INTEGER"));

    var plan = await changed.PreviewAsync();
    var step = plan.Steps.Single(s => s.Name == "perspective:TypeCompare");
    await Assert.That(step.Action).IsEqualTo(MigrationAction.BlueGreenEventReplay);

    await changed.InitializeSchemaAsync();
    var record = await _recordAsync();
    await Assert.That((int)record.status).IsEqualTo(4);
  }

  /// <summary>
  /// A changed length is a changed type: VARCHAR(100) to VARCHAR(20) could not copy every row, so it stays
  /// destructive rather than being treated as an alias of the old column.
  /// </summary>
  [Test]
  public async Task VarcharLengthChange_IsStillEventReplayAsync() {
    await new PostgresSchemaInitializer(_testConnectionString, _perspective("wh_per_typecmp_len", BASE_COLUMNS))
      .InitializeSchemaAsync();

    var plan = await new PostgresSchemaInitializer(_testConnectionString,
      _perspective("wh_per_typecmp_len", BASE_COLUMNS.Replace("VARCHAR(100)", "VARCHAR(20)"))).PreviewAsync();

    var step = plan.Steps.Single(s => s.Name == "perspective:TypeCompare");
    await Assert.That(step.Action).IsEqualTo(MigrationAction.BlueGreenEventReplay);
  }
}
