using Dapper;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Branch tests for <see cref="PostgresSchemaInitializer"/> covering the arms
/// <see cref="PostgresSchemaInitializerTests"/> does not reach: preview of tampered/changed/dropped
/// migrations, the preview bootstrap-failure swallow, core and perspective failure recording (status -1),
/// the EventReplay (status 4) execution path, the rollback swap-transaction failure path, unparseable
/// backup date suffixes, and custom-migration-provider shapes (empty, no bootstrap, null).
/// Uses SharedPostgresContainer with per-test database isolation (same pattern as the sibling file).
/// </summary>
public class PostgresSchemaInitializerBranchTests : IAsyncDisposable {
  private string? _testDatabaseName;
  private string? _connectionString;
  private string _testConnectionString => _connectionString ?? throw new InvalidOperationException("Test not initialized");

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _testDatabaseName = $"test_{Guid.NewGuid():N}";

    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName
    };
    _connectionString = builder.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_testDatabaseName != null) {
      try {
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();

        await adminConnection.ExecuteAsync($@"
          SELECT pg_terminate_backend(pg_stat_activity.pid)
          FROM pg_stat_activity
          WHERE pg_stat_activity.datname = '{_testDatabaseName}'
          AND pid <> pg_backend_pid()");

        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
      } catch {
        // Ignore cleanup errors
      }

      _testDatabaseName = null;
      _connectionString = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  /// <summary>Minimal provider so tests can control exactly which migration scripts run.</summary>
  private sealed class _customMigrationProvider(string version, MigrationScript[] migrations) : IMigrationProvider {
    public string Version => version;
    public string? ReleaseNotes => null;
    public IReadOnlyList<MigrationScript> GetMigrations() => migrations;
  }

  private static MigrationScript _realBootstrap() =>
    new PostgresMigrationProvider().GetMigrations()
      .First(m => m.Name.StartsWith("000", StringComparison.Ordinal));

  // --- Constructor guards ---

  /// <summary>
  /// The three-arg constructor must reject a null migration provider.
  /// </summary>
  [Test]
  public async Task Constructor_NullMigrationProvider_ThrowsAsync() {
    await Assert.That(() => new PostgresSchemaInitializer(
        _testConnectionString, perspectiveSchemaSql: null, migrationProvider: null!))
      .Throws<ArgumentNullException>();
  }

  // --- Core migration failure + update paths ---

  /// <summary>
  /// A failing core migration must be recorded with status -1 ("Failed: ...") and the failure re-thrown
  /// to halt the migration run.
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_CoreMigrationFails_RecordsFailureStatusAndThrowsAsync() {
    var provider = new _customMigrationProvider("9.9.1-test", [
      _realBootstrap(),
      new MigrationScript("999_intentionally_broken", "SELECT * FROM wh_this_table_does_not_exist;")
    ]);
    var initializer = new PostgresSchemaInitializer(
      _testConnectionString, perspectiveSchemaSql: null, migrationProvider: provider);

    await Assert.That(() => initializer.InitializeSchemaAsync()).Throws<PostgresException>();

    await using var connection = new NpgsqlConnection(_testConnectionString);
    await connection.OpenAsync();
    var record = await connection.QuerySingleAsync<dynamic>(
      "SELECT status, status_description FROM wh_schema_migrations WHERE file_name = '999_intentionally_broken'");
    await Assert.That((int)record.status).IsEqualTo(-1);
    await Assert.That((string)record.status_description).Contains("Failed:");
  }

  /// <summary>
  /// A core migration whose recorded hash no longer matches must show as Update in the preview and
  /// re-execute as an update (status 2, "Updated from hash ...") on the next initialization.
  /// </summary>
  [Test]
  public async Task PreviewAndInitialize_AfterCoreHashTamper_ShowUpdateAndReapplyAsync() {
    var initializer = new PostgresSchemaInitializer(_testConnectionString);
    await initializer.InitializeSchemaAsync();

    // Corrupt the recorded hash of a real non-bootstrap migration so it re-runs as an update.
    var target = new PostgresMigrationProvider().GetMigrations()
      .First(m => !m.Name.StartsWith("000", StringComparison.Ordinal));
    await using var connection = new NpgsqlConnection(_testConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(
      "UPDATE wh_schema_migrations SET content_hash = 'tampered' WHERE file_name = @name",
      new { name = target.Name });

    var plan = await initializer.PreviewAsync();
    var step = plan.Steps.Single(s => s.Name == target.Name);
    await Assert.That(step.Action).IsEqualTo(MigrationAction.Update);
    await Assert.That(step.OldHash).IsEqualTo("tampered");

    await initializer.InitializeSchemaAsync();
    var record = await connection.QuerySingleAsync<dynamic>(
      "SELECT status, status_description FROM wh_schema_migrations WHERE file_name = @name",
      new { name = target.Name });
    await Assert.That((int)record.status).IsEqualTo(2);
    await Assert.That((string)record.status_description).Contains("Updated from hash");
  }

  // --- Preview branches ---

  /// <summary>
  /// A bootstrap failure during preview is expected (tables may not exist yet) and must be swallowed;
  /// the plan is still produced. With no perspective entries the plan contains no perspective steps.
  /// </summary>
  [Test]
  public async Task PreviewAsync_BootstrapFailureIsSwallowed_AndPlanOmitsPerspectivesAsync() {
    // Tracking tables must exist so the rest of the preview can query recorded hashes.
    await new PostgresSchemaInitializer(_testConnectionString).InitializeSchemaAsync();

    var provider = new _customMigrationProvider("9.9.2-test", [
      new MigrationScript("000_broken_bootstrap", "SELECT * FROM wh_no_such_bootstrap_table;"),
      new MigrationScript("100_branch_probe", "SELECT 1;")
    ]);
    var initializer = new PostgresSchemaInitializer(
      _testConnectionString, perspectiveSchemaSql: null, migrationProvider: provider);

    var plan = await initializer.PreviewAsync();

    var step = plan.Steps.Single(s => s.Name == "100_branch_probe");
    await Assert.That(step.Action).IsEqualTo(MigrationAction.Apply);
    await Assert.That(plan.Steps.Any(s => s.Name.StartsWith("perspective:", StringComparison.Ordinal))).IsFalse();
  }

  /// <summary>
  /// A changed perspective entry with no CREATE TABLE statement has no table to diff — the preview must
  /// fall back to a plain Update (no column diff), and execution must record status 2 with DirectDdl.
  /// </summary>
  [Test]
  public async Task PreviewAndInitialize_ChangedPerspectiveWithoutCreateTable_UsesPlainUpdateAsync() {
    var entries1 = new[] { new KeyValuePair<string, string>("NoTableEntry", "SELECT 1;") };
    await new PostgresSchemaInitializer(_testConnectionString, entries1).InitializeSchemaAsync();

    var entries2 = new[] { new KeyValuePair<string, string>("NoTableEntry", "SELECT 2;") };
    var initializer = new PostgresSchemaInitializer(_testConnectionString, entries2);

    var plan = await initializer.PreviewAsync();
    var step = plan.Steps.Single(s => s.Name == "perspective:NoTableEntry");
    await Assert.That(step.Action).IsEqualTo(MigrationAction.Update);
    await Assert.That(step.AddedColumns).IsNull();
    await Assert.That(step.RemovedColumns).IsNull();

    await initializer.InitializeSchemaAsync();
    await using var connection = new NpgsqlConnection(_testConnectionString);
    await connection.OpenAsync();
    var record = await connection.QuerySingleAsync<dynamic>(
      "SELECT status, status_description FROM wh_schema_migrations WHERE file_name = 'perspective:NoTableEntry'");
    await Assert.That((int)record.status).IsEqualTo(2);
    await Assert.That((string)record.status_description).Contains("DirectDdl");
  }

  /// <summary>
  /// Removing a column is destructive: the preview must plan BlueGreenEventReplay with the removed
  /// column listed, and execution must record status 4 (MigratingInBackground) instead of swapping.
  /// The DDL also uses a table-level CONSTRAINT so the parser's constraint-skip arm is exercised.
  /// </summary>
  [Test]
  public async Task PreviewAndInitialize_ColumnRemoval_UsesEventReplayBlueGreenAsync() {
    var entries1 = new[] {
      new KeyValuePair<string, string>("DestructivePerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_destructive (id UUID, extra TEXT, CONSTRAINT pk_wh_per_destructive PRIMARY KEY (id));")
    };
    await new PostgresSchemaInitializer(_testConnectionString, entries1).InitializeSchemaAsync();

    var entries2 = new[] {
      new KeyValuePair<string, string>("DestructivePerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_destructive (id UUID, CONSTRAINT pk_wh_per_destructive PRIMARY KEY (id));")
    };
    var initializer = new PostgresSchemaInitializer(_testConnectionString, entries2);

    var plan = await initializer.PreviewAsync();
    var step = plan.Steps.Single(s => s.Name == "perspective:DestructivePerspective");
    await Assert.That(step.Action).IsEqualTo(MigrationAction.BlueGreenEventReplay);
    await Assert.That(step.RemovedColumns).IsNotNull();
    await Assert.That(step.RemovedColumns!).Contains("extra");
    await Assert.That(step.AddedColumns).IsNull();

    await initializer.InitializeSchemaAsync();
    await using var connection = new NpgsqlConnection(_testConnectionString);
    await connection.OpenAsync();
    var record = await connection.QuerySingleAsync<dynamic>(
      "SELECT status, status_description FROM wh_schema_migrations WHERE file_name = 'perspective:DestructivePerspective'");
    await Assert.That((int)record.status).IsEqualTo(4);
    await Assert.That((string)record.status_description).Contains("MigratingInBackground");
  }

  /// <summary>
  /// Changing a column's type (TEXT → JSONB) is the other destructive-change arm of strategy detection —
  /// the preview must plan BlueGreenEventReplay.
  /// </summary>
  [Test]
  public async Task PreviewAsync_ColumnTypeChange_ShowsEventReplayAsync() {
    var entries1 = new[] {
      new KeyValuePair<string, string>("TypeChangePerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_typechange (id UUID PRIMARY KEY, amount TEXT);")
    };
    await new PostgresSchemaInitializer(_testConnectionString, entries1).InitializeSchemaAsync();

    var entries2 = new[] {
      new KeyValuePair<string, string>("TypeChangePerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_typechange (id UUID PRIMARY KEY, amount JSONB);")
    };
    var plan = await new PostgresSchemaInitializer(_testConnectionString, entries2).PreviewAsync();

    var step = plan.Steps.Single(s => s.Name == "perspective:TypeChangePerspective");
    await Assert.That(step.Action).IsEqualTo(MigrationAction.BlueGreenEventReplay);
  }

  /// <summary>
  /// When a hash is recorded but the table was dropped out-of-band, strategy detection sees a new table
  /// (no existing columns) — the preview falls back to Update and reports every column as added.
  /// </summary>
  [Test]
  public async Task PreviewAsync_HashRecordedButTableDropped_ShowsUpdateWithColumnsAddedAsync() {
    var entries1 = new[] {
      new KeyValuePair<string, string>("DroppedPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_droppedtbl (id UUID PRIMARY KEY);")
    };
    await new PostgresSchemaInitializer(_testConnectionString, entries1).InitializeSchemaAsync();

    await using var conn = new NpgsqlConnection(_testConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync("DROP TABLE wh_per_droppedtbl");

    var entries2 = new[] {
      new KeyValuePair<string, string>("DroppedPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_droppedtbl (id UUID PRIMARY KEY, extra TEXT);")
    };
    var plan = await new PostgresSchemaInitializer(_testConnectionString, entries2).PreviewAsync();

    var step = plan.Steps.Single(s => s.Name == "perspective:DroppedPerspective");
    await Assert.That(step.Action).IsEqualTo(MigrationAction.Update);
    await Assert.That(step.AddedColumns).IsNotNull();
    await Assert.That(step.AddedColumns!).Contains("extra");
  }

  // --- Perspective failure path ---

  /// <summary>
  /// Invalid perspective DDL must roll back its transaction, record status -1 ("Failed: ..."), and
  /// re-throw to halt initialization.
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_PerspectiveDdlInvalid_RecordsFailureAndThrowsAsync() {
    var entries = new[] {
      // Missing closing parenthesis — a syntax error at execution time.
      new KeyValuePair<string, string>("BrokenPerspective", "CREATE TABLE wh_per_broken (id UUID PRIMARY KEY")
    };
    var initializer = new PostgresSchemaInitializer(_testConnectionString, entries);

    await Assert.That(() => initializer.InitializeSchemaAsync()).Throws<PostgresException>();

    await using var connection = new NpgsqlConnection(_testConnectionString);
    await connection.OpenAsync();
    var record = await connection.QuerySingleAsync<dynamic>(
      "SELECT status, status_description FROM wh_schema_migrations WHERE file_name = 'perspective:BrokenPerspective'");
    await Assert.That((int)record.status).IsEqualTo(-1);
    await Assert.That((string)record.status_description).Contains("Failed:");
  }

  // --- Rollback failure path ---

  /// <summary>
  /// If the status restore inside the rollback swap transaction fails (wh_schema_migrations missing on a
  /// bare database), the renames must be rolled back and the failure re-thrown.
  /// </summary>
  [Test]
  public async Task RollbackAsync_MigrationsTableMissing_RollsBackRenameAndThrowsAsync() {
    await using var conn = new NpgsqlConnection(_testConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync("CREATE TABLE wh_per_orphan_bak_20240101000000 (id INT)");

    var initializer = new PostgresSchemaInitializer(_testConnectionString);

    await Assert.That(() => initializer.RollbackAsync("perspective:OrphanPerspective"))
      .Throws<PostgresException>();

    // The transaction rolled back: the backup is still the backup and no active table appeared.
    var backupStillExists = await conn.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'wh_per_orphan_bak_20240101000000')");
    await Assert.That(backupStillExists).IsTrue();
    var activeExists = await conn.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'wh_per_orphan')");
    await Assert.That(activeExists).IsFalse();
  }

  // --- Cleanup branch ---

  /// <summary>
  /// A backup-like table whose date suffix does not parse must be kept, never dropped.
  /// </summary>
  [Test]
  public async Task CleanupBackupsAsync_UnparseableDateSuffix_KeepsTableAsync() {
    await using var conn = new NpgsqlConnection(_testConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync("CREATE TABLE wh_per_odd_bak_notadate (id INT)");

    var initializer = new PostgresSchemaInitializer(_testConnectionString);
    var dropped = await initializer.CleanupBackupsAsync(olderThanDays: 0);

    await Assert.That(dropped).Count().IsEqualTo(0);
    var stillExists = await conn.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'wh_per_odd_bak_notadate')");
    await Assert.That(stillExists).IsTrue();
  }

  // --- Custom provider shapes ---

  /// <summary>
  /// An empty migration provider skips the whole migration phase (infrastructure schema still runs) and
  /// previews to an empty plan (no bootstrap, no core steps, no perspectives).
  /// </summary>
  [Test]
  public async Task InitializeAndPreview_EmptyMigrationProvider_SkipMigrationPhaseAsync() {
    var provider = new _customMigrationProvider("9.9.3-test", []);
    var initializer = new PostgresSchemaInitializer(
      _testConnectionString, perspectiveSchemaSql: null, migrationProvider: provider);

    await initializer.InitializeSchemaAsync();

    await using var connection = new NpgsqlConnection(_testConnectionString);
    await connection.OpenAsync();
    var infraExists = await connection.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'wh_event_store')");
    await Assert.That(infraExists).IsTrue();

    var plan = await initializer.PreviewAsync();
    await Assert.That(plan.Steps).Count().IsEqualTo(0);
  }

  /// <summary>
  /// A provider with migrations but no 000 bootstrap skips the bootstrap step and still applies the
  /// remaining migrations with hash tracking (status 1, "First apply").
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_ProviderWithoutBootstrap_AppliesMigrationAsync() {
    // Tracking tables come from a prior real initialization; the custom provider then has no 000 script.
    await new PostgresSchemaInitializer(_testConnectionString).InitializeSchemaAsync();

    var provider = new _customMigrationProvider("9.9.4-test", [
      new MigrationScript("100_branch_probe", "SELECT 1;")
    ]);
    var initializer = new PostgresSchemaInitializer(
      _testConnectionString, perspectiveSchemaSql: null, migrationProvider: provider);
    await initializer.InitializeSchemaAsync();

    await using var connection = new NpgsqlConnection(_testConnectionString);
    await connection.OpenAsync();
    var record = await connection.QuerySingleAsync<dynamic>(
      "SELECT status, status_description FROM wh_schema_migrations WHERE file_name = '100_branch_probe'");
    await Assert.That((int)record.status).IsEqualTo(1);
    await Assert.That((string)record.status_description).IsEqualTo("First apply");
  }
}
