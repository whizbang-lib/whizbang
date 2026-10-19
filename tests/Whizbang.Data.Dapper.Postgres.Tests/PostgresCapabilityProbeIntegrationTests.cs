// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The extension probe against a real server: the shared test container (the pgvector image, which
/// preloads only <c>pg_stat_statements</c> and has no allowlist). Proves the probe's one query reads what
/// the derivation needs, including a managed-server allowlist and a role without privilege, and that a
/// database it cannot reach reads Unknown rather than throwing.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresCapabilityProbe.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/IPostgresCapabilities.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
[Category("Integration")]
public class PostgresCapabilityProbeIntegrationTests {
  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("ext_probe");
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_database.Name);

  private PostgresExtensionProbeTarget _target(string? afterOpen = null, string? connectionString = null) =>
    new("orders-db", async ct => {
      var connection = new NpgsqlConnection(connectionString ?? _database.ConnectionString);
      await connection.OpenAsync(ct);
      if (afterOpen is not null) {
        await using var command = connection.CreateCommand();
        command.CommandText = afterOpen;
        await command.ExecuteNonQueryAsync(ct);
      }
      return connection;
    });

  private async Task _executeAsync(string sql) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
  }

  private static PostgresExtensionState _state(PostgresDatabaseExtensions database, string extension) =>
    database.Extensions.Single(e => e.Extension.Name == extension).State;

  [Test]
  public async Task OnTheTestServer_EachExtensionHasTheStateItsImageGivesItAsync() {
    var result = await PostgresCapabilityProbe.ProbeAsync(_target(), PostgresExtensionCatalog.BuiltIn, CancellationToken.None);

    await Assert.That(result.ProbeFailure).IsNull();
    await Assert.That(_state(result, "vector")).IsEqualTo(PostgresExtensionState.Available);
    await Assert.That(_state(result, "pg_trgm")).IsEqualTo(PostgresExtensionState.Available);
    await Assert.That(_state(result, "pgstattuple")).IsEqualTo(PostgresExtensionState.Available);
    await Assert.That(_state(result, "pg_stat_statements")).IsEqualTo(PostgresExtensionState.Available)
      .Because("the container preloads it, and a fresh database has not created it");
    await Assert.That(_state(result, "pg_squeeze")).IsEqualTo(PostgresExtensionState.Missing);
    await Assert.That(result.Extensions.Any(e => e.State == PostgresExtensionState.NotAllowlisted)).IsFalse()
      .Because("a server with no allowlist allowlists nothing away");
    await Assert.That(result.Extensions.Any(e => e.State == PostgresExtensionState.Unknown)).IsFalse();
  }

  [Test]
  public async Task AfterCreate_PgStatStatementsIsInstalledWithItsVersionAsync() {
    await _executeAsync("CREATE EXTENSION IF NOT EXISTS pg_stat_statements");

    var result = await PostgresCapabilityProbe.ProbeAsync(_target(), PostgresExtensionCatalog.BuiltIn, CancellationToken.None);

    var status = result.Extensions.Single(e => e.Extension.Name == "pg_stat_statements");
    await Assert.That(status.State).IsEqualTo(PostgresExtensionState.Installed);
    await Assert.That(status.InstalledVersion).IsNotNull();
  }

  [Test]
  public async Task WithAManagedServerAllowlist_AnUnlistedExtensionIsNotAllowlistedAsync() {
    // The setting a managed server uses for its allowlist; any session can set a custom option, so
    // the real query reads a real value.
    var result = await PostgresCapabilityProbe.ProbeAsync(
      _target(afterOpen: "SET azure.extensions = 'VECTOR, PG_TRGM'"), PostgresExtensionCatalog.BuiltIn, CancellationToken.None);

    await Assert.That(_state(result, "vector")).IsEqualTo(PostgresExtensionState.Available);
    await Assert.That(_state(result, "pg_trgm")).IsEqualTo(PostgresExtensionState.Available);
    await Assert.That(_state(result, "pgstattuple")).IsEqualTo(PostgresExtensionState.NotAllowlisted);
    await Assert.That(_state(result, "pg_squeeze")).IsEqualTo(PostgresExtensionState.Missing);
  }

  [Test]
  public async Task ARoleThatCannotCreate_SeesNoPrivilege_AndCannotSeeThePreloadListAsync() {
    var role = $"ext_probe_{Guid.CreateVersion7():N}"[..40];
    await using (var admin = new NpgsqlConnection(_database.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = admin.CreateCommand();
      create.CommandText = $"CREATE ROLE {role} LOGIN PASSWORD 'probe'";
      await create.ExecuteNonQueryAsync();
    }
    try {
      var limited = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Username = role, Password = "probe" }.ConnectionString;

      var result = await PostgresCapabilityProbe.ProbeAsync(
        _target(connectionString: limited), PostgresExtensionCatalog.BuiltIn, CancellationToken.None);

      await Assert.That(_state(result, "pg_trgm")).IsEqualTo(PostgresExtensionState.NoPrivilege);
      await Assert.That(_state(result, "pgstattuple")).IsEqualTo(PostgresExtensionState.NoPrivilege);
      await Assert.That(_state(result, "pg_squeeze")).IsEqualTo(PostgresExtensionState.Missing);
      await Assert.That(_state(result, "pg_stat_statements")).IsEqualTo(PostgresExtensionState.Unknown)
        .Because("without pg_read_all_settings the role cannot read shared_preload_libraries");
      await Assert.That(result.ProbeFailure).IsNull()
        .Because("an unreadable setting costs the extensions that depend on it, not the probe");
    } finally {
      NpgsqlConnection.ClearAllPools();
      await using var admin = new NpgsqlConnection(_database.ConnectionString);
      await admin.OpenAsync();
      await using var drop = admin.CreateCommand();
      drop.CommandText = $"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role};";
      await drop.ExecuteNonQueryAsync();
    }
  }

  [Test]
  public async Task ADatabaseThatDoesNotExist_IsUnknownAndNeverThrowsAsync() {
    var missing = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = $"{_database.Name}_absent" }.ConnectionString;

    var result = await PostgresCapabilityProbe.ProbeAsync(
      _target(connectionString: missing), PostgresExtensionCatalog.BuiltIn, CancellationToken.None);

    await Assert.That(result.ProbeFailure).IsNotNull();
    await Assert.That(result.Extensions.All(e => e.State == PostgresExtensionState.Unknown)).IsTrue();
  }

  [Test]
  public async Task TheDapperDriver_RegistersItsDatabaseForTheProbeAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddMetrics();
    services.AddWhizbangPostgres(_database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false);
    await using var provider = services.BuildServiceProvider();
    var capabilities = provider.GetRequiredService<IPostgresCapabilities>();

    await capabilities.RefreshAsync(CancellationToken.None);

    await Assert.That(capabilities.Databases.Single().Database).IsEqualTo(_database.Name)
      .Because("the Dapper driver has no connection-string name, so the database is the one its string names");
    await Assert.That(capabilities.GetState(_database.Name, "vector")).IsEqualTo(PostgresExtensionState.Available);
    await Assert.That(capabilities.GetState(_database.Name, "pg_squeeze")).IsEqualTo(PostgresExtensionState.Missing);
  }

  [Test]
  public async Task TheDapperDriver_RegistersItsDatabaseForTheProbe_OnThePerPerspectiveOverloadAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddMetrics();
    services.AddWhizbangPostgres(_database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(),
      initializeSchema: false, perspectiveEntries: []);
    await using var provider = services.BuildServiceProvider();

    var capabilities = provider.GetRequiredService<IPostgresCapabilities>();

    await Assert.That(capabilities.Databases.Single().Database).IsEqualTo(_database.Name);
  }

  [Test]
  public async Task TheDapperDriver_FallsBackToTheDefaultNameWhenTheStringNamesNoDatabaseAsync() {
    await Assert.That(ServiceCollectionExtensions.ProbeDatabaseName("Host=localhost;Username=u"))
      .IsEqualTo(Whizbang.Core.Naming.WhizbangNamingConvention.DEFAULT_CONNECTION_STRING_NAME);
    await Assert.That(ServiceCollectionExtensions.ProbeDatabaseName("Host=localhost;Database=orders")).IsEqualTo("orders");
  }

  [Test]
  public async Task CapabilitiesOverARealDatabase_LogNoWarningAsync() {
    var capabilities = new PostgresCapabilities([_target()], NullLogger<PostgresCapabilities>.Instance);

    await capabilities.RefreshAsync(CancellationToken.None);

    await Assert.That(capabilities.Databases.Single().ProbeFailure).IsNull();
    await Assert.That(capabilities.GetState("orders-db", "pg_trgm")).IsEqualTo(PostgresExtensionState.Available);
  }
}
