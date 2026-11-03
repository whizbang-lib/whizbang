// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Perspectives;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #1992: what the Dapper schema initializer counts as current, each ingredient on its own, and the start it runs on a
/// current schema (no pass: the registry, the instance and the reconcile).
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
public class DapperSchemaCurrencyTests {
  private const string TABLE = "wh_per_currency_probe";
  private static readonly KeyValuePair<string, string>[] _entries = [
    new("CurrencyProbe", $"CREATE TABLE IF NOT EXISTS {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL);"),
  ];

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dapper_currency");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.ExecuteAsync(sql);
  }

  [Test]
  public async Task LegacyPerspectiveSql_CannotBeShownCurrentAsync() {
    var initializer = new PostgresSchemaInitializer(_database.ConnectionString, $"CREATE TABLE IF NOT EXISTS {TABLE} (id uuid);");

    await Assert.That(await initializer.IsCurrentAsync()).IsFalse()
      .Because("one string of perspective SQL is recorded under no hash, so nothing says it is applied");
  }

  [Test]
  public async Task WithTheCallersOwnMigrations_AnInitializedSchemaIsCurrentAsync() {
    var initializer = new PostgresSchemaInitializer(_database.ConnectionString, _entries, new PostgresMigrationProvider());
    await initializer.InitializeSchemaAsync();

    await Assert.That(await initializer.IsCurrentAsync()).IsTrue();
  }

  [Test]
  public async Task AnApplicationObject_IsPartOfCurrent_BeforeAndAfterThePerspectivesAsync() {
    var objects = new Objects(
      [new ApplicationSchemaObject("probe_fn", "CREATE OR REPLACE FUNCTION probe_fn() RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT 1';")],
      [new ApplicationSchemaObject("probe_view", $"CREATE OR REPLACE VIEW probe_view AS SELECT id FROM {TABLE};")]);
    var initializer = new PostgresSchemaInitializer(_database.ConnectionString, _entries, applicationObjects: objects);
    await initializer.InitializeSchemaAsync();
    await Assert.That(await initializer.IsCurrentAsync()).IsTrue();

    await _execAsync("UPDATE wh_schema_migrations SET content_hash = 'changed' WHERE file_name = 'app:after:probe_view'");

    await Assert.That(await initializer.IsCurrentAsync()).IsFalse();
  }

  [Test]
  public async Task ADeclaredKeyTable_IsCurrentOnlyOnceSettledAsync() {
    var table = new DeclaredKeyTable(TABLE, []);
    var initializer = new PostgresSchemaInitializer(_database.ConnectionString, _entries) { DeclaredKeyTables = [table] };
    await initializer.InitializeSchemaAsync();
    await _execAsync($"""
      INSERT INTO wh_stored_form_migrations (name, table_name, kind, rows_converted, settled_at)
        VALUES ('{table.JournalName}', '{TABLE}', 'declared-names', 0, now())
        ON CONFLICT (name) DO UPDATE SET settled_at = now();
      """);
    await Assert.That(await initializer.IsCurrentAsync()).IsTrue();

    await _execAsync($"UPDATE wh_stored_form_migrations SET settled_at = NULL WHERE name = '{table.JournalName}'");

    await Assert.That(await initializer.IsCurrentAsync()).IsFalse()
      .Because("a declared-key table the spelling pass has not settled still has keys to move");
  }

  [Test]
  public async Task ADeclaredObjectDroppedByHand_IsNotCurrentAsync() {
    KeyValuePair<string, string>[] entries = [
      new("CurrencyProbe", $"""
        CREATE TABLE IF NOT EXISTS {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL);
        CREATE INDEX IF NOT EXISTS idx_currency_probe_status ON {TABLE} ((data ->> 'Status'));
        """),
    ];
    var initializer = new PostgresSchemaInitializer(_database.ConnectionString, entries) {
      ManagedObjects = new ManagedSchemaObjectSet().Index(TABLE, "idx_currency_probe_status", "CurrencyProbe"),
    };
    await initializer.InitializeSchemaAsync();
    await Assert.That(await initializer.IsCurrentAsync()).IsTrue();

    await _execAsync("DROP INDEX idx_currency_probe_status");

    await Assert.That(await initializer.IsCurrentAsync()).IsFalse()
      .Because("a declared object the ledger has seen and the database lacks is work: the pass builds it again");
  }

  [Test]
  public async Task OnACurrentSchema_TheStartConverges_WithoutAnInstanceToRegisterAsync() {
    var initializer = new PostgresSchemaInitializer(_database.ConnectionString, _entries);
    await initializer.InitializeSchemaAsync();
    var instances = await _countAsync("SELECT count(*) FROM wh_service_instances");

    await initializer.ConvergeCurrentAsync();

    await Assert.That(await _countAsync("SELECT count(*) FROM wh_service_instances")).IsEqualTo(instances)
      .Because("with no instance identity there is nobody to register");
    await Assert.That(await initializer.IsCurrentAsync()).IsTrue();
  }

  private async Task<long> _countAsync(string sql) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    return await db.ExecuteScalarAsync<long>(sql);
  }

  private sealed class Objects(IReadOnlyList<ApplicationSchemaObject> before, IReadOnlyList<ApplicationSchemaObject> after)
    : IApplicationSchemaObjects {
    public IReadOnlyList<ApplicationSchemaObject> BeforePerspectives => before;
    public IReadOnlyList<ApplicationSchemaObject> AfterPerspectives => after;
  }
}
