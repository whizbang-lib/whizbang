// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Serialization;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Postgres capability provider against a real server: the shared test container (the pgvector image,
/// which preloads only <c>pg_stat_statements</c> and has no allowlist). Proves the probe's one query reads
/// what the derivation needs, including a managed-server allowlist and a role without privilege, that each
/// status carries the generic state with the Postgres state as its detail, and that a database the probe
/// cannot reach reads Unknown through the registry rather than throwing.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresCapabilityProbe.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresCapabilityProvider.cs</code-under-test>
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

  private PostgresCapabilityProvider _provider(string? afterOpen = null, string? connectionString = null) =>
    new([new PostgresCapabilityDatabase("orders-db", async ct => {
      var connection = new NpgsqlConnection(connectionString ?? _database.ConnectionString);
      await connection.OpenAsync(ct);
      if (afterOpen is not null) {
        await using var command = connection.CreateCommand();
        command.CommandText = afterOpen;
        await command.ExecuteNonQueryAsync(ct);
      }
      return connection;
    })]);

  private Task<IReadOnlyList<CapabilityStatus>> _probeAsync(string? afterOpen = null, string? connectionString = null) =>
    _provider(afterOpen, connectionString).ProbeAsync("orders-db", CancellationToken.None);

  private async Task _executeAsync(string sql) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
  }

  private static CapabilityStatus _status(IReadOnlyList<CapabilityStatus> statuses, string extension) =>
    statuses.Single(s => s.Descriptor.Id.Name == extension);

  private static string? _detail(IReadOnlyList<CapabilityStatus> statuses, string extension) =>
    _status(statuses, extension).Detail;

  [Test]
  public async Task OnTheTestServer_EachExtensionHasTheStateItsImageGivesItAsync() {
    var statuses = await _probeAsync();

    await Assert.That(statuses.All(s => s.Scope == "orders-db" && s.Reason is null)).IsTrue();
    await Assert.That(_status(statuses, "vector").State).IsEqualTo(CapabilityState.Provisionable);
    await Assert.That(_detail(statuses, "vector")).IsEqualTo(nameof(PostgresExtensionState.Available));
    await Assert.That(_detail(statuses, "pg_trgm")).IsEqualTo(nameof(PostgresExtensionState.Available));
    await Assert.That(_detail(statuses, "pgstattuple")).IsEqualTo(nameof(PostgresExtensionState.Available));
    await Assert.That(_detail(statuses, "pg_stat_statements")).IsEqualTo(nameof(PostgresExtensionState.Available))
      .Because("the container preloads it, and a fresh database has not created it");
    await Assert.That(_status(statuses, "pg_squeeze").State).IsEqualTo(CapabilityState.Missing);
    await Assert.That(statuses.Any(s => s.State == CapabilityState.NotPermitted)).IsFalse()
      .Because("a server with no allowlist allowlists nothing away");
    await Assert.That(statuses.Any(s => s.State == CapabilityState.Unknown)).IsFalse();
  }

  [Test]
  public async Task AfterCreate_PgStatStatementsIsReadyWithItsVersionAsync() {
    await _executeAsync("CREATE EXTENSION IF NOT EXISTS pg_stat_statements");

    var status = _status(await _probeAsync(), "pg_stat_statements");

    await Assert.That(status.State).IsEqualTo(CapabilityState.Ready);
    await Assert.That(status.Detail).IsEqualTo(nameof(PostgresExtensionState.Installed));
    await Assert.That(status.Version).IsNotNull();
  }

  [Test]
  public async Task WithAManagedServerAllowlist_AnUnlistedExtensionIsNotPermittedAsync() {
    // The setting a managed server uses for its allowlist; any session can set a custom option, so
    // the real query reads a real value.
    var statuses = await _probeAsync(afterOpen: "SET azure.extensions = 'VECTOR, PG_TRGM'");

    await Assert.That(_status(statuses, "vector").State).IsEqualTo(CapabilityState.Provisionable);
    await Assert.That(_status(statuses, "pg_trgm").State).IsEqualTo(CapabilityState.Provisionable);
    await Assert.That(_status(statuses, "pgstattuple").State).IsEqualTo(CapabilityState.NotPermitted);
    await Assert.That(_detail(statuses, "pgstattuple")).IsEqualTo(nameof(PostgresExtensionState.NotAllowlisted));
    await Assert.That(_status(statuses, "pg_squeeze").State).IsEqualTo(CapabilityState.Missing);
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

      var statuses = await _probeAsync(connectionString: limited);

      await Assert.That(_status(statuses, "pg_trgm").State).IsEqualTo(CapabilityState.NoPrivilege);
      await Assert.That(_status(statuses, "pgstattuple").State).IsEqualTo(CapabilityState.NoPrivilege);
      await Assert.That(_status(statuses, "pg_squeeze").State).IsEqualTo(CapabilityState.Missing);
      await Assert.That(_status(statuses, "pg_stat_statements").State).IsEqualTo(CapabilityState.Unknown)
        .Because("without pg_read_all_settings the role cannot read shared_preload_libraries");
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
  public async Task ADatabaseThatDoesNotExist_IsUnknownThroughTheRegistry_NeverAThrowAsync() {
    var missing = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = $"{_database.Name}_absent" }.ConnectionString;
    var registry = new CapabilityRegistry([_provider(connectionString: missing)], NullLogger<CapabilityRegistry>.Instance);

    await registry.RefreshAsync(CancellationToken.None);

    await Assert.That(registry.Statuses.All(s => s.State == CapabilityState.Unknown && s.Reason is not null)).IsTrue();
  }

  [Test]
  public async Task TheDapperDriver_RegistersItsDatabaseAsAPostgresScopeAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangPostgres(_database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false);
    await using var provider = services.BuildServiceProvider();
    var registry = provider.GetRequiredService<ICapabilityRegistry>();

    await registry.RefreshAsync(CancellationToken.None);

    await Assert.That(registry.Providers.Single().Scopes.Single()).IsEqualTo(_database.Name)
      .Because("the Dapper driver has no connection-string name, so the scope is the database its string names");
    await Assert.That(registry.GetState(new CapabilityId(PostgresExtensionCatalog.PROVIDER, "vector"), _database.Name))
      .IsEqualTo(CapabilityState.Provisionable);
    await Assert.That(registry.GetState(new CapabilityId(PostgresExtensionCatalog.PROVIDER, "pg_squeeze"), _database.Name))
      .IsEqualTo(CapabilityState.Missing);
  }

  [Test]
  public async Task TheDapperDriver_RegistersItsDatabase_OnThePerPerspectiveOverloadAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangPostgres(_database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(),
      initializeSchema: false, perspectiveEntries: []);
    await using var provider = services.BuildServiceProvider();

    var registry = provider.GetRequiredService<ICapabilityRegistry>();

    await Assert.That(registry.Providers.Single().Scopes.Single()).IsEqualTo(_database.Name);
  }

  [Test]
  public async Task TheDapperDriver_FallsBackToTheDefaultNameWhenTheStringNamesNoDatabaseAsync() {
    await Assert.That(ServiceCollectionExtensions.ProbeDatabaseName("Host=localhost;Username=u"))
      .IsEqualTo(Whizbang.Core.Naming.WhizbangNamingConvention.DEFAULT_CONNECTION_STRING_NAME);
    await Assert.That(ServiceCollectionExtensions.ProbeDatabaseName("Host=localhost;Database=orders")).IsEqualTo("orders");
  }
}
