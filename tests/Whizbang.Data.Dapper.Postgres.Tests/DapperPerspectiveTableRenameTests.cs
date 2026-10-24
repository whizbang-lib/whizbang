// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1719: the Dapper path now names a perspective's table from its model, as the EF Core path does, so a
/// table created under the old perspective-derived name must be followed to the new name by the perspective registry
/// reconcile (keyed by the model's CLR type), keeping its rows.
/// </summary>
/// <docs>extending/source-generators/configuration#table-names</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
[NotInParallel(nameof(PerspectiveRegistryPayloads))]
public class DapperPerspectiveTableRenameTests {
  private const string OLD_TABLE = "wh_per_order_summary_perspective";
  private const string NEW_TABLE = "wh_per_order_summary";
  private const string MODEL = "Probe.Contracts.OrderSummaryModel";
  private const string PERSPECTIVE = "Probe.Perspectives.OrderSummaryPerspective";
  private const string SERVICE = "RenameProbeService";

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dapper_rename");
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_database.Name);

  private static string _entry(string table) => $"""
    CREATE TABLE IF NOT EXISTS {table} (
      id UUID NOT NULL PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB,
      created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
      sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ, expires_at TIMESTAMPTZ, version INTEGER NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_{table}_created_at ON {table} (created_at);
    """;

  private static string _payload(string table) =>
    $$"""[{"ClrTypeName":"{{MODEL}}","TableName":"{{table}}","SchemaJson":{},"SchemaHash":"probe-hash","ServiceName":"{{SERVICE}}","PerspectiveName":"{{PERSPECTIVE}}"}]""";

  private PostgresSchemaInitializer _initializer(string table) =>
    new(_database.ConnectionString, [new("OrderSummaryPerspective", _entry(table))]) {
      PerspectiveRegistry = [new PerspectiveRegistryPayload(SERVICE, _payload(table))],
    };

  [Test]
  public async Task Initializer_WhenTheModelsTableNameChanges_RenamesTheExistingTableInPlaceAndKeepsItsRowsAsync() {
    await _initializer(OLD_TABLE).InitializeSchemaAsync();
    var id = Guid.CreateVersion7();
    await using (var seed = new NpgsqlConnection(_database.ConnectionString)) {
      await seed.ExecuteAsync(
        $"INSERT INTO {OLD_TABLE} (id, data, metadata, created_at, updated_at, version) " +
        "VALUES (@id, '{\"Total\":42}'::jsonb, '{}'::jsonb, now(), now(), 1)", new { id });
    }

    await _initializer(NEW_TABLE).InitializeSchemaAsync();

    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await Assert.That(await db.ExecuteScalarAsync<int>($"SELECT count(*) FROM {NEW_TABLE} WHERE id = @id", new { id }))
      .IsEqualTo(1).Because("the registry follows the model to its new table name and renames the table in place");
    await Assert.That(await db.ExecuteScalarAsync<string?>("SELECT to_regclass(@t)::text", new { t = OLD_TABLE }))
      .IsNull().Because("nothing is left behind under the old name");
    await Assert.That(await db.ExecuteScalarAsync<string>(
      "SELECT table_name FROM wh_perspective_registry WHERE clr_type_name = @MODEL", new { MODEL }))
      .IsEqualTo(NEW_TABLE);
  }
}
