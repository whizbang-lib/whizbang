// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The <c>Assess</c> verdict table (increment 9), exhaustively at the pure function and end to end
/// against a real ledger. Ordering is SemVer precedence; an unparseable own version refuses; an
/// unparseable recorded version cannot outrank anything.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresStartupAssessor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresStartupAssessment.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/StoredFormSettlement.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class StartupAssessorTests : EFCoreTestBase {

  // ── the verdict table, as a pure function ───────────────────────────────

  [Test]
  public async Task EmptyLedger_VerdictIsMigrateAsync() {
    var assessment = PostgresStartupAssessment.ComputeVerdict("0.9.4", []);
    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.Migrate)
      .Because("a fresh database is this instance's to migrate — contend for the duty");
  }

  [Test]
  [Arguments("0.9.4", "0.9.3")]
  [Arguments("0.9.4", "0.9.4")]
  [Arguments("1.0.0", "1.0.0-rc.1")]
  [Arguments("1.0.0-alpha.10", "1.0.0-alpha.2")]
  public async Task OnlyOlderOrSameRecorded_VerdictIsServeAsync(string mine, string recorded) {
    var assessment = PostgresStartupAssessment.ComputeVerdict(mine, [recorded]);
    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.Serve);
  }

  [Test]
  [Arguments("0.9.4", "0.9.5")]
  [Arguments("1.0.0-rc.1", "1.0.0")]
  [Arguments("1.0.0-alpha.2", "1.0.0-alpha.10")]
  public async Task AnyNewerRecorded_VerdictIsStandDownAsync(string mine, string recorded) {
    var assessment = PostgresStartupAssessment.ComputeVerdict(mine, ["0.0.1", recorded]);
    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown)
      .Because("numeric pre-release identifiers compare numerically — alpha.10 is newer than "
             + "alpha.2, and being wrong here means migrating when the instance must stand down");
  }

  [Test]
  public async Task NoLibraryVersionProvider_StandsDownNamingTheMissingRegistrationAsync() {
    // Absent and unreadable are different facts. Nobody registering a provider is a wiring gap the
    // driver now closes on its own; an unparseable value is a genuine ambiguity. Both stand down,
    // but the reason must say which one happened — a hang with "(none) is unreadable" cost a
    // process dump to diagnose (issue #619).
    var assessment = PostgresStartupAssessment.ComputeVerdict(null, ["0.9.4"]);
    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown)
      .Because("with no version to compare, migrating would be a guess — stand down, as before");
    await Assert.That(assessment.Reason).Contains("ILibraryVersionProvider")
      .Because("the reason must name the missing registration, not describe a value that was never there");
  }

  [Test]
  public async Task UnparseableOwnVersion_RefusesRatherThanGuessesAsync() {
    var assessment = PostgresStartupAssessment.ComputeVerdict("not-a-version", ["0.9.4"]);
    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown)
      .Because("an unparseable version is not guessed at — every wrong answer here is worse "
             + "than stopping");
  }

  [Test]
  public async Task UnparseableRecordedVersion_CannotOutrankAnythingAsync() {
    var assessment = PostgresStartupAssessment.ComputeVerdict("0.9.4", ["garbage", "0.9.3"]);
    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.Serve)
      .Because("unreadable history does not block — the same stance the ledger guard takes");
  }

  [Test]
  public async Task OnlyUnreadableRecordedVersions_ServeAndSayTheHistoryWasUnreadableAsync() {
    // Every recorded version is unreadable, so there is no newest to compare against. That still
    // serves, and the reason must say why rather than naming a recorded version that does not exist.
    var assessment = PostgresStartupAssessment.ComputeVerdict("0.9.4", ["garbage", "also-garbage"]);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.Serve);
    await Assert.That(assessment.Reason).Contains("only unreadable versions recorded");
    await Assert.That(assessment.Reason).Contains("0.9.4");
  }

  // ── end to end against the real ledger ─────────────────────────────────

  private EFCorePostgresStartupAssessor _assessorRunning(string version) {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    var provider = services.BuildServiceProvider();
    return new EFCorePostgresStartupAssessor(
      provider.GetRequiredService<IServiceScopeFactory>(),
      typeof(WorkCoordinationDbContext),
      new LibraryVersionProvider(version));
  }

  [Test]
  [Timeout(60000)]
  public async Task AgainstTheRealLedger_ANewerRecordedVersion_StandsThisBinaryDownAsync(
      CancellationToken cancellationToken) {
    // The ledger was written by this build; a binary claiming the SemVer floor is older than it.
    var standDown = await _assessorRunning("0.0.0-0").AssessAsync(cancellationToken);
    await Assert.That(standDown.Verdict).IsEqualTo(StartupVerdict.StandDown);

    // And a binary far in the future outranks everything recorded.
    var serve = await _assessorRunning("999.0.0").AssessAsync(cancellationToken);
    await Assert.That(serve.Verdict).IsNotEqualTo(StartupVerdict.StandDown);
  }

  /// <summary>
  /// #1992: a schema with nothing to apply and nothing left to convert is Current, the verdict that skips the election
  /// and the migration locks. Two initializations: the first creates and converts, the second settles.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task AnUpToDateSchema_IsCurrent_AndOneWithWorkPending_IsServeAsync(CancellationToken cancellationToken) {
    await _initializeTwiceAsync(cancellationToken);

    var current = await _assessorRunning("999.0.0").AssessAsync(cancellationToken);
    await Assert.That(current.Verdict).IsEqualTo(StartupVerdict.Current)
      .Because("the same lock-free checks the fast path makes find nothing to do: " + current.Reason);

    await _execAsync("DELETE FROM wh_schema_migrations WHERE file_name = (SELECT max(file_name) FROM wh_schema_migrations WHERE owner = 'whizbang')", cancellationToken);
    var pending = await _assessorRunning("999.0.0").AssessAsync(cancellationToken);
    await Assert.That(pending.Verdict).IsEqualTo(StartupVerdict.Serve)
      .Because("a migration this binary owes is work: the election and the pass run as they always have");
  }

  [Test]
  [Timeout(60000)]
  public async Task ACurrencyCheckThatCannotAnswer_IsServe_NeverCurrent_AndACancellationPropagatesAsync(
      CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(cancellationToken);

    var unanswered = await PostgresStartupAssessment.AssessAsync(connection, "public", new Whizbang.Core.Versioning.VersionSighting("999.0.0", null), false,
      _ => throw new InvalidOperationException("the catalog could not be read"), cancellationToken);

    await Assert.That(unanswered.Verdict).IsEqualTo(StartupVerdict.Serve)
      .Because("a schema that cannot be shown current takes the full path, which finds out for itself");
    await Assert.That(unanswered.Reason).Contains("could not be shown current");
    await Assert.That(async () => await PostgresStartupAssessment.AssessAsync(connection, "public", new Whizbang.Core.Versioning.VersionSighting("999.0.0", null), false,
        _ => throw new OperationCanceledException(), cancellationToken))
      .Throws<OperationCanceledException>();
  }

  [Test]
  [Timeout(60000)]
  public async Task ABrandNewDatabase_HasNoVersionRecord_AndMigratesAsync(CancellationToken cancellationToken) {
    await _execAsync("DROP TABLE wh_schema_migrations CASCADE", cancellationToken);

    var assessment = await _assessorRunning("1.0.0").AssessAsync(cancellationToken);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.Migrate)
      .Because("no version record means nothing newer can exist: the bootstrap that follows is safe");
  }

  [Test]
  [Timeout(60000)]
  public async Task ALedgerWithNoVersionTable_RecordsNothingNewer_AndMigratesAsync(CancellationToken cancellationToken) {
    await _execAsync("DROP TABLE wh_schema_versions CASCADE", cancellationToken);

    var assessment = await _assessorRunning("1.0.0").AssessAsync(cancellationToken);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.Migrate);
  }

  [Test]
  [Timeout(60000)]
  public async Task AVersionRecordThatCannotBeRead_IsNeverTakenForABrandNewDatabaseAsync(CancellationToken cancellationToken) {
    await _execAsync("ALTER TABLE wh_schema_versions RENAME COLUMN library_version TO lib_version", cancellationToken);

    await Assert.That(async () => await _assessorRunning("1.0.0").AssessAsync(cancellationToken))
      .Throws<PostgresException>()
      .Because("the record exists and cannot be read: the source turns that into a loud failure, never a blind migration");
  }

  [Test]
  [Timeout(120000)]
  public async Task StoredForms_AreSettledOnlyWhenEveryLedgerSaysSoAsync(CancellationToken cancellationToken) {
    await _execAsync("""
      CREATE TABLE wh_per_settle_probe (id uuid PRIMARY KEY, status text, amount integer);
      CREATE TABLE wh_per_settled (id uuid PRIMARY KEY);
      CREATE TABLE wh_per_open (id uuid PRIMARY KEY);
      INSERT INTO wh_perspective_forms (table_name, temporal_form, applied_at, settled_at) VALUES ('wh_per_settled', 1, now(), now());
      INSERT INTO wh_perspective_forms (table_name, temporal_form, applied_at, settled_at) VALUES ('wh_per_open', 1, now(), NULL);
      INSERT INTO wh_stored_form_migrations (name, table_name, kind, first_applied_at, last_applied_at, rows_converted, settled_at)
        VALUES ('m-settled', 'wh_per_settled', 'generated', now(), now(), 0, now()),
               ('m-open', 'wh_per_open', 'generated', now(), now(), 0, NULL);
      """, cancellationToken);
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(cancellationToken);

    async Task<bool> settledAsync(string[] temporal, (string Name, string Table)[] journal, string[] physical) =>
      await StoredFormSettlement.IsSettledAsync(connection, "public", temporal, journal, physical, cancellationToken);

    await Assert.That(await settledAsync([], [], [])).IsTrue().Because("a model with nothing to convert has nothing pending");
    await Assert.That(await settledAsync(["wh_per_settled"], [("m-settled", "wh_per_settled")], ["enum-column:wh_per_settle_probe.amount"]))
      .IsTrue();
    await Assert.That(await settledAsync(["wh_per_open"], [], [])).IsFalse().Because("a temporal rewrite not yet settled");
    await Assert.That(await settledAsync(["wh_per_settle_probe"], [], [])).IsFalse()
      .Because("a table that exists and that the rewrite never recorded has not been checked yet");
    await Assert.That(await settledAsync(["wh_per_not_created"], [("m-never-declared", "wh_per_not_created")], [])).IsTrue()
      .Because("a conversion waits for its table: with no table there is nothing to convert");
    await Assert.That(await settledAsync([], [("m-open", "wh_per_open")], [])).IsFalse().Because("a stored-form migration not yet settled");
    await Assert.That(await settledAsync([], [("m-never-declared", "wh_per_settle_probe")], [])).IsFalse();
    await Assert.That(await settledAsync([], [], ["enum-column:wh_per_settle_probe.status"])).IsFalse()
      .Because("an enum column still stored as text has its conversion pending");
    await Assert.That(await settledAsync([], [], ["something-else:wh_per_settle_probe.amount"])).IsFalse()
      .Because("a rewrite whose settledness cannot be read is never assumed settled");
    await Assert.That(await settledAsync([], [], ["enum-column:no_column_named"])).IsFalse();
    await Assert.That(await StoredFormSettlement.IsSettledAsync(connection, "", ["wh_per_open"], [], [], cancellationToken)).IsFalse()
      .Because("no schema named is the default one");
  }

  private async Task _initializeTwiceAsync(CancellationToken cancellationToken) {
    for (var i = 0; i < 2; i++) {
      await using var context = CreateDbContext();
      await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
    }
  }

  private async Task _execAsync(string sql, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand(sql, conn);
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  /// <summary>
  /// A ledger with no <c>owner</c> column is the Dapper driver's, or one an older library wrote: every row in it was
  /// recorded by the framework, so every version it records is read (#1992). Before, the assessment took a missing
  /// column for a ledger still being created and answered Migrate, which on the Dapper driver would have meant a stood-
  /// down instance migrating.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task ALedgerWithoutAnOwnerColumn_IsReadWhole_SoANewerVersionStillStandsDownAsync(CancellationToken cancellationToken) {
    await _execAsync("""
      ALTER TABLE wh_schema_migrations DROP COLUMN owner CASCADE;
      WITH v AS (INSERT INTO wh_schema_versions (library_version) VALUES ('999.0.0') RETURNING id)
      UPDATE wh_schema_migrations SET version_id = (SELECT id FROM v), content_hash = 'applied-by-999'
        WHERE file_name = (SELECT min(file_name) FROM wh_schema_migrations);
      """, cancellationToken);

    var assessment = await _assessorRunning("1.0.0").AssessAsync(cancellationToken);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown);
  }
}
