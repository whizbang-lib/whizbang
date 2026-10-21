// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1547: a Dapper host whose connection string names a schema in its Search Path installs Whizbang there
/// through <c>AddWhizbangPostgres</c> alone. The registration used to render the migrations for <c>public</c> while the
/// tables went wherever the session's search path pointed, so functions and tables split across two schemas, and the
/// startup readiness check looked only in <c>public</c>.
/// </summary>
/// <docs>data/drivers#dapper-coordinator-schema</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresConnectionRetry.cs</code-under-test>
[Category("Integration")]
public class DapperNamedSchemaRegistrationTests {
  private const string SCHEMA = "svc_named";

  private PerTestDatabase _database;
  private string _named = "";

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dapper_nmd_reg");
    _named = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { SearchPath = SCHEMA }.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_database.Name);

  private async Task<ServiceProvider> _startAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPostgres(_named, JsonContextRegistry.CreateCombinedOptions(), true, perspectiveEntries: []);
    return await SchemaStartup.StartAsync(services);
  }

  [Test]
  [Timeout(120_000)]
  public async Task Registration_SearchPathNamesASchema_InstallsTablesAndFunctionsThereAsync(CancellationToken cancellationToken) {
    // One start: the initializer runs to the open gate, then the host stops.
    await (await _startAsync()).DisposeAsync();

    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await Assert.That(await db.ExecuteScalarAsync<bool>($"SELECT to_regclass('{SCHEMA}.wh_outbox') IS NOT NULL")).IsTrue()
      .Because("the infrastructure tables go to the schema the connection string names, created if it is missing");
    await Assert.That(await db.ExecuteScalarAsync<bool>("SELECT to_regclass('public.wh_outbox') IS NOT NULL")).IsFalse();
    var claimWork = await db.ExecuteScalarAsync<string>(
      "SELECT pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
      "WHERE n.nspname = @SCHEMA AND p.proname = 'claim_work' LIMIT 1", new { SCHEMA });
    await Assert.That(claimWork).IsNotNull()
      .Because("the migrations install their functions in the same schema as the tables");
    await Assert.That(claimWork).DoesNotContain("public.wh_")
      .Because("a function rendered for public reads tables that are not there");
  }

  [Test]
  [Timeout(120_000)]
  public async Task Registration_SearchPathNamesASchema_TheCoordinatorWorksAgainstItAsync(CancellationToken cancellationToken) {
    await using var provider = await _startAsync();

    var coordinator = provider.GetRequiredService<IWorkCoordinator>();
    var instanceId = Guid.CreateVersion7();
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "named-schema-svc", "host", 1), cancellationToken);

    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await Assert.That(await db.ExecuteScalarAsync<long>(
      $"SELECT count(*) FROM {SCHEMA}.wh_service_instances WHERE instance_id = @instanceId", new { instanceId })).IsEqualTo(1L)
      .Because("the registered coordinator runs against the schema the migrations installed");
  }
}
