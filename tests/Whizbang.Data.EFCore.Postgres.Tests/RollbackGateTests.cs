// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Versioning;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2022, owner decision 3: the rollback gate. An instance may serve a database a newer version has touched only when
/// nothing was applied after its own version: no W! migration applied by a newer library, and no application schema
/// change (a perspective table, an association) applied by a newer application or library. A migration cannot be
/// reversed yet (#2122), so anything applied since stands the instance down with an error naming each change and who
/// applied it. A schema pass that applied nothing (it only re-stamped the ledger) leaves nothing to fear: an application
/// rolled back with nothing migrated since serves normally.
/// </summary>
/// <remarks>
/// Who applied a change is <c>wh_schema_migrations.applied_version_id</c> (351), kept by a trigger: set when a row is
/// inserted or its content hash changes, kept through skips and re-stamps, never moved by a failure. The ledger rows are
/// written here as a pass writes them, so the trigger decides what each write applied.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/351_TheLedgerRecordsWhoAppliedEachChange.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresStartupAssessment.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class RollbackGateTests : EFCoreTestBase {
  private const string SINCE = "schema change(s) were applied by a newer version that this version did not make, and a migration "
    + "cannot be reversed yet (down migrations: whizbang-lib/whizbang#2122)";

  private EFCorePostgresStartupAssessor _assessor(string library, string? application) {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    return new EFCorePostgresStartupAssessor(
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext),
      new LibraryVersionProvider(library), new ApplicationVersionProvider(ApplicationVersion.FromRecorded(application)));
  }

  private async Task<T> _scalarAsync<T>(string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (T)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private async Task _execAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync(ct);
  }

  /// <summary>The library version this test database's ledger was written at: the version of this build.</summary>
  private Task<string> _ledgerLibraryAsync(CancellationToken ct) => _scalarAsync<string>("""
    SELECT v.library_version FROM wh_schema_migrations m JOIN wh_schema_versions v ON v.id = m.applied_version_id
    WHERE m.owner = 'whizbang' ORDER BY m.file_name DESC LIMIT 1
    """, ct);

  private Task<int> _versionAsync(string library, string application, CancellationToken ct) => _scalarAsync<int>("""
    INSERT INTO wh_schema_versions (library_version, application_version) VALUES (@library, @application)
    ON CONFLICT (library_version, application_version) DO UPDATE SET notes = wh_schema_versions.notes
    RETURNING id
    """, ct, ("library", library), ("application", application));

  /// <summary>A pass by <paramref name="versionId"/> that applied nothing: every row skipped and re-stamped.</summary>
  private Task _restampAsync(int versionId, CancellationToken ct) => _execAsync(
    "UPDATE wh_schema_migrations SET version_id = @v, status = 3, status_description = 'Skipped (hash unchanged)'", ct, ("v", versionId));

  /// <summary>A pass by <paramref name="versionId"/> that changed one row's content: it applied that change.</summary>
  private Task _applyAsync(string fileName, int versionId, CancellationToken ct) => _execAsync(
    "UPDATE wh_schema_migrations SET version_id = @v, content_hash = 'applied-by-a-newer-version', status = 2 WHERE file_name = @f",
    ct, ("v", versionId), ("f", fileName));

  private Task<string> _lastWhizbangMigrationAsync(CancellationToken ct) =>
    _scalarAsync<string>("SELECT max(file_name) FROM wh_schema_migrations WHERE owner = 'whizbang'", ct);

  // ── the record ─────────────────────────────────────────────────────

  [Test]
  [Timeout(60000)]
  public async Task TheLedger_RecordsWhoAppliedAChange_AndKeepsItThroughSkipsAndFailuresAsync(CancellationToken ct) {
    var first = await _versionAsync("1.0.0", "1.0.0", ct);
    var second = await _versionAsync("2.0.0", "1.0.0", ct);
    var third = await _versionAsync("3.0.0", "1.0.0", ct);
    async Task<int?> appliedAsync() => await _scalarAsync<object>(
      "SELECT applied_version_id FROM wh_schema_migrations WHERE file_name = 'gate:probe'", ct) as int?;

    await _execAsync("INSERT INTO wh_schema_migrations (file_name, content_hash, version_id, status, owner) VALUES ('gate:probe', 'a', @v, 1, 'whizbang')",
      ct, ("v", first));
    await Assert.That(await appliedAsync()).IsEqualTo(first).Because("an insert is an apply");

    await _execAsync("UPDATE wh_schema_migrations SET version_id = @v, status = 3 WHERE file_name = 'gate:probe'", ct, ("v", second));
    await Assert.That(await appliedAsync()).IsEqualTo(first).Because("a skip re-stamps version_id and applies nothing");

    await _execAsync("UPDATE wh_schema_migrations SET version_id = @v, content_hash = 'b', status = -1 WHERE file_name = 'gate:probe'", ct, ("v", third));
    await Assert.That(await appliedAsync()).IsEqualTo(first).Because("a failed step rolled back: it applied nothing");

    await _execAsync("UPDATE wh_schema_migrations SET version_id = @v, content_hash = 'c', status = 2 WHERE file_name = 'gate:probe'", ct, ("v", third));
    await Assert.That(await appliedAsync()).IsEqualTo(third).Because("a content change is an apply");

    await _execAsync("INSERT INTO wh_schema_migrations (file_name, content_hash, version_id, status, owner) VALUES ('gate:failed', 'p', @v, -1, 'whizbang')",
      ct, ("v", third));
    await Assert.That(await _scalarAsync<object>("SELECT applied_version_id FROM wh_schema_migrations WHERE file_name = 'gate:failed'", ct))
      .IsEqualTo(DBNull.Value).Because("a row a failure inserted records nothing applied");
  }

  // ── the gate ───────────────────────────────────────────────────────

  [Test]
  [Timeout(60000)]
  public async Task AnApplicationRollback_WithNothingMigratedSince_ServesAsync(CancellationToken ct) {
    var library = await _ledgerLibraryAsync(ct);
    await _restampAsync(await _versionAsync(library, "2.0.0", ct), ct);

    var assessment = await _assessor(library, "1.9.0").AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown)
      .Because("2.0.0's passes applied nothing: a rollback to 1.9.0 is no outage. " + assessment.Reason);
  }

  [Test]
  [Timeout(60000)]
  public async Task AnApplicationRollback_AfterAnApplicationSchemaChange_StandsDown_NamingItAsync(CancellationToken ct) {
    var library = await _ledgerLibraryAsync(ct);
    var newer = await _versionAsync(library, "2.0.0", ct);
    await _execAsync("""
      INSERT INTO wh_schema_migrations (file_name, content_hash, version_id, status, owner)
      VALUES ('wh_per_rollback_probe', 'new-columns', @v, 1, 'perspective')
      """, ct, ("v", newer));

    var assessment = await _assessor(library, "1.9.0").AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(assessment.Reason).IsEqualTo(
      $"since this instance's version (library {library}, application 1.9.0), 1 {SINCE}: wh_per_rollback_probe (applied by "
      + $"library {library}, application 2.0.0); standing down: never apply anything, hold the data plane, report "
      + "not-ready-while-alive");
  }

  [Test]
  [Timeout(60000)]
  public async Task AWizbangRollback_AfterAWizbangMigration_StandsDown_NamingItAsync(CancellationToken ct) {
    var library = await _ledgerLibraryAsync(ct);
    var file = await _lastWhizbangMigrationAsync(ct);
    await _applyAsync(file, await _versionAsync("1000.0.0", "unknown", ct), ct);

    var assessment = await _assessor(library, null).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(assessment.Reason).Contains($"1 {SINCE}: {file} (applied by library 1000.0.0, application unknown)");
  }

  [Test]
  [Timeout(60000)]
  public async Task AWizbangRollback_WithNothingMigratedSince_ServesAsync(CancellationToken ct) {
    var library = await _ledgerLibraryAsync(ct);
    await _restampAsync(await _versionAsync("1000.0.0", "unknown", ct), ct);

    var assessment = await _assessor(library, null).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown)
      .Because("a newer library whose passes changed nothing left nothing this binary cannot serve: " + assessment.Reason);
  }

  [Test]
  [Timeout(60000)]
  public async Task EqualVersions_DoNothingAsync(CancellationToken ct) {
    var library = await _ledgerLibraryAsync(ct);

    var assessment = await _assessor(library, null).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown).Because(assessment.Reason);
  }

  [Test]
  [Timeout(60000)]
  public async Task AStepRow_IsProgress_NotAChangeAsync(CancellationToken ct) {
    var newer = await _versionAsync("1000.0.0", "9.0.0", ct);
    await _execAsync("""
      INSERT INTO wh_schema_migrations (file_name, content_hash, version_id, status, owner)
      VALUES ('step:Ctx:rewrite', 'progress', @v, 1, 'step')
      """, ct, ("v", newer));

    var assessment = await _assessor(await _ledgerLibraryAsync(ct), null).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown).Because(assessment.Reason);
  }

  [Test]
  [Timeout(60000)]
  public async Task OnALedgerOlderThanTheRecord_WhoeverLastStampedARow_IsTakenToHaveAppliedItAsync(CancellationToken ct) {
    // Before 351 the ledger cannot say who applied a change; the version that last stamped a row is the closest it
    // comes, so a newer library there stands down as it did before (#1992).
    var library = await _ledgerLibraryAsync(ct);
    await _execAsync("ALTER TABLE wh_schema_migrations DROP COLUMN applied_version_id CASCADE", ct);
    await _execAsync("DROP TRIGGER IF EXISTS wh_schema_migrations_applied_by ON wh_schema_migrations", ct);
    await _restampAsync(await _versionAsync("1000.0.0", "unknown", ct), ct);

    var assessment = await _assessor(library, null).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(assessment.Reason).Contains("(applied by library 1000.0.0, application unknown)");
  }
}
