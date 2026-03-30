using Npgsql;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End-to-end integration tests for schema initialization workflow.
/// Tests idempotency, error handling, and complete initialization process.
/// </summary>
public class SchemaInitializationTests : EFCoreTestBase {
  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_CreatesCoreInfrastructureTablesAsync() {
    // Arrange
    await DropAllWhizbangTablesAsync();

    // Act
    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync();

    // Assert - Query for all Whizbang tables
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    const string sql = @"
      SELECT table_name
      FROM information_schema.tables
      WHERE table_schema = 'public'
        AND table_name LIKE 'wh_%'
      ORDER BY table_name";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var tables = new List<string>();
    while (await reader.ReadAsync()) {
      tables.Add(reader.GetString(0));
    }

    // Assert - All 9 core infrastructure tables exist
    await Assert.That(tables).Contains("wh_service_instances");
    await Assert.That(tables).Contains("wh_message_deduplication");
    await Assert.That(tables).Contains("wh_inbox");
    await Assert.That(tables).Contains("wh_outbox");
    await Assert.That(tables).Contains("wh_event_store");
    await Assert.That(tables).Contains("wh_receptor_processing");
    await Assert.That(tables).Contains("wh_perspective_cursors");
    await Assert.That(tables).Contains("wh_request_response");
    await Assert.That(tables).Contains("wh_sequences");
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_IsIdempotentAsync() {
    // Arrange - ensure clean state
    await DropAllWhizbangTablesAsync();

    // Act - Call initialization multiple times
    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync();

    // Assert - Should not throw exceptions
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    const string sql = @"
      SELECT COUNT(*)
      FROM information_schema.tables
      WHERE table_schema = 'public'
        AND table_name LIKE 'wh_%'";

    await using var command = new NpgsqlCommand(sql, connection);
    var count = (long)(await command.ExecuteScalarAsync() ?? 0L);

    // 9 core tables + 1 perspective table (wh_per_order)
    await Assert.That(count).IsGreaterThanOrEqualTo(10);
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_CreatesPerspectiveTablesAsync() {
    // Arrange
    await DropAllWhizbangTablesAsync();

    // Act
    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync();

    // Assert - Perspective table exists
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    const string sql = @"
      SELECT table_name
      FROM information_schema.tables
      WHERE table_schema = 'public'
        AND table_name = 'wh_per_order'";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var perspectiveTables = new List<string>();
    while (await reader.ReadAsync()) {
      perspectiveTables.Add(reader.GetString(0));
    }

    await Assert.That(perspectiveTables).Count().IsEqualTo(1);
    await Assert.That(perspectiveTables[0]).IsEqualTo("wh_per_order");
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_AppliesAllMigrationsAsync() {
    // Arrange
    await DropAllWhizbangTablesAsync();

    // Act
    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync();

    // Assert - Check for migration-specific features
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Migration 001 - Outbox work coordination columns
    const string sql1 = @"
      SELECT column_name
      FROM information_schema.columns
      WHERE table_name = 'wh_outbox'
        AND column_name IN ('instance_id', 'lease_expiry', 'partition_number')";

    await using var command1 = new NpgsqlCommand(sql1, connection);
    await using var reader1 = await command1.ExecuteReaderAsync();

    var outboxColumns = new List<string>();
    while (await reader1.ReadAsync()) {
      outboxColumns.Add(reader1.GetString(0));
    }
    await reader1.CloseAsync();

    // At least instance_id and lease_expiry should be present (from migrations 001/002)
    // partition_number may be added in a later migration
    await Assert.That(outboxColumns).Count().IsGreaterThanOrEqualTo(2);
    await Assert.That(outboxColumns).Contains("instance_id");
    await Assert.That(outboxColumns).Contains("lease_expiry");

    // Migration 014 - process_work_batch function exists
    const string sql2 = @"
      SELECT routine_name
      FROM information_schema.routines
      WHERE routine_schema = 'public'
        AND routine_name = 'process_work_batch'
        AND routine_type = 'FUNCTION'";

    await using var command2 = new NpgsqlCommand(sql2, connection);
    var functionName = await command2.ExecuteScalarAsync() as string;

    await Assert.That(functionName).IsEqualTo("process_work_batch");
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_RecordsMigrationTrackingDataAsync() {
    // Arrange
    await DropAllWhizbangTablesAsync();

    // Act
    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync();

    // Assert - Migration tracking tables should have data
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // wh_schema_versions should have a version entry with both library and application versions
    string libraryVersion;
    string applicationVersion;
    {
      await using var versionCmd = new NpgsqlCommand(
        "SELECT library_version, application_version FROM wh_schema_versions LIMIT 1", connection);
      await using var versionReader = await versionCmd.ExecuteReaderAsync();
      await Assert.That(await versionReader.ReadAsync()).IsTrue();
      libraryVersion = versionReader.GetString(0);
      applicationVersion = versionReader.GetString(1);
    }

    // Library version should be a semver string (e.g., "0.9.4-local.64")
    await Assert.That(libraryVersion).Contains(".")
      .Because("library_version should be a semver version like '0.9.4', not an assembly name");

    // Application version should contain the assembly name and version (e.g., "Whizbang.Data.EFCore.Postgres.Tests/0.9.4.0")
    await Assert.That(applicationVersion).Contains("/")
      .Because("application_version should be 'AssemblyName/Version' format");
    await Assert.That(applicationVersion).Contains(".")
      .Because("application_version should include a version number");

    // wh_schema_migrations should have entries for each migration
    await using var migrationCmd = new NpgsqlCommand(
      "SELECT COUNT(*) FROM wh_schema_migrations WHERE status IN (1, 3)", connection);
    var migrationCount = (long)(await migrationCmd.ExecuteScalarAsync())!;
    await Assert.That(migrationCount).IsGreaterThanOrEqualTo(1)
      .Because("At least one migration should be recorded as Applied (1) or Skipped (3)");

    // Each migration should have a content hash
    await using var hashCmd = new NpgsqlCommand(
      "SELECT COUNT(*) FROM wh_schema_migrations WHERE content_hash IS NOT NULL AND LENGTH(content_hash) = 64", connection);
    var hashCount = (long)(await hashCmd.ExecuteScalarAsync())!;
    await Assert.That(hashCount).IsEqualTo(migrationCount)
      .Because("Every recorded migration should have a 64-char SHA256 hash");
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_SkipsUnchangedMigrationsOnSecondRunAsync() {
    // Arrange - First initialization
    await DropAllWhizbangTablesAsync();
    await using var dbContext1 = CreateDbContext();
    await dbContext1.EnsureWhizbangDatabaseInitializedAsync();

    // Act - Second initialization (same migrations)
    await using var dbContext2 = CreateDbContext();
    await dbContext2.EnsureWhizbangDatabaseInitializedAsync();

    // Assert - All migrations should be Skipped (status 3) on second run
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    await using var cmd = new NpgsqlCommand(
      "SELECT COUNT(*) FROM wh_schema_migrations WHERE status = 3", connection);
    var skippedCount = (long)(await cmd.ExecuteScalarAsync())!;
    await Assert.That(skippedCount).IsGreaterThanOrEqualTo(1)
      .Because("On second run, unchanged migrations should be skipped (status 3)");

    // Infrastructure migrations should not be Applied (1) on second run — they should all be Skipped (3)
    await using var appliedInfraCmd = new NpgsqlCommand(
      "SELECT COUNT(*) FROM wh_schema_migrations WHERE status = 1 AND file_name NOT LIKE 'perspective:%'", connection);
    var appliedInfraCount = (long)(await appliedInfraCmd.ExecuteScalarAsync())!;
    await Assert.That(appliedInfraCount).IsEqualTo(0)
      .Because("No infrastructure migrations should be newly applied on second identical run");
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_TracksPerspectivedIndividuallyAsync() {
    // Arrange
    await DropAllWhizbangTablesAsync();

    // Act
    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync();

    // Assert - Perspective tables should be tracked individually in wh_schema_migrations
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    await using var cmd = new NpgsqlCommand(
      "SELECT file_name, status, content_hash FROM wh_schema_migrations WHERE file_name LIKE 'perspective:%'", connection);
    await using var reader = await cmd.ExecuteReaderAsync();

    var perspectiveEntries = new List<(string Name, int Status, string Hash)>();
    while (await reader.ReadAsync()) {
      perspectiveEntries.Add((reader.GetString(0), reader.GetInt16(1), reader.GetString(2)));
    }

    // Should have at least one perspective entry tracked
    await Assert.That(perspectiveEntries.Count).IsGreaterThanOrEqualTo(1)
      .Because("Per-perspective entries should be tracked individually in wh_schema_migrations");

    // Each entry should have a valid hash
    foreach (var (name, status, hash) in perspectiveEntries) {
      await Assert.That(hash.Length).IsEqualTo(64)
        .Because($"Perspective entry '{name}' should have a 64-char SHA256 hash");
      await Assert.That(status == 1 || status == 3).IsTrue()
        .Because($"Perspective entry '{name}' should be Applied (1) or Skipped (3)");
    }
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_HandlesPartialInitializationAsync() {
    // Arrange - Create only core tables, no migrations
    await DropAllWhizbangTablesAsync();

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Create just the outbox table (simulating partial initialization)
    // This matches OutboxSchema.Table C# schema definition
    const string coreTableSql = @"
      CREATE TABLE IF NOT EXISTS wh_outbox (
        message_id UUID NOT NULL PRIMARY KEY,
        destination VARCHAR(500) NOT NULL,
        message_type VARCHAR(500) NOT NULL,
        event_data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NULL,
        stream_id UUID NULL,
        partition_number INTEGER NULL,
        status INTEGER NOT NULL DEFAULT 1,
        attempts INTEGER NOT NULL DEFAULT 0,
        error TEXT NULL,
        instance_id UUID NULL,
        lease_expiry TIMESTAMPTZ NULL,
        failure_reason INTEGER NOT NULL DEFAULT 99,
        scheduled_for TIMESTAMPTZ NULL,
        created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
        published_at TIMESTAMPTZ NULL,
        processed_at TIMESTAMPTZ NULL
      )";

    await using var createCommand = new NpgsqlCommand(coreTableSql, connection);
    await createCommand.ExecuteNonQueryAsync();

    // Act - Full initialization should add missing tables and migrations
    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync();

    // Assert - All tables should now exist
    const string sql = @"
      SELECT COUNT(*)
      FROM information_schema.tables
      WHERE table_schema = 'public'
        AND table_name LIKE 'wh_%'";

    await using var command = new NpgsqlCommand(sql, connection);
    var count = (long)(await command.ExecuteScalarAsync() ?? 0L);

    // Should have at least 10 tables (9 core + 1 perspective)
    await Assert.That(count).IsGreaterThanOrEqualTo(10);
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_SucceedsWithoutLoggingAsync() {
    // Arrange
    await DropAllWhizbangTablesAsync();

    // Act - Should succeed even without logger
    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync();

    // Assert - Verify tables were created
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    const string sql = @"
      SELECT COUNT(*)
      FROM information_schema.tables
      WHERE table_schema = 'public'
        AND table_name LIKE 'wh_%'";

    await using var command = new NpgsqlCommand(sql, connection);
    var count = (long)(await command.ExecuteScalarAsync() ?? 0L);

    // Should have at least 10 tables (9 core + 1 perspective)
    await Assert.That(count).IsGreaterThanOrEqualTo(10);
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_DoesNotReinitializeWhenCompleteAsync() {
    // Arrange - Full initialization
    await DropAllWhizbangTablesAsync();
    await using var dbContext1 = CreateDbContext();
    await dbContext1.EnsureWhizbangDatabaseInitializedAsync();

    // Act - Call again with new context
    await using var dbContext2 = CreateDbContext();
    await dbContext2.EnsureWhizbangDatabaseInitializedAsync();

    // Assert - Should not throw and should complete successfully
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    const string sql = @"
      SELECT COUNT(*)
      FROM information_schema.tables
      WHERE table_schema = 'public'
        AND table_name LIKE 'wh_%'";

    await using var command = new NpgsqlCommand(sql, connection);
    var count = (long)(await command.ExecuteScalarAsync() ?? 0L);

    await Assert.That(count).IsGreaterThanOrEqualTo(10);
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_ClearsConnectionPoolsAfterMigrationsAsync() {
    // This test verifies the ClearAllPools fix: after migrations run (CREATE OR REPLACE FUNCTION),
    // pooled connections with stale function OID caches must be discarded. ClearAllPools forces this.
    //
    // Strategy: warm up the connection pool, re-run initialization (which calls ClearAllPools),
    // then verify old pooled connections were discarded by checking backend PIDs changed.

    // Arrange: Start with clean state and initialize
    await DropAllWhizbangTablesAsync();
    await using var dbContext1 = CreateDbContext();
    await dbContext1.EnsureWhizbangDatabaseInitializedAsync();

    // Warm up the legacy connection pool: open connections and return them to the pool.
    // These connections cache PostgreSQL type/function OID mappings internally.
    var oldPids = new HashSet<int>();
    for (var i = 0; i < 3; i++) {
      await using var conn = new NpgsqlConnection(ConnectionString);
      await conn.OpenAsync();
      await using var cmd = new NpgsqlCommand("SELECT pg_backend_pid()", conn);
      oldPids.Add((int)(await cmd.ExecuteScalarAsync())!);
    }

    // Act: Re-initialize (simulates service restart where migrations re-run).
    // EnsureWhizbangDatabaseInitializedAsync runs CREATE OR REPLACE FUNCTION migrations
    // and then calls NpgsqlConnection.ClearAllPools() to discard stale connections.
    await using var dbContext2 = CreateDbContext();
    await dbContext2.EnsureWhizbangDatabaseInitializedAsync();

    // Assert: Old pooled connections should have been discarded by ClearAllPools.
    // New connections will get new PostgreSQL backend PIDs because the physical
    // connections were closed and re-established.
    var newPids = new HashSet<int>();
    for (var i = 0; i < 3; i++) {
      await using var conn = new NpgsqlConnection(ConnectionString);
      await conn.OpenAsync();
      await using var cmd = new NpgsqlCommand("SELECT pg_backend_pid()", conn);
      newPids.Add((int)(await cmd.ExecuteScalarAsync())!);
    }

    // If ClearAllPools worked, old connections were discarded and new ones were created.
    // PostgreSQL assigns new backend PIDs for new connections, so there should be no overlap.
    var reusedPids = oldPids.Intersect(newPids).Count();
    await Assert.That(reusedPids).IsEqualTo(0);
  }

  /// <summary>
  /// Helper method to drop all Whizbang tables for clean test state.
  /// </summary>
  private async Task DropAllWhizbangTablesAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Drop all Whizbang tables in correct order (respecting foreign keys)
    const string dropSql = @"
      DROP TABLE IF EXISTS wh_schema_migrations CASCADE;
      DROP TABLE IF EXISTS wh_schema_versions CASCADE;
      DROP TABLE IF EXISTS wh_receptor_processing CASCADE;
      DROP TABLE IF EXISTS wh_perspective_cursors CASCADE;
      DROP TABLE IF EXISTS wh_per_order CASCADE;
      DROP TABLE IF EXISTS wh_event_store CASCADE;
      DROP TABLE IF EXISTS wh_outbox CASCADE;
      DROP TABLE IF EXISTS wh_inbox CASCADE;
      DROP TABLE IF EXISTS wh_request_response CASCADE;
      DROP TABLE IF EXISTS wh_message_deduplication CASCADE;
      DROP TABLE IF EXISTS wh_service_instances CASCADE;
      DROP TABLE IF EXISTS wh_sequences CASCADE;
      DROP FUNCTION IF EXISTS process_work_batch CASCADE;
      DROP FUNCTION IF EXISTS claim_outbox_messages CASCADE;
      DROP FUNCTION IF EXISTS claim_inbox_messages CASCADE;
      DROP FUNCTION IF EXISTS update_receptor_processing CASCADE;
      DROP FUNCTION IF EXISTS update_perspective_cursors CASCADE;";

    await using var command = new NpgsqlCommand(dropSql, connection);
    await command.ExecuteNonQueryAsync();
  }
}
