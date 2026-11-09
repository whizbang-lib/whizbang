// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.Versioning;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2122 on the Dapper driver: a rollback the <c>Assess</c> verdict owes runs before the schema pass, under the schema
/// lock, from the reverse scripts the newer version stored in the ledger; and the pass records how each perspective
/// table it applies is reversed.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperSchemaInitializationRunner.cs</code-under-test>
public class DapperSchemaRollbackTests {
  private static readonly KeyValuePair<string, string>[] _entries = [
    new("RollbackProbe", "CREATE TABLE IF NOT EXISTS wh_per_rollback_probe (id uuid PRIMARY KEY, data jsonb NOT NULL);"),
  ];

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dapper_rollback");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  private NpgsqlConnection _open() => new(_database.ConnectionString);

  [Test]
  public async Task APerspectiveTableThePassApplies_IsRecordedAsIrreversible_SinceTheDriverKnowsItOnlyByKeyAsync() {
    await new PostgresSchemaInitializer(_database.ConnectionString, _entries).InitializeSchemaAsync();

    await using var db = _open();
    await Assert.That(await db.ExecuteScalarAsync<string>(
      "SELECT reversal FROM wh_schema_migrations WHERE file_name = 'perspective:RollbackProbe'")).IsEqualTo("irreversible");
  }

  [Test]
  public async Task AnOwedRollback_RunsTheStoredReverseScripts_ThenThePassMigratesAsync() {
    var initializer = new PostgresSchemaInitializer(_database.ConnectionString, _entries);
    await initializer.InitializeSchemaAsync();
    await using var db = _open();
    var library = new PostgresMigrationProvider().Version;
    var newer = await db.ExecuteScalarAsync<int>(
      "INSERT INTO wh_schema_versions (library_version, application_version) VALUES (@library, '2.0.0') RETURNING id", new { library });
    await db.ExecuteAsync("CREATE TABLE rb_dapper (id int)");
    await db.ExecuteAsync("""
      INSERT INTO wh_schema_migrations (file_name, content_hash, version_id, status, reversal, down_sql)
      VALUES ('perspective:Newer', 'h', @newer, 1, 'reversible', 'DROP TABLE rb_dapper;')
      """, new { newer });
    var plan = new RollbackPlan([new AppliedSchemaChange("perspective:Newer", SchemaChangeKind.Application, library, "2.0.0")
      { Reversal = SchemaChangeReversal.Reversible }], AllowLossy: false);
    var rolling = new PostgresSchemaInitializer(_database.ConnectionString, _entries, applicationVersion: "1.9.0");

    await Assert.That(await rolling.RollBackSchemaAsync(plan)).IsEqualTo(1);

    await Assert.That(await db.ExecuteScalarAsync<long>("SELECT count(*) FROM pg_class WHERE relname = 'rb_dapper'")).IsEqualTo(0L);
    await Assert.That(await db.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_schema_migrations WHERE file_name = 'perspective:Newer'"))
      .IsEqualTo(0L);
    await Assert.That(async () => await rolling.RollBackSchemaAsync(null!)).Throws<ArgumentNullException>();
  }

  /// <summary>An assessor that answers the one verdict it was given.</summary>
  private sealed class FixedAssessor(StartupAssessment assessment) : IStartupAssessor {
    public Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) => Task.FromResult(assessment);
  }

  [Test]
  public async Task TheRunner_RunsTheRollbackTheVerdictOwes_BeforeThePassAsync() {
    await new PostgresSchemaInitializer(_database.ConnectionString, _entries).InitializeSchemaAsync();
    await using var db = _open();
    var library = new PostgresMigrationProvider().Version;
    var newer = await db.ExecuteScalarAsync<int>(
      "INSERT INTO wh_schema_versions (library_version, application_version) VALUES (@library, '2.0.0') RETURNING id", new { library });
    await db.ExecuteAsync("CREATE TABLE rb_runner (id int)");
    await db.ExecuteAsync("""
      INSERT INTO wh_schema_migrations (file_name, content_hash, version_id, status, reversal, down_sql)
      VALUES ('perspective:RunnerNewer', 'h', @newer, 1, 'reversible', 'DROP TABLE rb_runner;')
      """, new { newer });
    var change = new AppliedSchemaChange("perspective:RunnerNewer", SchemaChangeKind.Application, library, "2.0.0") {
      Reversal = SchemaChangeReversal.Reversible,
    };
    var services = new ServiceCollection();
    services.AddSingleton(new StartupAssessmentSource(
      new FixedAssessor(new StartupAssessment(StartupVerdict.Migrate, "owed") { Rollback = new RollbackPlan([change], false) }),
      NullStartupStageReporter.Instance, new FakeTimeProvider(), NullLogger<StartupAssessmentSource>.Instance));
    var runner = new DapperSchemaInitializationRunner(
      _database.ConnectionString, new PostgresOptions(),
      () => new PostgresSchemaInitializer(_database.ConnectionString, _entries, applicationVersion: "1.9.0"),
      NullLogger<DapperSchemaInitializationRunner>.Instance, services.BuildServiceProvider());

    await runner.RunAsync(CancellationToken.None);

    await Assert.That(await db.ExecuteScalarAsync<long>("SELECT count(*) FROM pg_class WHERE relname = 'rb_runner'")).IsEqualTo(0L);
    await Assert.That(await db.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_schema_migrations WHERE file_name = 'perspective:RunnerNewer'"))
      .IsEqualTo(0L);
  }
}
