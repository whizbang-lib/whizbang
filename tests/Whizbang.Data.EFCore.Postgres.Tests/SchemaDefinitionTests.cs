using Npgsql;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres.Schema;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Schema;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests for schema definition validation.
/// Tests the pre-generated SQL schema for AOT compatibility.
/// </summary>
public class SchemaDefinitionTests : EFCoreTestBase {
  [Test]
  public async Task CoreInfrastructureSchema_ShouldCreateAllRequiredTablesAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query for all Whizbang tables
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

    // Assert - 9 core infrastructure tables
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
  public async Task PerspectiveTable_ShouldHaveCorrectSchemaAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query perspective table columns
    var sql = @"
      SELECT column_name, data_type, is_nullable
      FROM information_schema.columns
      WHERE table_name = 'wh_per_order'
      ORDER BY ordinal_position";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var columns = new Dictionary<string, (string DataType, string IsNullable)>();
    while (await reader.ReadAsync()) {
      columns[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
    }

    // Assert - PerspectiveRow<TModel> fixed schema
    await Assert.That(columns).ContainsKey("id");
    await Assert.That(columns["id"].DataType).IsEqualTo("uuid");
    await Assert.That(columns["id"].IsNullable).IsEqualTo("NO");

    await Assert.That(columns).ContainsKey("data");
    await Assert.That(columns["data"].DataType).IsEqualTo("jsonb");
    await Assert.That(columns["data"].IsNullable).IsEqualTo("NO");

    await Assert.That(columns).ContainsKey("metadata");
    await Assert.That(columns["metadata"].DataType).IsEqualTo("jsonb");
    await Assert.That(columns["metadata"].IsNullable).IsEqualTo("NO");

    await Assert.That(columns).ContainsKey("scope");
    await Assert.That(columns["scope"].DataType).IsEqualTo("jsonb");
    await Assert.That(columns["scope"].IsNullable).IsEqualTo("NO");

    await Assert.That(columns).ContainsKey("version");
    await Assert.That(columns["version"].DataType).IsEqualTo("integer");
    await Assert.That(columns["version"].IsNullable).IsEqualTo("NO");

    await Assert.That(columns).ContainsKey("created_at");
    await Assert.That(columns["created_at"].DataType).IsEqualTo("timestamp with time zone");
    await Assert.That(columns["created_at"].IsNullable).IsEqualTo("NO");

    await Assert.That(columns).ContainsKey("updated_at");
    await Assert.That(columns["updated_at"].DataType).IsEqualTo("timestamp with time zone");
    await Assert.That(columns["updated_at"].IsNullable).IsEqualTo("NO");
  }

  [Test]
  public async Task PerspectiveCheckpoints_ShouldHaveCompositePrimaryKeyAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query constraint information
    var sql = @"
      SELECT
        tc.constraint_name,
        kcu.column_name,
        tc.constraint_type
      FROM information_schema.table_constraints tc
      JOIN information_schema.key_column_usage kcu
        ON tc.constraint_name = kcu.constraint_name
        AND tc.table_schema = kcu.table_schema
      WHERE tc.table_name = 'wh_perspective_checkpoints'
        AND tc.constraint_type = 'PRIMARY KEY'
      ORDER BY kcu.ordinal_position";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var pkColumns = new List<string>();
    while (await reader.ReadAsync()) {
      pkColumns.Add(reader.GetString(1));
    }

