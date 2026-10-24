// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Issue #1719: when a model's generated table name changes (a suffix-stripping setting, the table-name attribute,
/// or the change that made the Dapper path name tables from the model), the table an earlier build created is renamed
/// in place before the perspective pass creates the new name, so its rows follow the model. The registry reconcile
/// used to try that rename after the pass, when the new name already existed as an empty table, and the rows were
/// left behind under the old name.
/// </summary>
/// <docs>fundamentals/perspectives/table-naming#renaming-perspectives</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class PerspectiveTableRenameTests : EFCoreTestBase {
  private const string LEGACY_TABLE = "wh_per_nested_retention_legacy";

  [Test]
  [Timeout(120000)]
  public async Task Initialize_WhenTheModelsTableNameChanged_RenamesTheTableInPlaceAndKeepsItsRowsAsync(
      CancellationToken cancellationToken) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync(cancellationToken);
    var (model, table) = await _registeredAsync(db);
    var id = Guid.CreateVersion7();

    // Arrange: the database an earlier build left, whose generated name for this model was LEGACY_TABLE.
    await db.ExecuteAsync($"ALTER TABLE {table} RENAME TO {LEGACY_TABLE}");
    await db.ExecuteAsync(
      $"INSERT INTO {LEGACY_TABLE} (id, data, metadata, scope, created_at, updated_at, version) " +
      "VALUES (@id, '{\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Total\":5}'::jsonb, '{}'::jsonb, '{}'::jsonb, now(), now(), 1)",
      new { id });
    await db.ExecuteAsync("UPDATE wh_perspective_registry SET table_name = @LEGACY_TABLE WHERE clr_type_name = @model",
      new { LEGACY_TABLE, model });
    await db.ExecuteAsync("DELETE FROM wh_schema_migrations WHERE file_name = @key", new { key = $"perspective:{table}" });

    await using var context = CreateDbContext();
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    await Assert.That(await db.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table} WHERE id = @id", new { id }))
      .IsEqualTo(1).Because("the table was renamed to the model's current name with its rows");
    await Assert.That(await db.ExecuteScalarAsync<string?>("SELECT to_regclass(@LEGACY_TABLE)::text", new { LEGACY_TABLE }))
      .IsNull().Because("nothing is left under the old name");
    await Assert.That(await db.ExecuteScalarAsync<string>(
      "SELECT table_name FROM wh_perspective_registry WHERE clr_type_name = @model", new { model })).IsEqualTo(table);
  }

  [Test]
  [Timeout(120000)]
  public async Task Initialize_WhenBothTablesExist_LeavesBothInPlaceAsync(CancellationToken cancellationToken) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync(cancellationToken);
    var (model, table) = await _registeredAsync(db);

    // Arrange: an orphan an earlier failed rename left, still holding rows, beside the model's current table.
    await db.ExecuteAsync($"CREATE TABLE {LEGACY_TABLE} (LIKE {table} INCLUDING ALL)");
    await db.ExecuteAsync(
      $"INSERT INTO {LEGACY_TABLE} (id, data, metadata, scope, created_at, updated_at, version) " +
      "VALUES (@id, '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, now(), now(), 1)", new { id = Guid.CreateVersion7() });
    await db.ExecuteAsync("UPDATE wh_perspective_registry SET table_name = @LEGACY_TABLE WHERE clr_type_name = @model",
      new { LEGACY_TABLE, model });
    await db.ExecuteAsync("DELETE FROM wh_schema_migrations WHERE file_name = @key", new { key = $"perspective:{table}" });

    await using var context = CreateDbContext();
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    await Assert.That(await db.ExecuteScalarAsync<int>($"SELECT count(*) FROM {LEGACY_TABLE}")).IsEqualTo(1)
      .Because("nothing is dropped or merged when both tables exist");
    await Assert.That(await db.ExecuteScalarAsync<string?>("SELECT to_regclass(@table)::text", new { table }))
      .IsEqualTo(table);
  }

  private static async Task<(string Model, string Table)> _registeredAsync(NpgsqlConnection db) {
    var row = await db.QuerySingleAsync<(string Model, string Table)>(
      "SELECT clr_type_name, table_name FROM wh_perspective_registry WHERE clr_type_name LIKE '%NestedRetentionOwner+Model'");
    return row;
  }
}
