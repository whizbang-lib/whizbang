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

    var sql = @"
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
    await Assert.That(tables).Contains("wh_perspective_checkpoints");
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

    var sql = @"
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

    var sql = @"
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

    await Assert.That(perspectiveTables).HasCount().EqualTo(1);
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
    var sql1 = @"
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
    await Assert.That(outboxColumns).HasCount().GreaterThanOrEqualTo(2);
    await Assert.That(outboxColumns).Contains("instance_id");
    await Assert.That(outboxColumns).Contains("lease_expiry");

    // Migration 014 - process_work_batch function exists
    var sql2 = @"
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
  public async Task EnsureWhizbangDatabaseInitialized_HandlesPartialInitializationAsync() {
    // Arrange - Create only core tables, no migrations
    await DropAllWhizbangTablesAsync();

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Create just the outbox table (simulating partial initialization)
    // This matches migration 001_CreateOutboxTable.sql schema
    var coreTableSql = @"
      CREATE TABLE IF NOT EXISTS wh_outbox (
        message_id UUID NOT NULL PRIMARY KEY,
        destination VARCHAR(500) NOT NULL,
        event_type VARCHAR(500) NOT NULL,
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
    var sql = @"
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

    var sql = @"
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

    var sql = @"
      SELECT COUNT(*)
      FROM information_schema.tables
      WHERE table_schema = 'public'
        AND table_name LIKE 'wh_%'";

    await using var command = new NpgsqlCommand(sql, connection);
    var count = (long)(await command.ExecuteScalarAsync() ?? 0L);

    await Assert.That(count).IsGreaterThanOrEqualTo(10);
  }

  /// <summary>
  /// Helper method to drop all Whizbang tables for clean test state.
  /// </summary>
  private async Task DropAllWhizbangTablesAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Drop all Whizbang tables in correct order (respecting foreign keys)
    var dropSql = @"
      DROP TABLE IF EXISTS wh_receptor_processing CASCADE;
      DROP TABLE IF EXISTS wh_perspective_checkpoints CASCADE;
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
      DROP FUNCTION IF EXISTS update_perspective_checkpoint CASCADE;";

    await using var command = new NpgsqlCommand(dropSql, connection);
    await command.ExecuteNonQueryAsync();
  }
}
