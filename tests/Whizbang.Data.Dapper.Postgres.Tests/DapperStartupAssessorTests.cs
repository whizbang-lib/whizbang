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
using Whizbang.Core.Versioning;
using Whizbang.Core.Workers;
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

  private ServiceProvider _compose(string? connectionString = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    services.AddWhizbangPostgres(
      connectionString ?? _database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: true, _entries,
      managedObjects: null);
    return services.BuildServiceProvider();
  }

  private async Task _startAsync(string? connectionString = null) {
    await using var provider = _compose(connectionString);
    await provider.GetServices<IHostedService>().OfType<WhizbangDatabaseInitializerService>().Single()
      .StartAsync(CancellationToken.None);
  }

  private async Task<StartupVerdict> _verdictAsync() {
    await using var provider = _compose();
    return (await provider.GetRequiredService<IStartupAssessor>().AssessAsync(CancellationToken.None)).Verdict;
  }

  /// <summary>
  /// A release strictly older than this binary, whatever version the build stamped (a CI build and a local build stamp
  /// different ones): the SemVer floor, checked against this binary's own version.
  /// </summary>
  private static string _olderThanThisBinary() {
    const string FLOOR = "0.0.0-0";
    if (!SemanticVersion.TryParse(BuildInfo.Version, out var mine) || !SemanticVersion.TryParse(FLOOR, out var floor) || floor >= mine) {
      throw new InvalidOperationException($"this binary's version '{BuildInfo.Version}' has no strictly older SemVer version");
    }
    return FLOOR;
  }

  /// <summary>A release strictly newer than this binary, derived from its version: the next major.</summary>
  private static string _newerThanThisBinary() =>
    SemanticVersion.TryParse(BuildInfo.Version, out var mine)
      ? $"{mine.Major + 1}.0.0"
      : throw new InvalidOperationException($"this binary's version '{BuildInfo.Version}' is not SemVer");

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

    await using var provider = _compose();
    var assessment = await provider.GetRequiredService<IStartupAssessor>().AssessAsync(CancellationToken.None);
    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.Current)
      .Because("every migration and perspective table this binary carries is recorded, at this binary's version: " + assessment.Reason);
  }

  [Test]
  public async Task ASchemaTheConnectionNames_IsTheOneAssessedAsync() {
    var named = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { SearchPath = "assess_named" }.ConnectionString;
    await _startAsync(named);

    await using var provider = _compose(named);
    var assessment = await provider.GetRequiredService<IStartupAssessor>().AssessAsync(CancellationToken.None);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.Current).Because(assessment.Reason);
    await Assert.That(await _verdictAsync()).IsEqualTo(StartupVerdict.Migrate)
      .Because("the default schema of the same database holds no ledger: a brand-new schema");
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
    await _execAsync($"""
      WITH v AS (INSERT INTO wh_schema_versions (library_version) VALUES ('{_olderThanThisBinary()}') RETURNING id)
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
    await _execAsync($"""
      WITH v AS (INSERT INTO wh_schema_versions (library_version) VALUES ('{_newerThanThisBinary()}') RETURNING id)
      UPDATE wh_schema_migrations SET version_id = (SELECT id FROM v), content_hash = 'applied-by-a-newer-release', status = 2
        WHERE file_name = (SELECT min(file_name) FROM wh_schema_migrations WHERE file_name ~ '^[0-9]');
      """);

    await Assert.That(await _verdictAsync()).IsEqualTo(StartupVerdict.StandDown);
  }

  // ── #2022: a newer version online, read as the EF Core driver reads it ──

  private ServiceProvider _composeAt(string applicationVersion) {
    var services = new ServiceCollection();
    services.AddSingleton<IApplicationVersionProvider>(new ApplicationVersionProvider(ApplicationVersion.FromRecorded(applicationVersion)));
    services.AddLogging();
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    services.AddWhizbangPostgres(_database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: true, _entries,
      managedObjects: null);
    return services.BuildServiceProvider();
  }

  private async Task<Guid> _onlineAsync(string application, string phase = "Running") {
    var id = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.ExecuteScalarAsync("""
      SELECT record_heartbeat(@id, 'svc', 'host', 1, '{}'::jsonb, @phase, @library, 90, 'pooled', @application)
      """, new { id, phase, library = BuildInfo.Version, application });
    return id;
  }

  [Test]
  public async Task ANewerApplicationVersionOnline_StandsAnOlderInstanceDown_AsTheEFCoreDriverDoesAsync() {
    await _startAsync();
    var newer = await _onlineAsync("2.0.0");

    await using var provider = _composeAt("1.0.0");
    var assessment = await provider.GetRequiredService<IStartupAssessor>().AssessAsync(CancellationToken.None);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(assessment.Reason).Contains($"instance {newer} is online with application version 2.0.0");
  }

  [Test]
  public async Task AnEqualOrOlderVersionOnline_OrOneNotYetServing_StandsNothingDownAsync() {
    await _startAsync();
    await _onlineAsync("1.0.0");
    await _onlineAsync("0.9.0");
    await _onlineAsync("2.0.0", "Migrating");

    await using var provider = _composeAt("1.0.0");
    var assessment = await provider.GetRequiredService<IStartupAssessor>().AssessAsync(CancellationToken.None);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.Current).Because(assessment.Reason);
  }

  [Test]
  public async Task TheOnlineVersions_AreReadForTheAnnouncementAsync() {
    await _startAsync();
    var running = await _onlineAsync("2.0.0");
    await _onlineAsync("2.0.0", "Migrating");

    await using var provider = _composeAt("1.0.0");
    var online = await provider.GetRequiredService<IOnlineVersionReader>().ReadOnlineAsync(CancellationToken.None);

    await Assert.That(online).IsEquivalentTo([new VersionSighting(BuildInfo.Version, "2.0.0", running)]);
  }

  // ── #2022: the rollback gate, as the EF Core driver applies it ──

  private async Task<int> _versionAsync(string library, string application) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    return await db.ExecuteScalarAsync<int>("""
      INSERT INTO wh_schema_versions (library_version, application_version) VALUES (@library, @application)
      ON CONFLICT (library_version, application_version) DO UPDATE SET notes = wh_schema_versions.notes
      RETURNING id
      """, new { library, application });
  }

  private async Task<StartupAssessment> _assessAtAsync(string applicationVersion) {
    await using var provider = _composeAt(applicationVersion);
    return await provider.GetRequiredService<IStartupAssessor>().AssessAsync(CancellationToken.None);
  }

  [Test]
  public async Task AnApplicationRollback_WithNothingMigratedSince_ServesAsync() {
    await _startAsync();
    var newer = await _versionAsync(BuildInfo.Version, "2.0.0");
    await _execAsync($"UPDATE wh_schema_migrations SET version_id = {newer}, status = 3");

    var assessment = await _assessAtAsync("1.9.0");

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown).Because(assessment.Reason);
  }

  [Test]
  public async Task AnApplicationRollback_AfterAnApplicationSchemaChange_StandsDown_NamingItAsync() {
    await _startAsync();
    var newer = await _versionAsync(BuildInfo.Version, "2.0.0");
    await _execAsync($"UPDATE wh_schema_migrations SET version_id = {newer}, content_hash = 'new-columns', status = 2 WHERE file_name = 'perspective:AssessProbe'");

    var assessment = await _assessAtAsync("1.9.0");

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(assessment.Reason).Contains(
      "1 schema change(s) were applied by a newer version that this version did not make, and a migration cannot be "
      + "reversed yet (down migrations: whizbang-lib/whizbang#2122): perspective:AssessProbe (applied by library "
      + $"{BuildInfo.Version}, application 2.0.0)");
  }

  [Test]
  public async Task AWizbangRollback_AfterAWizbangMigration_StandsDown_AndOneThatOnlyReStamped_ServesAsync() {
    await _startAsync();
    var newer = await _versionAsync(_newerThanThisBinary(), "unknown");
    await _execAsync($"UPDATE wh_schema_migrations SET version_id = {newer}, status = 3");
    await Assert.That((await _assessAtAsync("1.0.0")).Verdict).IsNotEqualTo(StartupVerdict.StandDown)
      .Because("a newer library whose passes changed nothing left nothing this binary cannot serve");

    await _execAsync("UPDATE wh_schema_migrations SET content_hash = 'applied-by-a-newer-release', status = 2 WHERE file_name = (SELECT max(file_name) FROM wh_schema_migrations WHERE file_name ~ '^[0-9]')");
    var assessment = await _assessAtAsync("1.0.0");

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(assessment.Reason).Contains($"(applied by library {_newerThanThisBinary()}, application unknown)");
  }
}
