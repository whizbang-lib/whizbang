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
/// Targeted coverage for <see cref="PostgresSchemaInitializer"/> branches the existing suites
/// (<see cref="PostgresSchemaInitializerTests"/>, <see cref="PostgresSchemaInitializerBranchTests"/>) do
/// not reach: the rollback swap's status-write failure rethrow, a failing core migration's rethrow, a
/// failing perspective migration's rethrow, both arms of the blue-green column-copy post-table-DDL
/// branch, and the DDL column parser's "no CREATE TABLE match" fallback reached through preview of a
/// perspective whose new DDL the stricter parser regex cannot match.
/// Uses SharedPostgresContainer with a per-test bare database (same pattern as
/// <see cref="PostgresSchemaInitializerBranchTests"/>) so each test controls exactly what schema exists
/// going in.
/// </summary>
public class PostgresSchemaInitializerCoverageTests : IAsyncDisposable {
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

        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName} WITH (FORCE)");
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

  /// <summary>Minimal provider so a test can force a specific set of migration scripts to run.</summary>
  private sealed class _customMigrationProvider(string version, MigrationScript[] migrations) : IMigrationProvider {
    public string Version => version;
    public string? ReleaseNotes => null;
    public IReadOnlyList<MigrationScript> GetMigrations() => migrations;
  }

  // --- Rollback swap status-write failure ---

  /// <summary>
  /// If this rethrow regresses, an operator who rolls back a bad perspective migration on a database
  /// whose tracking table is missing or corrupted sees the table renames silently half-applied with no
  /// exception — the one moment a trustworthy failure signal matters most.
  /// </summary>
  [Test]
  public async Task RollbackAsync_StatusUpdateFails_RollsBackSwapAndThrowsAsync() {
    await using var conn = new NpgsqlConnection(_testConnectionString);
    await conn.OpenAsync();
    // Bare database: wh_schema_migrations does not exist, so the status-restore UPDATE inside the swap
    // transaction fails after both renames already ran.
    await conn.ExecuteAsync("CREATE TABLE wh_per_covrbfail_bak_20240101000000 (id INT)");

    var initializer = new PostgresSchemaInitializer(_testConnectionString);

    await Assert.That(() => initializer.RollbackAsync("perspective:CovRbFailPerspective"))
      .Throws<PostgresException>()
      .Because("a failed status write inside the swap transaction must roll back and surface the failure, never swallow it");

    var backupStillExists = await conn.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'wh_per_covrbfail_bak_20240101000000')");
    await Assert.That(backupStillExists).IsTrue()
      .Because("the transaction rollback must undo both renames, leaving the backup table exactly as it was");
    var activeExists = await conn.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'wh_per_covrbfail')");
    await Assert.That(activeExists).IsFalse()
      .Because("a rolled-back swap must never leave the renamed-to-active table in place");
  }

  // --- Core migration failure rethrow ---

  /// <summary>
  /// If this rethrow regresses, a broken core migration is recorded as failed in wh_schema_migrations
  /// but InitializeSchemaAsync returns normally — every pod that starts up believes migrations succeeded
  /// and serves traffic against a schema that never finished migrating.
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_CoreMigrationSqlFails_RethrowsAfterRecordingFailureAsync() {
    var bootstrap = new PostgresMigrationProvider().GetMigrations()
      .First(m => m.Name.StartsWith("000", StringComparison.Ordinal));
    var provider = new _customMigrationProvider("9.9.20-coverage", [
      bootstrap,
      new MigrationScript("910_coverage_broken", "SELECT * FROM wh_coverage_table_does_not_exist;")
    ]);
    var initializer = new PostgresSchemaInitializer(_testConnectionString, perspectiveSchemaSql: null, migrationProvider: provider);

    await Assert.That(() => initializer.InitializeSchemaAsync())
      .Throws<PostgresException>()
      .Because("a migration that fails to execute must halt startup, not be silently absorbed");

    await using var connection = new NpgsqlConnection(_testConnectionString);
    await connection.OpenAsync();
    var record = await connection.QuerySingleAsync<dynamic>(
      "SELECT status, status_description FROM wh_schema_migrations WHERE file_name = '910_coverage_broken'");
    await Assert.That((int)record.status).IsEqualTo(-1)
      .Because("the failure record must be durably written before the exception propagates");
    await Assert.That((string)record.status_description).Contains("Failed:")
      .Because("the recorded description must carry enough detail for an operator to diagnose the failure");
  }

  // --- Perspective migration failure rethrow ---

  /// <summary>
  /// If this rethrow regresses, a perspective with invalid DDL is recorded as failed but
  /// InitializeSchemaAsync returns as though perspectives finished — a projector starts reading from a
  /// perspective table that was never created.
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_PerspectiveSqlFails_RethrowsAfterRecordingFailureAsync() {
    var entries = new[] {
      // Missing closing parenthesis — a syntax error at execution time.
      new KeyValuePair<string, string>("CoveragePerspectiveBroken", "CREATE TABLE wh_per_covbroken (id UUID PRIMARY KEY")
    };
    var initializer = new PostgresSchemaInitializer(_testConnectionString, entries);

    await Assert.That(() => initializer.InitializeSchemaAsync())
      .Throws<PostgresException>()
      .Because("invalid perspective DDL must halt initialization, not be swallowed");

    await using var connection = new NpgsqlConnection(_testConnectionString);
    await connection.OpenAsync();
    var record = await connection.QuerySingleAsync<dynamic>(
      "SELECT status, status_description FROM wh_schema_migrations WHERE file_name = 'perspective:CoveragePerspectiveBroken'");
    await Assert.That((int)record.status).IsEqualTo(-1)
      .Because("the failure record must persist against the specific perspective that failed to apply");
  }

  // --- Blue-green column-copy: post-table-DDL branch, both arms ---

  /// <summary>
  /// If the post-table DDL is skipped after a column-copy swap, an index the new perspective code relies
  /// on for query performance silently never gets created — the perspective still answers queries, just
  /// slower, with nothing in the logs to explain why.
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_ColumnCopyWithIndexDdl_CreatesIndexAfterSwapAsync() {
    var entries1 = new[] {
      new KeyValuePair<string, string>("CovColumnCopyIndexPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_covcolcopyidx (id UUID PRIMARY KEY);")
    };
    await new PostgresSchemaInitializer(_testConnectionString, entries1).InitializeSchemaAsync();

    var entries2 = new[] {
      new KeyValuePair<string, string>("CovColumnCopyIndexPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_covcolcopyidx (id UUID PRIMARY KEY, extra TEXT);\n" +
        "CREATE INDEX IF NOT EXISTS idx_wh_per_covcolcopyidx_extra ON wh_per_covcolcopyidx (extra);")
    };
    await new PostgresSchemaInitializer(_testConnectionString, entries2).InitializeSchemaAsync();

    await using var connection = new NpgsqlConnection(_testConnectionString);
    await connection.OpenAsync();
    var indexExists = await connection.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT FROM pg_indexes WHERE indexname = 'idx_wh_per_covcolcopyidx_extra')");
    await Assert.That(indexExists).IsTrue()
      .Because("post-table DDL must run against the final (post-swap) table name so its indexes actually exist");
    var columnExists = await connection.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT FROM information_schema.columns WHERE table_name = 'wh_per_covcolcopyidx' AND column_name = 'extra')");
    await Assert.That(columnExists).IsTrue()
      .Because("the additive column itself must also survive the swap");
  }

  /// <summary>
  /// A pure additive change with no trailing DDL must still complete the swap without error — the
  /// post-table-DDL branch has to tolerate "nothing to run" exactly as cleanly as "something to run".
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_ColumnCopyWithoutPostTableDdl_CompletesSwapAsync() {
    var entries1 = new[] {
      new KeyValuePair<string, string>("CovColumnCopyBarePerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_covcolcopybare (id UUID PRIMARY KEY);")
    };
    await new PostgresSchemaInitializer(_testConnectionString, entries1).InitializeSchemaAsync();

    var entries2 = new[] {
      new KeyValuePair<string, string>("CovColumnCopyBarePerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_covcolcopybare (id UUID PRIMARY KEY, extra TEXT);")
    };
    await new PostgresSchemaInitializer(_testConnectionString, entries2).InitializeSchemaAsync();

    await using var connection = new NpgsqlConnection(_testConnectionString);
    await connection.OpenAsync();
    var record = await connection.QuerySingleAsync<dynamic>(
      "SELECT status, status_description FROM wh_schema_migrations WHERE file_name = 'perspective:CovColumnCopyBarePerspective'");
    await Assert.That((int)record.status).IsEqualTo(2)
      .Because("the swap is an update to an already-tracked perspective");
    await Assert.That((string)record.status_description).Contains("ColumnCopy")
      .Because("the recorded strategy must reflect column-copy even when there is no post-table DDL to run");
    var columnExists = await connection.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT FROM information_schema.columns WHERE table_name = 'wh_per_covcolcopybare' AND column_name = 'extra')");
    await Assert.That(columnExists).IsTrue()
      .Because("the additive column must exist on the final table once the swap completes");
  }

  // --- Column parser "no CREATE TABLE match" fallback ---

  /// <summary>
  /// If the parser's "no match" fallback stopped returning an empty column set, an unparseable new-side
  /// DDL would silently keep whatever partial columns it found instead of falling into the safe
  /// destructive-change (event-replay) path — exactly the case additive-vs-destructive detection exists
  /// to get right, since guessing wrong here risks losing data on a live perspective table.
  /// </summary>
  [Test]
  public async Task PreviewAsync_ChangedPerspectiveDdlMissingSemicolon_TreatsAsEventReplayAsync() {
    var entries1 = new[] {
      new KeyValuePair<string, string>("CovParseFailPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_covparsefail (id UUID PRIMARY KEY);")
    };
    await new PostgresSchemaInitializer(_testConnectionString, entries1).InitializeSchemaAsync();

    // No trailing semicolon: _extractTableName still finds the table name (it only needs "(" after the
    // name), but the stricter ");"-terminated column-parser regex cannot match, so it must fall back to
    // an empty column set rather than misparse or throw.
    var entries2 = new[] {
      new KeyValuePair<string, string>("CovParseFailPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_covparsefail (id UUID PRIMARY KEY, extra TEXT)")
    };
    var plan = await new PostgresSchemaInitializer(_testConnectionString, entries2).PreviewAsync();

    var step = plan.Steps.Single(s => s.Name == "perspective:CovParseFailPerspective");
    await Assert.That(step.Action).IsEqualTo(MigrationAction.BlueGreenEventReplay)
      .Because("an unparseable new-side column set must be treated as removing every existing column — the safe (destructive) strategy — never a silent direct-DDL no-op");
    await Assert.That(step.RemovedColumns).IsNotNull();
    await Assert.That(step.RemovedColumns!).Contains("id")
      .Because("every old column must show as removed when the new DDL could not be parsed at all");
    await Assert.That(step.AddedColumns).IsNull()
      .Because("nothing was actually parsed out of the new DDL, so nothing can be reported as added");
  }

  // --- Lines confirmed unreachable through any live call path (see test-class remarks / report) ---
  //
  // PostgresSchemaInitializer.cs:136  (RollbackAsync bakIdx<0 "return false")
  // PostgresSchemaInitializer.cs:315  (CleanupBackupsAsync bakIdx<0 "continue")
  // PostgresSchemaInitializer.cs:746  (_splitDdl "no CREATE TABLE match" fallback)
  //
  // Both backup-table queries filter with `table_name LIKE '%\_bak\_%' ESCAPE '\'`, which guarantees any
  // row returned already contains the literal substring "_bak_" — so the C# LastIndexOf("_bak_") guard
  // that follows can never see -1. And _splitDdl is reached only from the ColumnCopy branch of
  // _executeSinglePerspectiveMigrationAsync, which is only selected when _parseColumnsFromDdl(entry.Value)
  // already matched its "CREATE TABLE ... );" regex on that exact same string — the regex _splitDdl itself
  // uses (byte-for-byte the same match structure, differing only in which group is captured). A DDL that
  // fails one necessarily fails the other, so ColumnCopy is never selected for a DDL _splitDdl cannot
  // parse. All three are defensive dead code under the current call graph; no test in this file forces
  // them, per the instruction to report rather than fabricate an unreachable path.
}
