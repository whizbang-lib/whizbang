// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #1992: the Dapper driver gets an <c>Assess</c> verdict that behaves like the EF Core driver's. The version record is
/// the same ledger; Current means nothing to apply, read without a lock: every migration this binary carries recorded
/// with its hash and this binary's version, every perspective table at its hash, and nothing left to convert.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperPostgresStartupAssessor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
public class DapperStartupAssessorTests {
  private const string TABLE = "wh_per_assess_probe";
  private static readonly KeyValuePair<string, string>[] _entries = [
    new("AssessProbe", $"CREATE TABLE IF NOT EXISTS {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL);"),
  ];

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dapper_assess");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  private ServiceProvider _compose() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    services.AddWhizbangPostgres(
      _database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: true, _entries,
      managedObjects: null);
    return services.BuildServiceProvider();
  }

  private async Task _startAsync() {
    await using var provider = _compose();
    await provider.GetServices<IHostedService>().OfType<WhizbangDatabaseInitializerService>().Single()
      .StartAsync(CancellationToken.None);
  }

  private async Task<StartupVerdict> _verdictAsync() {
    await using var provider = _compose();
    return (await provider.GetRequiredService<IStartupAssessor>().AssessAsync(CancellationToken.None)).Verdict;
  }

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.ExecuteAsync(sql);
  }

  [Test]
  public async Task ABrandNewDatabase_MigratesAsync() {
    await Assert.That(await _verdictAsync()).IsEqualTo(StartupVerdict.Migrate);
  }

  [Test]
  public async Task AfterAStart_TheSchemaIsCurrentAsync() {
    await _startAsync();

    await Assert.That(await _verdictAsync()).IsEqualTo(StartupVerdict.Current)
      .Because("every migration and perspective table this binary carries is recorded, at this binary's version");
  }

  [Test]
  public async Task AMigrationThisBinaryOwes_IsServeAsync() {
    await _startAsync();
    await _execAsync("DELETE FROM wh_schema_migrations WHERE file_name = (SELECT max(file_name) FROM wh_schema_migrations WHERE file_name ~ '^[0-9]')");

    await Assert.That(await _verdictAsync()).IsEqualTo(StartupVerdict.Serve);
  }

  [Test]
  public async Task AMigrationLastRecordedByAnotherRelease_IsServeAsync() {
    await _startAsync();
    await _execAsync("""
      WITH v AS (INSERT INTO wh_schema_versions (library_version) VALUES ('0.0.1') RETURNING id)
      UPDATE wh_schema_migrations SET version_id = (SELECT id FROM v)
        WHERE file_name = (SELECT max(file_name) FROM wh_schema_migrations WHERE file_name ~ '^[0-9]');
      """);

    await Assert.That(await _verdictAsync()).IsEqualTo(StartupVerdict.Serve)
      .Because("the infrastructure schema this binary builds is tied to its version: a pass by this release has to run");
  }

  [Test]
  public async Task APerspectiveTableWhoseDefinitionChanged_IsServeAsync() {
    await _startAsync();
    await _execAsync("UPDATE wh_schema_migrations SET content_hash = 'changed' WHERE file_name = 'perspective:AssessProbe'");

    await Assert.That(await _verdictAsync()).IsEqualTo(StartupVerdict.Serve);
  }

  [Test]
  public async Task ANewerRelease_StandsThisBinaryDownAsync() {
    await _startAsync();
    await _execAsync("""
      WITH v AS (INSERT INTO wh_schema_versions (library_version) VALUES ('999.0.0') RETURNING id)
      UPDATE wh_schema_migrations SET version_id = (SELECT id FROM v)
        WHERE file_name = (SELECT min(file_name) FROM wh_schema_migrations WHERE file_name ~ '^[0-9]');
      """);

    await Assert.That(await _verdictAsync()).IsEqualTo(StartupVerdict.StandDown);
  }
}