    // Assert - Composite PK: (stream_id, perspective_name)
    await Assert.That(pkColumns).HasCount().EqualTo(2);
    await Assert.That(pkColumns[0]).IsEqualTo("stream_id");
    await Assert.That(pkColumns[1]).IsEqualTo("perspective_name");
  }

  [Test]
  public async Task ReceptorProcessing_ShouldHaveForeignKeyToEventStoreAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query foreign key constraints
    var sql = @"
      SELECT
        tc.constraint_name,
        kcu.column_name,
        ccu.table_name AS foreign_table_name,
        ccu.column_name AS foreign_column_name
      FROM information_schema.table_constraints AS tc
      JOIN information_schema.key_column_usage AS kcu
        ON tc.constraint_name = kcu.constraint_name
        AND tc.table_schema = kcu.table_schema
      JOIN information_schema.constraint_column_usage AS ccu
        ON ccu.constraint_name = tc.constraint_name
        AND ccu.table_schema = tc.table_schema
      WHERE tc.table_name = 'wh_receptor_processing'
        AND tc.constraint_type = 'FOREIGN KEY'";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var foreignKeys = new List<(string Column, string RefTable, string RefColumn)>();
    while (await reader.ReadAsync()) {
      foreignKeys.Add((reader.GetString(1), reader.GetString(2), reader.GetString(3)));
    }

    // Assert - FK: event_id -> wh_event_store.event_id
    await Assert.That(foreignKeys).HasCount().GreaterThanOrEqualTo(1);
    var eventFk = foreignKeys.FirstOrDefault(fk => fk.Column == "event_id");
    await Assert.That(eventFk).IsNotEqualTo(default((string, string, string)));
    await Assert.That(eventFk.RefTable).IsEqualTo("wh_event_store");
    await Assert.That(eventFk.RefColumn).IsEqualTo("event_id");
  }

  [Test]
  public async Task PerspectiveCheckpoints_ShouldHaveForeignKeyToEventStoreAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query foreign key constraints
    var sql = @"
      SELECT
        tc.constraint_name,
        kcu.column_name,
        ccu.table_name AS foreign_table_name,
        ccu.column_name AS foreign_column_name
      FROM information_schema.table_constraints AS tc
      JOIN information_schema.key_column_usage AS kcu
        ON tc.constraint_name = kcu.constraint_name
        AND tc.table_schema = kcu.table_schema
      JOIN information_schema.constraint_column_usage AS ccu
        ON ccu.constraint_name = tc.constraint_name
        AND ccu.table_schema = tc.table_schema
      WHERE tc.table_name = 'wh_perspective_checkpoints'
        AND tc.constraint_type = 'FOREIGN KEY'";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var foreignKeys = new List<(string Column, string RefTable, string RefColumn)>();
    while (await reader.ReadAsync()) {
      foreignKeys.Add((reader.GetString(1), reader.GetString(2), reader.GetString(3)));
    }

    // Assert - FK: last_event_id -> wh_event_store.event_id
    await Assert.That(foreignKeys).HasCount().GreaterThanOrEqualTo(1);
    var eventFk = foreignKeys.FirstOrDefault(fk => fk.Column == "last_event_id");
    await Assert.That(eventFk).IsNotEqualTo(default((string, string, string)));
    await Assert.That(eventFk.RefTable).IsEqualTo("wh_event_store");
    await Assert.That(eventFk.RefColumn).IsEqualTo("event_id");
  }

  [Test]
  public async Task ReceptorProcessing_ShouldHaveUniqueConstraintAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query unique indexes
    var sql = @"
      SELECT
        i.indexname,
        i.indexdef
      FROM pg_indexes i
      WHERE i.tablename = 'wh_receptor_processing'
        AND i.indexdef LIKE '%UNIQUE%'
      ORDER BY i.indexname";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var uniqueIndexes = new List<string>();
    while (await reader.ReadAsync()) {
      uniqueIndexes.Add(reader.GetString(0));
    }

    // Assert - Unique constraint for (event_id, receptor_name)
    await Assert.That(uniqueIndexes).HasCount().GreaterThanOrEqualTo(1);
    await Assert.That(uniqueIndexes).Contains("uq_receptor_processing_event_receptor");
  }

  [Test]
  public async Task PartialIndexes_ShouldExistForStatusQueriesAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query partial indexes
    var sql = @"
      SELECT
        i.indexname,
        i.tablename,
        pg_get_indexdef(idx.indexrelid) as index_definition
      FROM pg_indexes i
      JOIN pg_class c ON c.relname = i.tablename
      JOIN pg_index idx ON idx.indrelid = c.oid
      JOIN pg_class ic ON ic.oid = idx.indexrelid
      WHERE i.tablename IN ('wh_receptor_processing', 'wh_perspective_checkpoints')
        AND pg_get_indexdef(idx.indexrelid) LIKE '%WHERE%'
      ORDER BY i.indexname";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var partialIndexes = new Dictionary<string, string>();
    while (await reader.ReadAsync()) {
      partialIndexes[reader.GetString(0)] = reader.GetString(2);
    }

    // Assert - Partial indexes exist
    await Assert.That(partialIndexes).ContainsKey("idx_receptor_processing_status");
    await Assert.That(partialIndexes).ContainsKey("idx_perspective_checkpoints_catching_up");
    await Assert.That(partialIndexes).ContainsKey("idx_perspective_checkpoints_failed");

    // Assert - Partial index definitions contain status checks
    await Assert.That(partialIndexes["idx_receptor_processing_status"]).Contains("WHERE");
    await Assert.That(partialIndexes["idx_perspective_checkpoints_catching_up"]).Contains("WHERE");
    await Assert.That(partialIndexes["idx_perspective_checkpoints_failed"]).Contains("WHERE");
  }

  [Test]
  public async Task PerspectiveTable_ShouldHaveCreatedAtIndexAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query indexes on wh_per_order
    var sql = @"
      SELECT indexname
      FROM pg_indexes
      WHERE tablename = 'wh_per_order'
        AND indexdef LIKE '%created_at%'";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var indexes = new List<string>();
    while (await reader.ReadAsync()) {
      indexes.Add(reader.GetString(0));
    }

    // Assert - Index on created_at column (generated by EF Core)
    await Assert.That(indexes).HasCount().GreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task PostgresSchemaBuilder_ShouldGenerateValidSQLAsync() {
    // Arrange
    var config = new SchemaConfiguration("wh_", "wh_per_", "public", 1);

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert - SQL should not be empty
    await Assert.That(sql).IsNotNull();
    await Assert.That(sql.Length).IsGreaterThan(0);

    // Assert - SQL should contain CREATE TABLE statements for all 9 core tables
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_service_instances");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_message_deduplication");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_inbox");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_outbox");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_event_store");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_receptor_processing");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_perspective_checkpoints");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_request_response");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_sequences");

    // Assert - SQL should have IF NOT EXISTS for idempotency
    await Assert.That(sql).Contains("IF NOT EXISTS");
  }

  [Test]
  public async Task SchemaInitialization_ShouldBeIdempotentAsync() {
    // Arrange
    await using var dbContext = CreateDbContext();

    // Act - Initialize schema twice
    await dbContext.EnsureWhizbangDatabaseInitializedAsync();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync();

    // Assert - Should not throw (idempotent operation)
    // Query to verify tables exist
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
}
