// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1548: the Dapper driver reconciles <c>wh_perspective_registry</c> at startup from the payloads its
/// assemblies registered, as the EF Core driver always has. Row retention acts only on registered perspectives, so
/// on this driver a declared window used to enroll nothing.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention#dapper-driver</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRegistryPayloads.cs</code-under-test>
[NotInParallel(nameof(PerspectiveRegistryPayloads))]
public class DapperPerspectiveRegistryReconcileTests {
  private const string TABLE = "wh_per_registry_probe";
  private const string MODEL = "Probe.Contracts.RegistryProbeModel";
  private const string PERSPECTIVE = "Probe.Perspectives.RegistryProbePerspective";
  private const string ENTRY = $"""
    CREATE TABLE IF NOT EXISTS {TABLE} (
      id UUID NOT NULL PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB,
      created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
      sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ, expires_at TIMESTAMPTZ, version INTEGER NOT NULL);
    """;
  private const string PAYLOAD =
    $$"""[{"ClrTypeName":"{{MODEL}}","TableName":"{{TABLE}}","SchemaJson":{},"SchemaHash":"probe-hash","ServiceName":"RegistryProbeService","PerspectiveName":"{{PERSPECTIVE}}"}]""";

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dapper_registry");
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_database.Name);

  [Test]
  public async Task Initializer_WithARegistryPayload_RecordsEachPerspectiveOfTheModelAsync() {
    var initializer = new PostgresSchemaInitializer(_database.ConnectionString, [new(nameof(TABLE), ENTRY)]) {
      PerspectiveRegistry = [new PerspectiveRegistryPayload("RegistryProbeService", PAYLOAD)],
    };

    await initializer.InitializeSchemaAsync();

    await using var db = new NpgsqlConnection(_database.ConnectionString);
    var row = await db.QuerySingleAsync<(string Table, string Service, string[] Perspectives)>(
      "SELECT table_name, service_name, perspective_names FROM wh_perspective_registry WHERE clr_type_name = @MODEL",
      new { MODEL });
    await Assert.That(row.Table).IsEqualTo(TABLE);
    await Assert.That(row.Service).IsEqualTo("RegistryProbeService");
    await Assert.That(row.Perspectives).IsEquivalentTo([PERSPECTIVE])
      .Because("the row-eviction cleanup finds a model's cursors and snapshots by the perspectives recorded here");
  }

  [Test]
  public async Task Host_StartedWithAddWhizbangPostgres_EnrollsADeclaredRetentionWindowAsync() {
    PerspectiveRegistryPayloads.Register("RegistryProbeService", PAYLOAD);
    var services = new ServiceCollection();
    services.AddWhizbangPostgres(_database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), true,
      [new KeyValuePair<string, string>(nameof(TABLE), ENTRY)], managedObjects: null);
    await using (await SchemaStartup.StartAsync(services)) {
    }

    var coordinator = new DapperWorkCoordinator(_database.ConnectionString, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.SyncPerspectiveRetentionAsync([
      new PerspectiveRetentionDeclaration(MODEL, Enrolled: true, TtlSeconds: 3600, MaxAgeSeconds: null, CapPerScope: null, CapScopeKey: null)]);

    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await Assert.That(await db.ExecuteScalarAsync<int?>(
      "SELECT row_ttl_seconds FROM wh_perspective_registry WHERE clr_type_name = @MODEL", new { MODEL })).IsEqualTo(3600)
      .Because("the host's startup registered the perspective, so the declared window is adopted with no hand-seeded row");
  }
}
