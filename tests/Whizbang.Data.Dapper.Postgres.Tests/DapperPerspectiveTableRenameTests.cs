// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.Logging;
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
/// <docs>fundamentals/perspectives/table-naming#renaming-perspectives</docs>
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

  private static string _entry(string table, string extra = "") => $"""
    CREATE TABLE IF NOT EXISTS {table} (
      id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
      data JSONB NOT NULL,
      metadata JSONB NOT NULL,
      scope JSONB,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      version BIGINT NOT NULL DEFAULT 0
    );
    CREATE INDEX IF NOT EXISTS ix_{table}_created_at ON {table} (created_at);
    CREATE INDEX IF NOT EXISTS idx_{table[7..]}_updated_at ON {table} (updated_at);
    {extra}
    """;

  private static string _payload(string table) =>
    $$"""[{"ClrTypeName":"{{MODEL}}","TableName":"{{table}}","SchemaJson":{},"SchemaHash":"probe-hash","ServiceName":"{{SERVICE}}","PerspectiveName":"{{PERSPECTIVE}}"}]""";

  private PostgresSchemaInitializer _initializer(string table, ILogger? logger = null, string extra = "") =>
    new(_database.ConnectionString, [new("OrderSummaryPerspective", _entry(table, extra))]) {
      PerspectiveRegistry = [new PerspectiveRegistryPayload(SERVICE, _payload(table))],
      Logger = logger,
    };

  private async Task<Guid> _seedAsync(string table, string total) {
    var id = Guid.CreateVersion7();
    await using var seed = new NpgsqlConnection(_database.ConnectionString);
    await seed.ExecuteAsync(
      $"INSERT INTO {table} (id, data, metadata, created_at, updated_at, version) " +
      $"VALUES (@id, '{{\"Total\":{total}}}'::jsonb, '{{}}'::jsonb, now(), now(), 1)", new { id });
    return id;
  }

  [Test]
  public async Task Initializer_WhenTheModelsTableNameChanges_RenamesTheExistingTableInPlaceAndKeepsItsRowsAsync() {
    await _initializer(OLD_TABLE).InitializeSchemaAsync();
    var id = await _seedAsync(OLD_TABLE, "42");

    await _initializer(NEW_TABLE).InitializeSchemaAsync();

    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await Assert.That(await db.ExecuteScalarAsync<int>($"SELECT count(*) FROM {NEW_TABLE} WHERE id = @id", new { id }))
      .IsEqualTo(1).Because("the registry follows the model to its new table name and renames the table in place");
    await Assert.That(await db.ExecuteScalarAsync<string?>("SELECT to_regclass(@t)::text", new { t = OLD_TABLE }))
      .IsNull().Because("nothing is left behind under the old name");
    await Assert.That(await db.ExecuteScalarAsync<string>(
      "SELECT table_name FROM wh_perspective_registry WHERE clr_type_name = @MODEL", new { MODEL }))
      .IsEqualTo(NEW_TABLE);
    await Assert.That(await db.ExecuteScalarAsync<int>($"SELECT count(*) FROM {NEW_TABLE}")).IsEqualTo(1)
      .Because("the rename neither copied nor replayed the rows into a duplicate");
    await Assert.That(await db.ExecuteScalarAsync<int>(
      "SELECT count(*) FROM pg_tables WHERE schemaname = current_schema() AND tablename LIKE '%\\_bak\\_%'")).IsEqualTo(0)
      .Because("a pure rename is not changed DDL, so no column-copy swap left a backup table");
    await Assert.That(await db.ExecuteScalarAsync<string>(
      "SELECT status_description FROM wh_schema_migrations WHERE file_name = 'perspective:OrderSummaryPerspective'"))
      .IsEqualTo($"Renamed from {OLD_TABLE}");
    await Assert.That(await db.ExecuteScalarAsync<int>(
      "SELECT count(*) FROM pg_indexes WHERE schemaname = current_schema() AND indexname LIKE @old",
      new { old = $"%{OLD_TABLE[7..]}%" })).IsEqualTo(0)
      .Because("the table's indexes, its primary key and those named after the part past wh_per_ included, are renamed with it");
    await Assert.That(await db.ExecuteScalarAsync<int>(
      "SELECT count(*) FROM pg_indexes WHERE schemaname = current_schema() AND tablename = @NEW_TABLE",
      new { NEW_TABLE })).IsEqualTo(3)
      .Because("the primary key and the two indexes, each once, under the new name");
  }

  [Test]
  public async Task Initializer_WhenTheTableIsRenamedAndItsDdlChanges_KeepsItsRowsOnceAsync() {
    await _initializer(OLD_TABLE).InitializeSchemaAsync();
    var id = await _seedAsync(OLD_TABLE, "7");

    await _initializer(NEW_TABLE, extra: $"CREATE INDEX IF NOT EXISTS ix_{NEW_TABLE}_updated_at ON {NEW_TABLE} (updated_at);")
      .InitializeSchemaAsync();

    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await Assert.That(await db.ExecuteScalarAsync<int>($"SELECT count(*) FROM {NEW_TABLE} WHERE id = @id", new { id }))
      .IsEqualTo(1).Because("a rename that also changes the DDL still keeps the rows, through the changed-table path");
    await Assert.That(await db.ExecuteScalarAsync<int>($"SELECT count(*) FROM {NEW_TABLE}")).IsEqualTo(1);
    await Assert.That(await db.ExecuteScalarAsync<string>(
      "SELECT status_description FROM wh_schema_migrations WHERE file_name = 'perspective:OrderSummaryPerspective'"))
      .DoesNotStartWith("Renamed").Because("a DDL change beyond the name goes through the changed-table path");
    await Assert.That(await db.ExecuteScalarAsync<int>(
      "SELECT count(*) FROM pg_tables WHERE schemaname = current_schema() AND tablename LIKE '%\\_bak\\_%'")).IsEqualTo(0)
      .Because("the renamed table was not swapped out by a column copy");
  }

  [Test]
  public async Task Initializer_WhenBothTablesExist_LeavesBothAndWarnsAsync() {
    await _initializer(OLD_TABLE).InitializeSchemaAsync();
    var oldId = await _seedAsync(OLD_TABLE, "1");
    await using (var db = new NpgsqlConnection(_database.ConnectionString)) {
      // A table an earlier rename already orphaned: the new name exists too, with rows of its own.
      await db.ExecuteAsync(_entry(NEW_TABLE));
    }
    var newId = await _seedAsync(NEW_TABLE, "2");
    var logger = new RecordingLogger();

    await _initializer(NEW_TABLE, logger).InitializeSchemaAsync();

    await using var check = new NpgsqlConnection(_database.ConnectionString);
    await Assert.That(await check.ExecuteScalarAsync<int>($"SELECT count(*) FROM {OLD_TABLE} WHERE id = @oldId", new { oldId }))
      .IsEqualTo(1).Because("nothing is dropped or merged when both tables exist");
    await Assert.That(await check.ExecuteScalarAsync<int>($"SELECT count(*) FROM {NEW_TABLE}")).IsEqualTo(1);
    await Assert.That(await check.ExecuteScalarAsync<int>($"SELECT count(*) FROM {NEW_TABLE} WHERE id = @newId", new { newId }))
      .IsEqualTo(1);
    var warning = logger.Warnings.SingleOrDefault(m => m.Contains(MODEL, StringComparison.Ordinal));
    await Assert.That(warning).IsNotNull().Because("an operator is told which model has two tables");
    await Assert.That(warning!).Contains(OLD_TABLE);
    await Assert.That(warning!).Contains(NEW_TABLE);
  }

  // A released Dapper build: the table named from the perspective class, and no registry row at all (that build never
  // reconciled the registry, #1548).
  private async Task _releasedBuildAsync(string table, string key = "OrderSummaryPerspective") =>
    await new PostgresSchemaInitializer(_database.ConnectionString, [new(key, _entry(table))])
      .InitializeSchemaAsync();

  private static string _entryJson(string table, string perspective, string? former, string model = MODEL) =>
    $$"""{"ClrTypeName":"{{model}}","TableName":"{{table}}","SchemaJson":{},"SchemaHash":"probe-hash","ServiceName":"{{SERVICE}}","PerspectiveName":"{{perspective}}"{{(former is null ? "" : $",\"FormerTableName\":\"{former}\"")}}}""";

  private PostgresSchemaInitializer _upgraded(string json, ILogger? logger = null) =>
    new(_database.ConnectionString, [new("OrderSummaryPerspective", _entry(NEW_TABLE))]) {
      PerspectiveRegistry = [new PerspectiveRegistryPayload(SERVICE, json)],
      Logger = logger,
    };

  [Test]
  public async Task Initializer_WhenAReleasedBuildLeftTheTableUnderItsPerspectiveName_FollowsItWithNoRegistryRowAsync() {
    await _releasedBuildAsync(OLD_TABLE);
    var id = await _seedAsync(OLD_TABLE, "9");

    await _upgraded($"[{_entryJson(NEW_TABLE, PERSPECTIVE, OLD_TABLE)}]").InitializeSchemaAsync();

    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await Assert.That(await db.ExecuteScalarAsync<int>($"SELECT count(*) FROM {NEW_TABLE} WHERE id = @id", new { id }))
      .IsEqualTo(1).Because("the payload names the table the released build created, and no registry row has to");
    await Assert.That(await db.ExecuteScalarAsync<string?>("SELECT to_regclass(@t)::text", new { t = OLD_TABLE }))
      .IsNull().Because("the table was renamed, not copied");
    await Assert.That(await db.ExecuteScalarAsync<int>(
      "SELECT count(*) FROM pg_indexes WHERE schemaname = current_schema() AND indexname LIKE @old",
      new { old = $"%{OLD_TABLE[7..]}%" })).IsEqualTo(0)
      .Because("its indexes follow it");
  }

  [Test]
  public async Task Initializer_WhenTwoPerspectivesOfOneModelLeftTwoTables_RenamesTheRegisteredOneAndWarnsAboutTheOtherAsync() {
    const string OTHER_TABLE = "wh_per_order_audit_perspective";
    await _releasedBuildAsync(OTHER_TABLE, "OrderAuditPerspective");
    var otherId = await _seedAsync(OTHER_TABLE, "3");
    await _initializer(OLD_TABLE).InitializeSchemaAsync();
    var id = await _seedAsync(OLD_TABLE, "4");
    var logger = new RecordingLogger();

    await _upgraded(
      $"[{_entryJson(NEW_TABLE, "Probe.Perspectives.OrderAuditPerspective", OTHER_TABLE)},{_entryJson(NEW_TABLE, PERSPECTIVE, OLD_TABLE)}]",
      logger).InitializeSchemaAsync();

    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await Assert.That(await db.ExecuteScalarAsync<int>($"SELECT count(*) FROM {NEW_TABLE} WHERE id = @id", new { id }))
      .IsEqualTo(1).Because("the table the registry names for the model is the one renamed");
    await Assert.That(await db.ExecuteScalarAsync<int>($"SELECT count(*) FROM {OTHER_TABLE} WHERE id = @otherId", new { otherId }))
      .IsEqualTo(1).Because("the second perspective's table is neither dropped nor merged");
    var warning = logger.Warnings.SingleOrDefault(m => m.Contains(OTHER_TABLE, StringComparison.Ordinal));
    await Assert.That(warning).IsNotNull().Because("an operator is told the second table was left, and where its model now lives");
    await Assert.That(warning!).Contains(NEW_TABLE);
  }

  [Test]
  public async Task Initializer_WhenAFormerNameIsAnotherModelsTable_LeavesItToThatModelAsync() {
    const string OTHER_MODEL = "Probe.Contracts.OrderSummaryPerspectiveModel";
    await _releasedBuildAsync(OLD_TABLE);
    var id = await _seedAsync(OLD_TABLE, "5");

    await _upgraded(
      $"[{_entryJson(NEW_TABLE, PERSPECTIVE, OLD_TABLE)},{_entryJson(OLD_TABLE, "Probe.Perspectives.Other", null, OTHER_MODEL)}]")
      .InitializeSchemaAsync();

    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await Assert.That(await db.ExecuteScalarAsync<int>($"SELECT count(*) FROM {OLD_TABLE} WHERE id = @id", new { id }))
      .IsEqualTo(1).Because("a name the build now gives another model is that model's table, not a former one to take");
  }

  [Test]
  [Arguments("wh_per_order_summary", "wh_per_order_summary_perspective",
    "CREATE INDEX idx_order_summary_x ON wh_per_order_summary (id);",
    "CREATE INDEX idx_order_summary_perspective_x ON wh_per_order_summary_perspective (id);")]
  [Arguments("wh_per_order_summary", "legacy_orders",
    "CREATE INDEX idx_order_summary_x ON wh_per_order_summary (id);",
    "CREATE INDEX idx_order_summary_x ON legacy_orders (id);")]
  [Arguments("orders", "wh_per_orders_old",
    "CREATE INDEX idx_order_summary_x ON orders (id);",
    "CREATE INDEX idx_order_summary_x ON wh_per_orders_old (id);")]
  public async Task AsNamed_RewritesTheNameAndThePartPastThePrefix_OnlyWhenBothCarryItAsync(
      string table, string previous, string ddl, string expected) {
    await Assert.That(PostgresSchemaInitializer.AsNamed(ddl, table, previous)).IsEqualTo(expected);
  }

  private sealed class RecordingLogger : ILogger {
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _warnings = new();
    public IReadOnlyList<string> Warnings => [.. _warnings];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        _warnings.Enqueue(formatter(state, exception));
      }
    }
    private sealed class Scope : IDisposable { public void Dispose() { } }
  }
}
