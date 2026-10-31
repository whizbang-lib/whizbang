// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Every Dapper maintenance step acts on the schema the host names, found the way the Dapper work coordinator finds
/// it: the connection string's <c>Search Path</c>, else the server's <c>current_schema()</c> (#1894).
/// </summary>
/// <remarks>
/// <para>
/// The database is migrated into <see cref="SCHEMA"/> and <c>public</c> is left empty. The steps' connection string
/// names the schema as the second entry of its <c>Search Path</c>, after <c>$user</c>, and the session user has a
/// schema of its own. So a step that hard-codes <c>public</c> finds no tables, and a step that asks the server
/// resolves the session user's schema (the server skips <c>$user</c> only when no such schema exists), where the
/// coordinator, which skips <c>$user</c> in the connection string, writes to <see cref="SCHEMA"/>.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperPartitionMaintenanceStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperPhysicalColumnFillMaintenanceStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperManagedSchemaReconcileStep.cs</code-under-test>
[Category("Integration")]
public class DapperMaintenanceStepNamedSchemaTests {
  private const string SCHEMA = "svc_maint";
  private static readonly DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

  private PerTestDatabase _database;
  private string _hostConnectionString = "";

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dap_mnt_named");
    await using (var connection = new NpgsqlConnection(_database.ConnectionString)) {
      await connection.OpenAsync();
      await connection.ExecuteAsync($"CREATE SCHEMA {SCHEMA}");
      await connection.ExecuteAsync("DO $$ BEGIN EXECUTE format('CREATE SCHEMA %I', current_user); END $$");
    }
    var migrating = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { SearchPath = SCHEMA }.ConnectionString;
    var migrations = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, SCHEMA);
    await new PostgresSchemaInitializer(migrating, [], migrations).InitializeSchemaAsync();
    _hostConnectionString = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { SearchPath = $"$user, {SCHEMA}" }.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_database.Name);

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  [Test]
  public async Task ThePremise_PublicIsEmpty_AndTheServerResolvesTheSessionUsersSchemaAsync() {
    await using var host = new NpgsqlConnection(_hostConnectionString);
    await host.OpenAsync();

    await Assert.That(await host.ExecuteScalarAsync<string?>("SELECT to_regclass('public.wh_log')::text")).IsNull();
    await Assert.That(await host.ExecuteScalarAsync<string>("SELECT current_schema()"))
      .IsEqualTo(await host.ExecuteScalarAsync<string>("SELECT current_user::text"))
      .Because("the server resolves $user to the session user's schema, which the coordinator does not write to");
    await Assert.That(DapperCoordinatorSchema.FromConnectionString(_hostConnectionString)).IsEqualTo(SCHEMA);
  }

  [Test]
  public async Task ThePartitionStep_ConvertsTheLogInTheHostsSchemaAsync() {
    await using (var setup = await _openAsync()) {
      await setup.ExecuteAsync($"DELETE FROM {SCHEMA}.wh_log");
    }
    var services = new ServiceCollection();
    services.AddSingleton(Options.Create(new MaintenanceWorkerOptions {
      Partitioning = new MaintenancePartitioningOptions { Enabled = true, CreateAhead = 1 },
    }));
    services.AddPartitionedTable(PartitionedTables.Log);
    await using var provider = services.BuildServiceProvider();
    var step = new DapperPartitionMaintenanceStep(
      _hostConnectionString, NullLogger<DapperPartitionMaintenanceStep>.Instance, new FixedClock(_now));

    await step.RunAsync(provider, CancellationToken.None);

    await using var connection = await _openAsync();
    await Assert.That(await connection.ExecuteScalarAsync<string>(
      $"SELECT relkind::text FROM pg_class WHERE oid = '{SCHEMA}.wh_log'::regclass")).IsEqualTo("p");
  }

  [Test]
  public async Task ThePhysicalColumnFillStep_FillsAndClaimsInTheHostsSchemaAsync() {
    await using (var setup = await _openAsync()) {
      await setup.ExecuteAsync($$"""
        CREATE TABLE {{SCHEMA}}.wh_per_named_probe (id uuid PRIMARY KEY, data jsonb NOT NULL, name text);
        INSERT INTO {{SCHEMA}}.wh_per_named_probe (id, data) VALUES (gen_random_uuid(), '{"Name": "late"}');
        INSERT INTO {{SCHEMA}}.wh_physical_column_fills (table_name, column_name, json_key, extraction)
        VALUES ('{{SCHEMA}}.wh_per_named_probe', 'name', 'Name', '(data ->> ''Name'')');
        """);
    }
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var step = new DapperPhysicalColumnFillMaintenanceStep(_hostConnectionString, null, new FixedClock(_now));

    await step.RunAsync(provider, CancellationToken.None);

    await using var connection = await _openAsync();
    await Assert.That(await connection.ExecuteScalarAsync<string?>($"SELECT name FROM {SCHEMA}.wh_per_named_probe"))
      .IsEqualTo("late");
    await Assert.That(await connection.ExecuteScalarAsync<long>(
      $"SELECT count(*) FROM {SCHEMA}.wh_unique_emission_claims WHERE claim_key = @key",
      new { key = PhysicalColumnFill.ClaimKey(SCHEMA, _now) })).IsEqualTo(1L)
      .Because("the fleet claim is taken in, and keyed by, the host's schema");
  }

  [Test]
  public async Task TheManagedSchemaReconcileStep_ClaimsInTheHostsSchemaAsync() {
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var step = new DapperManagedSchemaReconcileStep(
      _hostConnectionString,
      () => ManagedSchemaObjectSet.FromDeclarations([]),
      NullLogger<DapperManagedSchemaReconcileStep>.Instance,
      new FixedClock(_now));

    await step.RunAsync(provider, CancellationToken.None);

    await using var connection = await _openAsync();
    await Assert.That(await connection.ExecuteScalarAsync<long>(
      $"SELECT count(*) FROM {SCHEMA}.wh_unique_emission_claims WHERE claim_key = @key",
      new { key = ManagedSchemaHostPass.ClaimKey(SCHEMA, _now) })).IsEqualTo(1L)
      .Because("the reconcile runs against the schema the coordinator writes to, not the one the server resolves first");
  }

  private sealed class FixedClock(DateTimeOffset now) : TimeProvider {
    public override DateTimeOffset GetUtcNow() => now;
  }
}
