// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// A migration that runs again over a database it already migrated finds what its earlier run did, on a
/// service whose EF Core context owns a named schema; and the run-time functions work in that schema.
/// </summary>
/// <remarks>
/// <para>
/// The ledger re-runs every file whose hash changed, so an upgrade re-runs each shipped migration whose
/// text differs between the two releases (0.2620.0's 046 differs from 0.1024.0's) against a schema that
/// already holds what the earlier text created. Each guard that makes the re-run safe asks the catalog
/// whether a column or table already exists. Two things made those questions unanswerable on the EF Core
/// driver for a context with a schema of its own, while the connection's <c>search_path</c> stayed the
/// default:
/// </para>
/// <list type="bullet">
///   <item>the guards asked about <c>current_schema()</c>, which on that connection is <c>public</c>,
///   not the schema the guarded DDL alters; and</item>
///   <item>the driver's qualification of bare framework table names reached inside string literals,
///   so <c>table_name = 'wh_inbox'</c> became <c>table_name = '"schema".wh_inbox'</c>, which no row
///   can equal.</item>
/// </list>
/// <para>
/// Either one alone makes a guard read "not there yet". On an empty database that is true, which is
/// why every first run passed; on a re-run the guarded DDL then failed (an <c>ADD COLUMN</c> raising
/// 42701) or a repair quietly never happened. The run-time functions asked <c>current_schema()</c> too,
/// on pooled connections, and so looked for their tables in <c>public</c>. Every other context in this
/// project migrates <c>public</c>, where none of this shows, so this class keeps one context that does not.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard2")]
public class NamedSchemaMigrationReplayTests {
  private const string SCHEMA = NamedSchemaDbContext.SCHEMA;

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("namedschema");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is null) {
      return;
    }
    await PerTestDatabaseFactory.DropAsync(_databaseName);
  }

  /// <summary>
  /// Every migration re-runs over a schema it already migrated (the upgrade shape, for every file at
  /// once), and each guard finds the state the first run left.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task EveryMigrationReRunsOverItsOwnEarlierRunAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);

    await Assert.That(await _scalarAsync<bool>($"SELECT to_regclass('{SCHEMA}.wh_inbox') IS NOT NULL")).IsTrue()
      .Because("precondition: the framework tables belong to the context's schema");
    await Assert.That(await _scalarAsync<string>("SELECT current_schema()")).IsEqualTo("public")
      .Because("precondition: the connection keeps the default search_path, as a deployed service's does");

    // An upgrade from a release whose copy of every file differed: the ledger holds a hash no file has.
    var edited = await _executeAsync(
      $"UPDATE {SCHEMA}.wh_schema_migrations SET content_hash = 'an-earlier-release' WHERE owner = 'whizbang'");
    await Assert.That(edited).IsGreaterThan(100)
      .Because("precondition: the first run recorded every migration it applied");

    // Throws when any migration fails, naming it; 046's guarded ADD COLUMN was the first to.
    await _initializeAsync(cancellationToken);

    await Assert.That(await _scalarAsync<long>(
        $"SELECT count(*) FROM {SCHEMA}.wh_schema_migrations WHERE owner = 'whizbang' AND content_hash = 'an-earlier-release'"))
      .IsEqualTo(0L)
      .Because("every edited migration ran again and recorded its current hash");
  }

  /// <summary>
  /// The same replay in <c>public</c>, where no name is qualified: every file this change edited
  /// re-runs on upgrade there too, so each must be safe to run over its own earlier run on both paths.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task EveryMigrationReRunsOverItsOwnEarlierRun_InPublicTooAsync(CancellationToken cancellationToken) {
    await _initializePublicAsync(cancellationToken);

    var edited = await _executeAsync(
      "UPDATE public.wh_schema_migrations SET content_hash = 'an-earlier-release' WHERE owner = 'whizbang'");
    await Assert.That(edited).IsGreaterThan(100);

    await _initializePublicAsync(cancellationToken);

    await Assert.That(await _scalarAsync<long>(
        "SELECT count(*) FROM public.wh_schema_migrations WHERE owner = 'whizbang' AND content_hash = 'an-earlier-release'"))
      .IsEqualTo(0L);
  }

  /// <summary>
  /// The upgrade that failed: a database migrated by an earlier release, whose ledger records a different
  /// hash for 046, starts on this one. 046 runs again over the columns its earlier run added.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task AnUpgradeReRunsTheChanged046OverItsEarlierRunAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);
    await Assert.That(await _scalarAsync<bool>($"""
        SELECT EXISTS (SELECT 1 FROM information_schema.columns
          WHERE table_schema = '{SCHEMA}' AND table_name = 'wh_inbox' AND column_name = 'source_service_id')
        """)).IsTrue()
      .Because("precondition: the earlier run added the column 046 adds");
    await _executeAsync(
      $"UPDATE {SCHEMA}.wh_schema_migrations SET content_hash = 'an-earlier-release' WHERE file_name LIKE '046%'");

    // Threw "42701: column \"source_service_id\" of relation \"wh_inbox\" already exists".
    await _initializeAsync(cancellationToken);

    await Assert.That(await _scalarAsync<string>(
        $"SELECT status_description FROM {SCHEMA}.wh_schema_migrations WHERE file_name LIKE '046%'"))
      .StartsWith("Updated from hash")
      .Because("046 ran again on the upgrade and recorded this release's hash");
  }

  /// <summary>
  /// A guard that asks whether its repair is still needed answers about the context's own table, so
  /// the repair is made there rather than skipped.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task AReRunRepairsTheContextsOwnTableAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);

    // 046 makes the inbox's commit-sequence columns NOT NULL once its backfill is done, guarded on the
    // column still being nullable. Put the column back in that state, as a database part way through
    // the upgrade would hold it, and have 046 run again.
    await _executeAsync($"ALTER TABLE {SCHEMA}.wh_inbox ALTER COLUMN source_commit_sequence DROP NOT NULL");
    await _executeAsync(
      $"UPDATE {SCHEMA}.wh_schema_migrations SET content_hash = 'an-earlier-release' WHERE file_name LIKE '046%'");

    await _initializeAsync(cancellationToken);

    await Assert.That(await _scalarAsync<string>($"""
        SELECT is_nullable FROM information_schema.columns
        WHERE table_schema = '{SCHEMA}' AND table_name = 'wh_inbox' AND column_name = 'source_commit_sequence'
        """)).IsEqualTo("NO")
      .Because("the guard must find the nullable column in the context's schema; asked about public, or "
             + "about a table named '\"schema\".wh_inbox', it finds nothing and the repair never happens");
  }

  /// <summary>
  /// A maintenance function reaps the context's own perspective rows when the connection that calls it
  /// resolves names through <c>public</c>.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task MaintenanceReapsTheContextsOwnExpiredRowsAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);

    await _executeAsync($"""
      CREATE TABLE {SCHEMA}.wh_per_named_ttl_probe (id UUID PRIMARY KEY, expires_at TIMESTAMPTZ);
      INSERT INTO {SCHEMA}.wh_per_named_ttl_probe VALUES (gen_random_uuid(), NOW() - INTERVAL '1 hour');
      INSERT INTO {SCHEMA}.wh_perspective_registry
        (clr_type_name, table_name, schema_json, schema_hash, service_name, row_ttl_seconds)
      VALUES ('Probe.NamedTtl', 'wh_per_named_ttl_probe', jsonb_build_object(), 'probe', 'probe', 60);
      """);

    await _executeAsync($"SELECT * FROM {SCHEMA}.perform_maintenance()");

    await Assert.That(await _scalarAsync<long>($"SELECT count(*) FROM {SCHEMA}.wh_per_named_ttl_probe")).IsEqualTo(0L)
      .Because("the reap must look for registered tables in the schema that owns the function, not in the "
             + "caller's current_schema(), which is public here");
  }

  /// <summary>
  /// The retention preview counts the context's own expired rows, not those of a table in <c>public</c>.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task RetentionBacklogCountsTheContextsOwnRowsAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);
    await _seedExpiredPerspectiveRowAsync("wh_per_named_backlog_probe", "Probe.NamedBacklog", acknowledged: false);

    await Assert.That(await _scalarAsync<long>($"SELECT {SCHEMA}.count_perspective_retention_backlog('Probe.NamedBacklog')"))
      .IsEqualTo(1L)
      .Because("the preview must look for the registered table in the schema that owns the function");
  }

  /// <summary>
  /// The reap-target collection offers the context's own expired rows.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task ReapTargetsAreCollectedFromTheContextsSchemaAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);
    await _seedExpiredPerspectiveRowAsync("wh_per_named_target_probe", "Probe.NamedTargets", acknowledged: true);

    await Assert.That(await _scalarAsync<long>(
        $"SELECT count(*) FROM {SCHEMA}.collect_perspective_row_reap_targets(ARRAY['Probe.NamedTargets'])"))
      .IsEqualTo(1L)
      .Because("the collection must look for the registered table in the schema that owns the function");
  }

  /// <summary>
  /// The table-rewrite scan measures the context's own tables.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task RewriteCandidatesAreMeasuredInTheContextsSchemaAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);

    // A threshold of zero offers every measured table, so the only question left is which schema is measured.
    await _executeAsync($"""
      INSERT INTO {SCHEMA}.wh_settings (setting_key, setting_value, value_type)
      VALUES ('table_rewrite_bloat_threshold', '0', 'decimal'), ('table_rewrite_min_rows', '1', 'integer')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value;
      CREATE TABLE {SCHEMA}.wh_rewrite_probe (id INTEGER PRIMARY KEY, note TEXT);
      INSERT INTO {SCHEMA}.wh_rewrite_probe SELECT g, 'probe' FROM generate_series(1, 100) g;
      ANALYZE {SCHEMA}.wh_rewrite_probe;
      """);

    await Assert.That(await _scalarAsync<long>(
        $"SELECT count(*) FROM {SCHEMA}.wh_tables_needing_rewrite() WHERE table_name = 'wh_rewrite_probe'"))
      .IsEqualTo(1L)
      .Because("the scan must measure the tables of the schema that owns the function, not public's");
  }

  /// <summary>
  /// A function that branches on a framework table's name, given as a value, still recognizes it: the
  /// name inside its literal is the value callers pass, not a reference to qualify.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task ADeadLetterMoveRecognizesItsSourceTableByNameAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);
    var messageId = Guid.CreateVersion7();
    await _executeAsync($"""
      INSERT INTO {SCHEMA}.wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number)
      VALUES ('{messageId}', 'topic', 'TestEvent', 'TestEnvelope', jsonb_build_object(), jsonb_build_object(), 1, 11, NOW(), gen_random_uuid(), 0)
      """);

    // The value every caller passes (DeadLetterSourceTable.OUTBOX).
    await _executeAsync(
      $"SELECT {SCHEMA}.move_to_dead_letters(gen_random_uuid(), 'wh_outbox', '{messageId}', 5, 'test', gen_random_uuid(), 'gen')");

    await Assert.That(await _scalarAsync<long>($"SELECT count(*) FROM {SCHEMA}.wh_outbox WHERE message_id = '{messageId}'"))
      .IsEqualTo(0L)
      .Because("the function compares p_source_table with 'wh_outbox'; rewritten to '\"named_svc\".wh_outbox' "
             + "inside the function, that comparison never holds and the row is never moved");
    await Assert.That(await _scalarAsync<long>(
        $"SELECT count(*) FROM {SCHEMA}.wh_dead_letters WHERE source_id = '{messageId}' AND source_table = 'wh_outbox'"))
      .IsEqualTo(1L);
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private async Task _initializeAsync(CancellationToken cancellationToken) {
    await using var context = new NamedSchemaDbContext(new DbContextOptionsBuilder<NamedSchemaDbContext>()
      .UseNpgsql(_connectionString)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
  }

  private async Task _initializePublicAsync(CancellationToken cancellationToken) {
    await using var context = new MinimalDbContext(new DbContextOptionsBuilder<MinimalDbContext>()
      .UseNpgsql(_connectionString)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
  }

  private async Task _seedExpiredPerspectiveRowAsync(string table, string clrTypeName, bool acknowledged) {
    await _executeAsync($"""
      CREATE TABLE {SCHEMA}.{table} (
        id UUID PRIMARY KEY, expires_at TIMESTAMPTZ, scope JSONB, data JSONB,
        created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW());
      INSERT INTO {SCHEMA}.{table} (id, expires_at) VALUES (gen_random_uuid(), NOW() - INTERVAL '1 hour');
      INSERT INTO {SCHEMA}.wh_perspective_registry
        (clr_type_name, table_name, schema_json, schema_hash, service_name, row_ttl_seconds,
         row_retention_enrolled, retention_enforcement_acknowledged)
      VALUES ('{clrTypeName}', '{table}', jsonb_build_object(), 'probe', 'probe', 60, TRUE, {(acknowledged ? "TRUE" : "FALSE")});
      """);
  }

  private async Task<int> _executeAsync(string sql) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    return await command.ExecuteNonQueryAsync();
  }

  private async Task<T?> _scalarAsync<T>(string sql) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    var result = await command.ExecuteScalarAsync();
    return result is null or DBNull ? default : (T)result;
  }
}
