// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;
using Whizbang.Generators.Shared.Models;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1023: a perspective's declared storage options reach its table through the schema pass, a second
/// pass changes nothing, and a declared size budget is enforced on write.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-storage</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class JsonbColumnStorageIntegrationTests {
  private const string TABLE = "wh_per_jsonb_extracted_item";

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("jsonb_store");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await _initializeAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  [Test]
  public async Task DeclaredOptions_AreAppliedToTheTableAsync() {
    await Assert.That(await _scalarAsync($"SELECT attstorage FROM pg_attribute WHERE attrelid = '{TABLE}'::regclass AND attname = 'grid_filter'"))
      .IsEqualTo("m").Because("the filter column is kept in the row: SET STORAGE MAIN.");
    await Assert.That(await _scalarAsync($"SELECT attcompression FROM pg_attribute WHERE attrelid = '{TABLE}'::regclass AND attname = 'data'"))
      .IsEqualTo("l").Because("the document is compressed with lz4.");
    await Assert.That(await _scalarAsync($"SELECT array_to_string(reloptions, ',') FROM pg_class WHERE oid = '{TABLE}'::regclass"))
      .Contains("toast_tuple_target=512");
    await Assert.That(await _scalarAsync($"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_{TABLE}_grid_filter_size'"))
      .Contains("pg_column_size(grid_filter) <= 4096");
  }

  /// <summary>
  /// The statements that applied the options, run again, alter nothing: each compares the catalog first, so
  /// a restart takes no lock on the table. An event trigger records every DDL command the second run issues.
  /// </summary>
  [Test]
  public async Task TheStatements_RunAgain_AlterNothingAsync() {
    var field = new PhysicalFieldInfo(
      "GridFilter", "grid_filter", "x", IsIndexed: false, IsUnique: false, MaxLength: null, IsVector: false,
      VectorDimensions: null, VectorDistanceMetric: null, VectorIndexType: null, VectorIndexLists: null,
      ColumnType: "jsonb", Storage: "MAIN", MaxBytes: 4096);
    var statements = ColumnStorageSql.ForTable(TABLE, new TableStorageInfo("lz4", 512))
      .Concat(ColumnStorageSql.ForColumn(TABLE, TABLE, field));

    await _execAsync("""
      CREATE TABLE ddl_seen (tag text, identity text);
      CREATE FUNCTION ddl_record() RETURNS event_trigger LANGUAGE plpgsql AS $$
      BEGIN
        INSERT INTO ddl_seen SELECT command_tag, object_identity FROM pg_event_trigger_ddl_commands();
      END $$;
      CREATE EVENT TRIGGER ddl_watch ON ddl_command_end EXECUTE FUNCTION ddl_record();
      """);

    await _execAsync(string.Join('\n', statements));

    await Assert.That(await _scalarAsync("SELECT count(*) FROM ddl_seen")).IsEqualTo("0");

    // And the same statements against a table that differs do alter it, so the zero above is a comparison
    // that held rather than statements that never run.
    await _execAsync($"ALTER TABLE {TABLE} ALTER COLUMN grid_filter SET STORAGE EXTENDED; TRUNCATE ddl_seen;");
    await _execAsync(string.Join('\n', statements));
    await Assert.That(await _scalarAsync("SELECT count(*) FROM ddl_seen WHERE tag = 'ALTER TABLE'")).IsEqualTo("1");
  }

  /// <summary>A budget declared with a different size replaces the constraint the earlier declaration left.</summary>
  [Test]
  public async Task AChangedBudget_ReplacesTheConstraintAsync() {
    await _execAsync($"""
      ALTER TABLE {TABLE} DROP CONSTRAINT ck_{TABLE}_grid_filter_size;
      ALTER TABLE {TABLE} ADD CONSTRAINT ck_{TABLE}_grid_filter_size CHECK (pg_column_size(grid_filter) <= 10) NOT VALID;
      DELETE FROM wh_schema_migrations WHERE file_name = 'perspective:{TABLE}';
      """);

    await _initializeAsync();

    await Assert.That(await _scalarAsync($"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_{TABLE}_grid_filter_size'"))
      .Contains("<= 4096");
  }

  /// <summary>A value larger than the declared budget is refused with a check violation naming the column's constraint.</summary>
  [Test]
  public async Task AValueOverItsBudget_IsRefusedOnWriteAsync() {
    var id = Guid.NewGuid();
    var model = new JsonbExtractedItem.Model {
      Id = id,
      GridFilter = new() { ["region"] = [.. Enumerable.Range(0, 2000).Select(i => "value-" + i)] },
    };

    await using var context = PhysicalJsonbContainmentIntegrationTests.Context(_connectionString);
    var store = new EFCorePostgresPerspectiveStore<JsonbExtractedItem.Model>(context, TABLE);

    var refused = await Assert.ThrowsAsync<PostgresException>(() =>
      store.UpsertWithPhysicalFieldsAsync(id, model, new Dictionary<string, object?> { ["grid_filter"] = model.GridFilter }));

    await Assert.That(refused!.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
    await Assert.That(refused.ConstraintName).IsEqualTo($"ck_{TABLE}_grid_filter_size");
  }

  /// <summary>
  /// A table an earlier release created with a TEXT column for a field that is now jsonb: the text column is
  /// kept under a legacy name, the jsonb column is added and filled from the document's copy, and a second
  /// start changes nothing.
  /// </summary>
  [Test]
  public async Task AnEarlierTextColumn_IsKeptAsLegacy_AndTheJsonbColumnFilledFromTheDocumentAsync() {
    var id = Guid.NewGuid();
    const string DOCUMENT = """{"GridFilter": {"region": ["north"]}}""";
    const string EMPTY = "{}";
    await _execAsync($"""
      ALTER TABLE {TABLE} DROP COLUMN grid_filter;
      ALTER TABLE {TABLE} ADD COLUMN grid_filter TEXT;
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version, grid_filter, labels, tags, stamp)
      VALUES ('{id}', '{DOCUMENT}', '{EMPTY}', '{EMPTY}', now(), now(), now(), now(), 1,
              'System.Collections.Generic.Dictionary`2[System.String,System.String[]]', '[]', '[]', now());
      DELETE FROM wh_schema_migrations WHERE file_name = 'perspective:{TABLE}';
      """);

    await _initializeAsync();

    await Assert.That(await _scalarAsync($"SELECT grid_filter_text_legacy FROM {TABLE} WHERE id = '{id}'"))
      .IsEqualTo("System.Collections.Generic.Dictionary`2[System.String,System.String[]]")
      .Because("the old column is renamed, never dropped.");
    await Assert.That(await _scalarAsync($"SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = '{TABLE}'::regclass AND attname = 'grid_filter'"))
      .IsEqualTo("jsonb");
    await Assert.That(await _scalarAsync($"SELECT grid_filter::text FROM {TABLE} WHERE id = '{id}'"))
      .IsEqualTo("{\"region\": [\"north\"]}");

    await _execAsync($"DELETE FROM wh_schema_migrations WHERE file_name = 'perspective:{TABLE}'");
    await _initializeAsync();

    await Assert.That(await _scalarAsync($"SELECT count(*) FROM pg_attribute WHERE attrelid = '{TABLE}'::regclass AND attname LIKE 'grid_filter%' AND NOT attisdropped"))
      .IsEqualTo("2").Because("a second start finds a jsonb column and renames nothing.");
  }

  /// <summary>A Split model's document has no copy: the text column is kept as legacy and the jsonb column starts empty.</summary>
  [Test]
  public async Task AnEarlierTextColumnOnASplitModel_IsKeptAsLegacy_AndLeftForARebuildAsync() {
    const string SPLIT = "wh_per_jsonb_split_item";
    const string EMPTY = "{}";
    var id = Guid.NewGuid();
    await _execAsync($"""
      ALTER TABLE {SPLIT} DROP COLUMN labels;
      ALTER TABLE {SPLIT} ADD COLUMN labels TEXT;
      INSERT INTO {SPLIT} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version, labels)
      VALUES ('{id}', '{EMPTY}', '{EMPTY}', '{EMPTY}', now(), now(), now(), now(), 1, 'System.Collections.Generic.List`1[Label]');
      DELETE FROM wh_schema_migrations WHERE file_name = 'perspective:{SPLIT}';
      """);

    await _initializeAsync();

    await Assert.That(await _scalarAsync($"SELECT labels_text_legacy FROM {SPLIT} WHERE id = '{id}'"))
      .IsEqualTo("System.Collections.Generic.List`1[Label]");
    await Assert.That(await _scalarAsync($"SELECT labels IS NULL FROM {SPLIT} WHERE id = '{id}'")).IsEqualTo("True");
  }

  private async Task _initializeAsync() {
    await using var context = PhysicalJsonbContainmentIntegrationTests.Context(_connectionString);
    await context.EnsureWhizbangDatabaseInitializedAsync();
  }

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "<null>";
  }
}
