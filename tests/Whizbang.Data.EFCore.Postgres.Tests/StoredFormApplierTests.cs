// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Startup;
using Whizbang.Core.Versioning;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2131: a stored-form conversion records which library and application version applied it, so the rollback gate
/// counts it like any other change applied since an older version. The stored-form migrations and the declared-key
/// pass record into <c>wh_stored_form_migrations</c>, the temporal rewrite into <c>wh_perspective_forms</c>; each records
/// the version that converted rows and keeps it through a later pass that converts nothing.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/StoredFormApplier.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresStartupAssessment.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class StoredFormApplierTests : EFCoreTestBase {
  private const string TABLE = "wh_per_applier_probe";
  private const long LOCK_ID = 987654131;
  private const int TIMEOUT_SECONDS = 30;

  private static readonly StoredFormApplier _older = new("9.0.0", "2.0.0");
  private static readonly StoredFormApplier _newer = new("10.0.0", "3.0.0");

  private NpgsqlConnection _open() => new(ConnectionString);

  private async Task _execAsync(string sql, CancellationToken ct) {
    await using var connection = _open();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync(ct);
  }

  private async Task<string?> _textAsync(string sql, CancellationToken ct) {
    await using var connection = _open();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(sql, connection);
    return await command.ExecuteScalarAsync(ct) as string;
  }

  private Task _applyAsync(IReadOnlyList<(string Name, string Sql)> rewrites, CancellationToken ct) =>
    CanonicalTemporalRewritePhase.ApplyAsync(_open, LOCK_ID, rewrites, TIMEOUT_SECONDS, null, ct);

  // ── the record ─────────────────────────────────────────────────────

  [Test]
  [Timeout(60000)]
  public async Task AStoredFormMigration_RecordsWhoConvertedRows_AndKeepsItThroughAPassThatConvertsNothingAsync(CancellationToken ct) {
    await _execAsync($"CREATE TABLE {TABLE} (id int PRIMARY KEY, data jsonb)", ct);
    await _execAsync($$"""INSERT INTO {{TABLE}} VALUES (1, '{"Status": 123}')""", ct);
    var migration = StoredFormMigrationSql.Generated("public", TABLE, "applier:Status", StoredFormStep.ToText("Status"));

    using (StoredFormApplier.ScopeTo(_older)) {
      await StoredFormMigrationSql.DeclareAsync(_open, "public", [migration], ct);
      await _applyAsync(StoredFormMigrationSql.ForPhase("public", [migration]), ct);
    }
    using (StoredFormApplier.ScopeTo(_newer)) {
      await _applyAsync(StoredFormMigrationSql.ForPhase("public", [migration]), ct);
    }

    await Assert.That(await _textAsync(
        "SELECT applied_library_version || '/' || applied_application_version FROM wh_stored_form_migrations WHERE name = 'applier:Status'", ct))
      .IsEqualTo("9.0.0/2.0.0")
      .Because("9.0.0 converted the row; 10.0.0's pass found nothing to convert and applied nothing");
  }

  [Test]
  [Timeout(60000)]
  public async Task ACustomMigration_RecordsWhoRanItAsync(CancellationToken ct) {
    await _execAsync($"CREATE TABLE {TABLE} (id int PRIMARY KEY, data jsonb)", ct);
    var migration = StoredFormMigrationSql.Custom("public", TABLE, new NoRowsMigration());

    using (StoredFormApplier.ScopeTo(_newer)) {
      await StoredFormMigrationSql.DeclareAsync(_open, "public", [migration], ct);
      await _applyAsync(StoredFormMigrationSql.ForPhase("public", [migration]), ct);
    }

    await Assert.That(await _textAsync(
        "SELECT applied_library_version FROM wh_stored_form_migrations WHERE name = 'applier:custom'", ct))
      .IsEqualTo("10.0.0").Because("a custom migration is not assumed reversible, whatever it changed");
  }

  [Test]
  [Timeout(60000)]
  public async Task TheTemporalRewrite_RecordsWhoConvertedATable_AndKeepsItAsync(CancellationToken ct) {
    await _execAsync($"CREATE TABLE {TABLE} (id int PRIMARY KEY, data jsonb)", ct);
    await _execAsync($$"""INSERT INTO {{TABLE}} VALUES (1, '{"At": "2026-01-02T03:04:05Z"}')""", ct);
    var paths = ImmutableArray.Create(new TemporalPath("data", ["At"], StoredTemporalKind.Instant));

    using (StoredFormApplier.ScopeTo(_older)) {
      await _applyAsync([(TABLE, CanonicalTemporalRewrite.StatementFor("public", TABLE, paths))], ct);
    }
    using (StoredFormApplier.ScopeTo(_newer)) {
      await _applyAsync([(TABLE, CanonicalTemporalRewrite.StatementFor("public", TABLE, paths))], ct);
    }

    await Assert.That(await _textAsync(
        $"SELECT applied_library_version || '/' || applied_application_version FROM wh_perspective_forms WHERE table_name = '{TABLE}'", ct))
      .IsEqualTo("9.0.0/2.0.0");
  }

  [Test]
  [Timeout(60000)]
  public async Task TheDeclaredKeyPass_RecordsWhoMovedKeysAsync(CancellationToken ct) {
    await _execAsync($"CREATE TABLE {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb NULL)", ct);
    await _execAsync($$"""INSERT INTO {{TABLE}} VALUES ('{{Guid.CreateVersion7()}}', '{"Name": "a"}', NULL)""", ct);
    var table = new DeclaredKeyTable(TABLE, [new DeclaredKeyColumn("data", DeclaredKeyShape.ForObject(new DeclaredMember("name")))]);

    using (StoredFormApplier.ScopeTo(_newer)) {
      await DeclaredKeySpellingPass.ApplyAsync(_open, "public", [table], TIMEOUT_SECONDS, logger: null, cancellationToken: ct);
    }

    await Assert.That(await _textAsync(
        $"SELECT applied_library_version FROM wh_stored_form_migrations WHERE name = '{table.JournalName}'", ct))
      .IsEqualTo("10.0.0");
  }

  [Test]
  [Timeout(60000)]
  public async Task TheDeclaredKeyPass_OnAJournalOlderThanTheRecord_StillMovesTheKeysAsync(CancellationToken ct) {
    await _execAsync("ALTER TABLE wh_stored_form_migrations DROP COLUMN applied_library_version, DROP COLUMN applied_application_version", ct);
    await _execAsync($"CREATE TABLE {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb NULL)", ct);
    await _execAsync($$"""INSERT INTO {{TABLE}} VALUES ('{{Guid.CreateVersion7()}}', '{"Name": "a"}', NULL)""", ct);
    var table = new DeclaredKeyTable(TABLE, [new DeclaredKeyColumn("data", DeclaredKeyShape.ForObject(new DeclaredMember("name")))]);

    using (StoredFormApplier.ScopeTo(_newer)) {
      await DeclaredKeySpellingPass.ApplyAsync(_open, "public", [table], TIMEOUT_SECONDS, logger: null, cancellationToken: ct);
    }

    await Assert.That(await _textAsync($"SELECT data ->> 'name' FROM {TABLE}", ct)).IsEqualTo("a")
      .Because("a journal with no place for the applier records the conversion without it");
  }

  [Test]
  [Timeout(60000)]
  public async Task OutsideAnyScope_AConversionRecordsNobodyAsync(CancellationToken ct) {
    await _execAsync($"CREATE TABLE {TABLE} (id int PRIMARY KEY, data jsonb)", ct);
    await _execAsync($$"""INSERT INTO {{TABLE}} VALUES (1, '{"Status": 123}')""", ct);
    var migration = StoredFormMigrationSql.Generated("public", TABLE, "applier:Status", StoredFormStep.ToText("Status"));

    await StoredFormMigrationSql.DeclareAsync(_open, "public", [migration], ct);
    await _applyAsync(StoredFormMigrationSql.ForPhase("public", [migration]), ct);

    await Assert.That(await _textAsync(
        "SELECT coalesce(applied_library_version, 'nobody') FROM wh_stored_form_migrations WHERE name = 'applier:Status'", ct))
      .IsEqualTo("nobody").Because("an unknown applier never stands an instance down");
  }

  [Test]
  public async Task ScopeTo_RestoresTheApplierBefore_AndRefusesNoneAsync() {
    using (StoredFormApplier.ScopeTo(_older)) {
      using (StoredFormApplier.ScopeTo(_newer)) {
        await Assert.That(StoredFormApplier.Current).IsEqualTo(_newer);
      }
      await Assert.That(StoredFormApplier.Current).IsEqualTo(_older);
    }
    await Assert.That(StoredFormApplier.Current).IsNull();
    await Assert.That(() => StoredFormApplier.ScopeTo(null!)).Throws<ArgumentNullException>();
  }

  // ── the gate ───────────────────────────────────────────────────────

  private EFCorePostgresStartupAssessor _assessor(string library, string? application) {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    return new EFCorePostgresStartupAssessor(
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext),
      new LibraryVersionProvider(library), new ApplicationVersionProvider(ApplicationVersion.FromRecorded(application)));
  }

  private Task<string?> _libraryAsync(CancellationToken ct) => _textAsync("""
    SELECT v.library_version FROM wh_schema_migrations m JOIN wh_schema_versions v ON v.id = m.applied_version_id
    WHERE m.owner = 'whizbang' ORDER BY m.file_name DESC LIMIT 1
    """, ct);

  [Test]
  [Timeout(60000)]
  public async Task AStoredFormConversionByANewerApplication_StandsTheOlderDown_NamingItAsync(CancellationToken ct) {
    var library = (await _libraryAsync(ct))!;
    await _execAsync($"""
      INSERT INTO wh_stored_form_migrations (name, table_name, kind, rows_converted, applied_library_version, applied_application_version)
      VALUES ('wh_per_orders.Status:Int32->String', 'wh_per_orders', 'generated', 3, '{library}', '2.0.0')
      """, ct);

    var assessment = await _assessor(library, "1.9.0").AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(assessment.Reason)
      .Contains($"stored-form:wh_per_orders.Status:Int32->String (applied by library {library}, application 2.0.0)");
  }

  [Test]
  [Timeout(60000)]
  public async Task ATemporalConversionByANewerLibrary_StandsTheOlderDown_NamingItAsync(CancellationToken ct) {
    var library = (await _libraryAsync(ct))!;
    await _execAsync("""
      INSERT INTO wh_perspective_forms (table_name, temporal_form, applied_library_version, applied_application_version)
      VALUES ('wh_per_gate_probe', 2, '1000.0.0', 'unknown')
      """, ct);

    var assessment = await _assessor(library, null).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(assessment.Reason).Contains("temporal-form:wh_per_gate_probe (applied by library 1000.0.0, application unknown)");
  }

  [Test]
  [Timeout(60000)]
  public async Task AConversionByThisVersion_OrByNobodyKnown_DoesNotStandItDownAsync(CancellationToken ct) {
    var library = (await _libraryAsync(ct))!;
    await _execAsync($"""
      INSERT INTO wh_stored_form_migrations (name, table_name, kind, rows_converted, applied_library_version, applied_application_version)
      VALUES ('mine', 't', 'generated', 3, '{library}', '1.9.0'), ('nobody', 't', 'generated', 3, NULL, NULL)
      """, ct);

    var assessment = await _assessor(library, "1.9.0").AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown).Because(assessment.Reason);
  }

  [Test]
  [Timeout(60000)]
  public async Task OnJournalsOlderThanTheRecord_TheGateReadsTheLedgerAloneAsync(CancellationToken ct) {
    var library = (await _libraryAsync(ct))!;
    await _execAsync("ALTER TABLE wh_stored_form_migrations DROP COLUMN applied_library_version, DROP COLUMN applied_application_version", ct);
    await _execAsync("ALTER TABLE wh_perspective_forms DROP COLUMN applied_library_version, DROP COLUMN applied_application_version", ct);

    var assessment = await _assessor(library, null).AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown).Because(assessment.Reason);
  }

  // ── #2553: what a conversion applied, and a restored database's conversions ──

  [Test]
  [Timeout(60000)]
  public async Task EveryConversion_RecordsWhatItApplied_AsTheHashOfItsStatementAsync(CancellationToken ct) {
    await _execAsync($"CREATE TABLE {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb NULL)", ct);
    await _execAsync($$"""INSERT INTO {{TABLE}} VALUES ('{{Guid.CreateVersion7()}}', '{"Status": 123, "At": "2026-01-02T03:04:05Z", "Name": "a"}', NULL)""", ct);
    var migration = StoredFormMigrationSql.Generated("public", TABLE, "applier:Status", StoredFormStep.ToText("Status"));
    var temporal = CanonicalTemporalRewrite.StatementFor("public", TABLE,
      ImmutableArray.Create(new TemporalPath("data", ["At"], StoredTemporalKind.Instant)));
    var declared = new DeclaredKeyTable(TABLE, [new DeclaredKeyColumn("data", DeclaredKeyShape.ForObject(new DeclaredMember("name")))]);

    using (StoredFormApplier.ScopeTo(_newer)) {
      await StoredFormMigrationSql.DeclareAsync(_open, "public", [migration], ct);
      await _applyAsync([.. StoredFormMigrationSql.ForPhase("public", [migration]), (TABLE, temporal)], ct);
      await DeclaredKeySpellingPass.ApplyAsync(_open, "public", [declared], TIMEOUT_SECONDS, logger: null, cancellationToken: ct);
    }

    await Assert.That(await _textAsync("SELECT content_hash FROM wh_stored_form_migrations WHERE name = 'applier:Status'", ct))
      .IsEqualTo(StoredFormApplier.ContentHash(migration.Sql));
    await Assert.That(await _textAsync($"SELECT content_hash FROM wh_perspective_forms WHERE table_name = '{TABLE}'", ct))
      .IsEqualTo(StoredFormApplier.ContentHash(temporal));
    await Assert.That(await _textAsync($"SELECT content_hash FROM wh_stored_form_migrations WHERE name = '{declared.JournalName}'", ct))
      .IsEqualTo(declared.ContentHash);
  }

  /// <summary>Every conversion this build's model declares, by the gate's name for it, with its journal and key.</summary>
  private List<(string Key, string Journal, string Column, string Name, string Hash)> _declaredConversions() {
    using var context = CreateDbContext();
    var options = PerspectiveDocumentSerialization.Options;
    List<(string, string, string, string, string)> found = [
      .. CanonicalTemporalRewrite.ForModel(context.Model, options, "public")
        .Select(r => ("temporal-form:" + r.Name, "wh_perspective_forms", "table_name", r.Name, StoredFormApplier.ContentHash(r.Sql))),
      .. DictionaryPairsRewrite.ForModel(context.Model, options, "public")
        .Select(t => ("stored-form:" + t.Name, "wh_stored_form_migrations", "name", t.Name, StoredFormApplier.ContentHash(t.Sql))),
      .. DeclaredKeyShapes.ForModel(context.Model, options)
        .Select(t => ("stored-form:" + t.JournalName, "wh_stored_form_migrations", "name", t.JournalName, t.ContentHash)),
    ];
    return found;
  }

  /// <summary>
  /// The journals as a database restored from another environment holds them: every conversion this build declares,
  /// applied by that environment's SemVer-higher application version, written before the journals recorded hashes.
  /// </summary>
  private async Task<List<(string Key, string Journal, string Column, string Name, string Hash)>> _restoreConversionsAsync(
      string library, CancellationToken ct) {
    var declared = _declaredConversions();
    await Assert.That(declared.Count).IsGreaterThan(0).Because("the scenario needs conversions this build declares");
    foreach (var (_, journal, column, name, _) in declared) {
      var insert = journal == "wh_perspective_forms"
        ? $"INSERT INTO wh_perspective_forms (table_name, temporal_form) VALUES ('{name}', 2) ON CONFLICT (table_name) DO NOTHING"
        : $"INSERT INTO wh_stored_form_migrations (name, table_name, kind) VALUES ('{name}', 't', 'generated') ON CONFLICT (name) DO NOTHING";
      await _execAsync(insert, ct);
      await _execAsync(
        $"UPDATE {journal} SET applied_library_version = '{library}', applied_application_version = '1.63.0-rc.3' WHERE {column} = '{name}'", ct);
    }
    return declared;
  }

  [Test]
  [Timeout(60000)]
  public async Task ARestoredDatabase_WhoseConversionsThisBuildDeclares_ServesWhateverApplicationAppliedThemAsync(CancellationToken ct) {
    var library = (await _libraryAsync(ct))!;
    await _restoreConversionsAsync(library, ct);

    var assessment = await _assessor(library, "0.0.0-preview.restored").AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown)
      .Because("a conversion recorded before its hash is identified by its name, which this build declares: " + assessment.Reason);
    await Assert.That(assessment.Rollback).IsNull();
  }

  [Test]
  [Timeout(60000)]
  public async Task OnceAConversionRecordsItsHash_TheHashDecides_NotTheNameAsync(CancellationToken ct) {
    var library = (await _libraryAsync(ct))!;
    var declared = await _restoreConversionsAsync(library, ct);
    foreach (var (_, journal, column, name, hash) in declared) {
      await _execAsync($"UPDATE {journal} SET content_hash = '{hash}' WHERE {column} = '{name}'", ct);
    }
    var serving = await _assessor(library, "0.0.0-preview.restored").AssessAsync(ct);
    var (key, firstJournal, firstColumn, firstName, _) = declared[0];
    await _execAsync($"UPDATE {firstJournal} SET content_hash = 'an-older-statement' WHERE {firstColumn} = '{firstName}'", ct);

    var refused = await _assessor(library, "0.0.0-preview.restored").AssessAsync(ct);

    await Assert.That(serving.Verdict).IsNotEqualTo(StartupVerdict.StandDown).Because(serving.Reason);
    await Assert.That(refused.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(refused.Reason).Contains($"{key} (applied by library {library}, application 1.63.0-rc.3)");
  }

  private sealed class NoRowsMigration : IStoredFormMigration {
    public string Name => "applier:custom";
    public string BuildSql(StoredFormMigrationTarget target) => $"UPDATE {target.QualifiedTable} SET data = data WHERE false";
  }
}
