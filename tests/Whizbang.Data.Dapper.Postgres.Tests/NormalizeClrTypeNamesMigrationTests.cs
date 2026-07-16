using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Integration tests for migration 063_NormalizeClrTypeNamesV2 and the wh_settings entries it and
/// migration 032 seed. Verifies the full migration chain applies cleanly and that the CLR type-name
/// normalization is DATA-driven, reliable ('.'-nested -> '+'-nested via the event_type oracle), and
/// gated on the wh_settings 'clr_type_name_format_version' marker so it never re-scans once at v2.
/// </summary>
[Category("Integration")]
public class NormalizeClrTypeNamesMigrationTests : IAsyncDisposable {
  private string? _testDatabaseName;
  private string? _connectionString;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _testDatabaseName = $"test_{Guid.NewGuid():N}";
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await admin.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    }.ConnectionString;

    // Applies the full migration chain (000..063) including edited 032 and new 063.
    await new PostgresSchemaInitializer(_connectionString).InitializeSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_testDatabaseName is null) {
      return;
    }
    try {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await admin.ExecuteAsync($@"SELECT pg_terminate_backend(pid) FROM pg_stat_activity
        WHERE datname = '{_testDatabaseName}' AND pid <> pg_backend_pid()");
      await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
    } catch { /* ignore */ }
    _testDatabaseName = null;
    _connectionString = null;
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  [Test]
  public async Task FreshInit_SeedsFormatVersionAndAbandonedStreamHoursSettingsAsync() {
    // A clean migration chain leaves the data on format v3 (063 sets it after a no-op normalization)
    // and seeds the new abandoned_stream_hours knob (032).
    await Assert.That(await _settingAsync("clr_type_name_format_version")).IsEqualTo("3");
    await Assert.That(await _settingAsync("abandoned_stream_hours")).IsEqualTo("1");
  }

  [Test]
  public async Task Migration063_NormalizesDottedNestedNamesToPlus_AndIsVersionGatedAsync() {
    const string plusForm = "Acme.Contracts.OrderContracts+CreatedEvent";
    const string dottedForm = "Acme.Contracts.OrderContracts.CreatedEvent";
    var pinnedId = Guid.NewGuid();

    await using (var conn = new NpgsqlConnection(_connectionString)) {
      await conn.OpenAsync();
      // Reset to the legacy version so the (already-applied) migration will act again.
      await conn.ExecuteAsync(
        "UPDATE wh_settings SET setting_value = '1' WHERE setting_key = 'clr_type_name_format_version'");
      // Oracle row: event_type already holds the correct '+' full name; aggregate_type written the
      // legacy Dapper way (bare simple name) to also exercise the aggregate_type fix.
      await conn.ExecuteAsync(
        @"INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
          VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'CreatedEvent', @Plus || ', Acme.Contracts', 1, NOW())",
        new { Plus = plusForm });
      // Stale registry row written by the old catalog generator ('.'-nested).
      await conn.ExecuteAsync(
        @"INSERT INTO wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at)
          VALUES (@Dotted, @PinnedId::uuid, 'event', NOW())",
        new { Dotted = dottedForm, PinnedId = pinnedId });
    }

    var sql063 = new PostgresMigrationProvider().GetMigration("063_NormalizeClrTypeNamesV2")!.Sql;

    // Act — run the migration body.
    await _execAsync(sql063);

    // Registry '.' -> '+', aggregate_type bare -> full '+', and the version marker advances to 3.
    await Assert.That(await _scalarAsync(
      "SELECT clr_type_name FROM wh_message_type_registry WHERE pinned_id = @Id::uuid", new { Id = pinnedId }))
      .IsEqualTo(plusForm);
    await Assert.That(await _scalarAsync(
      "SELECT aggregate_type FROM wh_event_store WHERE event_type LIKE @Like", new { Like = plusForm + ",%" }))
      .IsEqualTo(plusForm);
    await Assert.That(await _settingAsync("clr_type_name_format_version")).IsEqualTo("3");

    // Idempotent: at v3 the gate short-circuits — a second run neither errors nor un-does the fix.
    await _execAsync(sql063);
    await Assert.That(await _scalarAsync(
      "SELECT clr_type_name FROM wh_message_type_registry WHERE pinned_id = @Id::uuid", new { Id = pinnedId }))
      .IsEqualTo(plusForm);
    await Assert.That(await _settingAsync("clr_type_name_format_version")).IsEqualTo("3");
  }

  [Test]
  public async Task Migration063_NormalizesPerspectiveTypeRegistryEntries_ViaAssociationTargetNameAsync() {
    // Regression for the production gap: wh_message_type_registry is dominated by PERSPECTIVE types
    // (e.g. Domain.X+Projection), whose '+'-name never appears in an event/message column — only as
    // wh_message_associations.target_name. v2's oracle missed them, so they stayed '.'-nested and
    // reconcile logged a drift warning for each on every startup. v3 adds target_name as an oracle.
    const string plusPersp = "Acme.Job.Domain.BulkOperation+Projection";
    const string dottedPersp = "Acme.Job.Domain.BulkOperation.Projection";
    var pinnedId = Guid.NewGuid();

    await using (var conn = new NpgsqlConnection(_connectionString)) {
      await conn.OpenAsync();
      await conn.ExecuteAsync(
        "UPDATE wh_settings SET setting_value = '2' WHERE setting_key = 'clr_type_name_format_version'");
      // The '+'-form of the perspective type exists ONLY as an association target (BuildClrTypeName form).
      await conn.ExecuteAsync(
        @"INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name)
          VALUES ('Acme.Job.Contracts.JobContracts+CreatedEvent, Acme.Job.Contracts', 'perspective', @Plus, 'JobService')",
        new { Plus = plusPersp });
      // Stale '.'-nested perspective registry row (pinned — reconcile can't self-heal it).
      await conn.ExecuteAsync(
        @"INSERT INTO wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at)
          VALUES (@Dotted, @PinnedId::uuid, 'perspective', NOW())",
        new { Dotted = dottedPersp, PinnedId = pinnedId });
    }

    var sql063 = new PostgresMigrationProvider().GetMigration("063_NormalizeClrTypeNamesV2")!.Sql;
    await _execAsync(sql063);

    // The perspective registry row is normalized to '+' via the target_name oracle; version -> 3.
    await Assert.That(await _scalarAsync(
      "SELECT clr_type_name FROM wh_message_type_registry WHERE pinned_id = @Id::uuid", new { Id = pinnedId }))
      .IsEqualTo(plusPersp);
    await Assert.That(await _settingAsync("clr_type_name_format_version")).IsEqualTo("3");
  }

  [Test]
  public async Task Migration063_WhenBothDottedAndPlusRowsExist_DedupsInsteadOfCollidingAsync() {
    // Regression for the production DEPLOY FAILURE: a type can already have BOTH a stale '.'-row and the
    // canonical '+'-row (on the prior deploy reconcile inserted the '+' form while the pre-existing
    // '.' row lingered). A plain UPDATE '.'->'+' then violates the clr_type_name PRIMARY KEY
    // ('duplicate key value violates unique constraint') — the migration aborts and service startup
    // fails. The migration must drop the stale '.' duplicate instead of renaming into a collision.
    const string plus = "Acme.Job.Domain.JobArch+Projection";
    const string dotted = "Acme.Job.Domain.JobArch.Projection";

    await using (var conn = new NpgsqlConnection(_connectionString)) {
      await conn.OpenAsync();
      await conn.ExecuteAsync(
        "UPDATE wh_settings SET setting_value = '2' WHERE setting_key = 'clr_type_name_format_version'");
      await conn.ExecuteAsync(
        @"INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name)
          VALUES ('Acme.Job.Contracts.JobContracts+CreatedEvent, Acme.Job.Contracts', 'perspective', @Plus, 'JobService')",
        new { Plus = plus });
      // BOTH forms already present, both pinned — the exact production collision.
      await conn.ExecuteAsync(
        @"INSERT INTO wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at)
          VALUES (@Plus, gen_random_uuid(), 'perspective', NOW()),
                 (@Dotted, gen_random_uuid(), 'perspective', NOW())",
        new { Plus = plus, Dotted = dotted });
    }

    var sql063 = new PostgresMigrationProvider().GetMigration("063_NormalizeClrTypeNamesV2")!.Sql;

    // Must NOT throw a duplicate-key violation.
    await _execAsync(sql063);

    // Stale '.' row dropped; canonical '+' row kept exactly once; version -> 3.
    await Assert.That(await _scalarAsync(
      "SELECT count(*)::text FROM wh_message_type_registry WHERE clr_type_name = @D", new { D = dotted })).IsEqualTo("0");
    await Assert.That(await _scalarAsync(
      "SELECT count(*)::text FROM wh_message_type_registry WHERE clr_type_name = @P", new { P = plus })).IsEqualTo("1");
    await Assert.That(await _settingAsync("clr_type_name_format_version")).IsEqualTo("3");
  }

  [Test]
  public async Task Migration063_UnderNonPublicSchema_ReferencesWhSettingsUnqualifiedAsync() {
    // Regression: wh_settings is created UNqualified in migration 028, so it lives in the
    // search_path schema (public), NOT __SCHEMA__. A qualified __SCHEMA__.wh_settings reference
    // throws 42P01 ("relation \"inventory.wh_settings\" does not exist") whenever __SCHEMA__ is a
    // non-public schema — exactly what the ECommerce sample (schema 'inventory') hit in CI. This
    // reproduces that: the qualified core tables live in 'inventory', wh_settings stays in public.
    const string schema = "inv_test";
    await _execAsync($@"
      CREATE SCHEMA IF NOT EXISTS {schema};
      CREATE TABLE {schema}.wh_event_store (event_id uuid PRIMARY KEY DEFAULT gen_random_uuid(), stream_id uuid, aggregate_id uuid, aggregate_type text, event_type text, event_data jsonb DEFAULT '{{}}', metadata jsonb DEFAULT '{{}}', version int DEFAULT 1, created_at timestamptz DEFAULT now());
      CREATE TABLE {schema}.wh_message_type_registry (clr_type_name text PRIMARY KEY, pinned_id uuid, kind text, updated_at timestamptz DEFAULT now());
      CREATE TABLE {schema}.wh_outbox (message_id uuid DEFAULT gen_random_uuid(), message_type text, envelope_type text);
      CREATE TABLE {schema}.wh_inbox (message_id uuid DEFAULT gen_random_uuid(), message_type text);
      CREATE TABLE {schema}.wh_message_associations (message_type text, association_type text, target_name text, service_name text);");

    // Reset the (public) version marker so the migration acts.
    await _execAsync("UPDATE wh_settings SET setting_value = '1' WHERE setting_key = 'clr_type_name_format_version'");

    // Run migration 063 with __SCHEMA__ resolved to the non-public schema.
    var sql063 = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, schema)
      .GetMigration("063_NormalizeClrTypeNamesV2")!.Sql;

    // Must NOT throw 42P01 — and must still write the marker to the unqualified (public) wh_settings.
    await _execAsync(sql063);
    await Assert.That(await _settingAsync("clr_type_name_format_version")).IsEqualTo("3");
  }

  // ── helpers ──

  private async Task<string?> _settingAsync(string key) =>
    await _scalarAsync("SELECT setting_value FROM wh_settings WHERE setting_key = @Key", new { Key = key });

  private async Task<string?> _scalarAsync(string sql, object param) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    return await conn.ExecuteScalarAsync<string?>(sql, param);
  }

  private async Task _execAsync(string sql) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(sql);
  }
}
