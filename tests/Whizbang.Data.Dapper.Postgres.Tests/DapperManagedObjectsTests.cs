// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
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

  [Test]
  public async Task AReconcileThatFails_IsLoggedAndDoesNotFailTheStartAsync() {
    await _initializeAsync();
    await using (var db = new NpgsqlConnection(_connectionString)) {
      // Any write to the ledger now fails, as a permissions change or a broken ledger would.
      await db.ExecuteAsync("ALTER TABLE wh_managed_objects ADD CONSTRAINT ck_refuse_everything CHECK (false) NOT VALID");
    }
    var logger = new ListLogger();

    await new PostgresSchemaInitializer(_connectionString, [new KeyValuePair<string, string>("ProbePerspective", ENTRY)]) {
      ManagedObjects = ManagedSchemaObjectSet.FromDeclarations([(TABLE, "index", $"ix_{TABLE}_status", "ProbeModel")]),
      Logger = logger,
    }.InitializeSchemaAsync();

    await Assert.That(logger.Messages).Contains("The managed-object reconcile failed; it runs again at the next start");
  }

  [Test]
  public async Task AReconcileThatFails_WithNoLogger_StillDoesNotFailTheStartAsync() {
    await _initializeAsync();
    await using (var db = new NpgsqlConnection(_connectionString)) {
      await db.ExecuteAsync("ALTER TABLE wh_managed_objects ADD CONSTRAINT ck_refuse_everything CHECK (false) NOT VALID");
    }

    await _initializeAsync();

    await Assert.That(await _indexesAsync()).Contains($"ix_{TABLE}_status");
  }

  /// <summary>
  /// The registration hands the declared objects to the schema initialization it runs, so a host that
  /// declares them gets the reconcile without building the initializer itself.
  /// </summary>
  [Test]
  public async Task TheRegistration_WithDeclaredObjects_ReconcilesAtStartAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPostgres(
      _connectionString, new System.Text.Json.JsonSerializerOptions(), initializeSchema: true,
      [new KeyValuePair<string, string>("ProbePerspective", ENTRY)],
      managedObjects: [(TABLE, "index", $"ix_{TABLE}_status", "ProbeModel")]);

    await using var db = new NpgsqlConnection(_connectionString);
    var recorded = await db.QueryAsync<string>("SELECT object_name FROM wh_managed_objects ORDER BY object_name");
    await Assert.That(recorded).Contains($"ix_{TABLE}_status")
      .Because("the declared objects reached the initialization the registration ran");
  }

  /// <summary>The registration without a declaration leaves every object in place and records none.</summary>
  [Test]
  public async Task TheRegistration_WithoutADeclaration_ReconcilesNothingAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPostgres(
      _connectionString, new System.Text.Json.JsonSerializerOptions(), initializeSchema: true,
      [new KeyValuePair<string, string>("ProbePerspective", ENTRY)],
      managedObjects: null);

    await using var db = new NpgsqlConnection(_connectionString);
    await Assert.That(await db.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_managed_objects")).IsEqualTo(0L);
    await Assert.That(await _indexesAsync()).Contains($"ix_{TABLE}_status");
  }

  private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
  }
}
