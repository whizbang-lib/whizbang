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
    // A clean migration chain leaves the data on format v2 (063 sets it after a no-op normalization)
    // and seeds the new abandoned_stream_hours knob (032).
    await Assert.That(await _settingAsync("clr_type_name_format_version")).IsEqualTo("2");
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
        @"INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
          VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'CreatedEvent', @Plus || ', Acme.Contracts', '{}'::jsonb, '{}'::jsonb, 1, NOW())",
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

    // Registry '.' -> '+', aggregate_type bare -> full '+', and the version marker advances to 2.
    await Assert.That(await _scalarAsync(
      "SELECT clr_type_name FROM wh_message_type_registry WHERE pinned_id = @Id::uuid", new { Id = pinnedId }))
      .IsEqualTo(plusForm);
    await Assert.That(await _scalarAsync(
      "SELECT aggregate_type FROM wh_event_store WHERE event_type LIKE @Like", new { Like = plusForm + ",%" }))
      .IsEqualTo(plusForm);
    await Assert.That(await _settingAsync("clr_type_name_format_version")).IsEqualTo("2");

    // Idempotent: at v2 the gate short-circuits — a second run neither errors nor un-does the fix.
    await _execAsync(sql063);
    await Assert.That(await _scalarAsync(
      "SELECT clr_type_name FROM wh_message_type_registry WHERE pinned_id = @Id::uuid", new { Id = pinnedId }))
      .IsEqualTo(plusForm);
    await Assert.That(await _settingAsync("clr_type_name_format_version")).IsEqualTo("2");
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
