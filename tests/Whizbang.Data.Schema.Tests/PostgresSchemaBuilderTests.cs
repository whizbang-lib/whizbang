using System.Collections.Immutable;
using Whizbang.Data.Schema.Postgres;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests;

/// <summary>
/// Tests for PostgresSchemaBuilder - generates Postgres DDL from schema definitions.
/// </summary>
public class PostgresSchemaBuilderTests {
  [Test]
  public async Task BuildCreateTable_SimpleTable_GeneratesCreateStatementAsync() {
    // Arrange
    var table = new TableDefinition(
      Name: "test_table",
      Columns: ImmutableArray.Create(
        new ColumnDefinition("id", WhizbangDataType.Uuid, PrimaryKey: true, Nullable: false)
      )
    );
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.BuildCreateTable(table, config.InfrastructurePrefix);

    // Assert
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wb_test_table");
    await Assert.That(sql).Contains("id UUID NOT NULL PRIMARY KEY");
  }

  [Test]
  public async Task BuildCreateTable_WithMultipleColumns_GeneratesAllColumnsAsync() {
    // Arrange
    var table = new TableDefinition(
      Name: "users",
      Columns: ImmutableArray.Create(
        new ColumnDefinition("id", WhizbangDataType.Uuid, PrimaryKey: true, Nullable: false),
        new ColumnDefinition("name", WhizbangDataType.String, MaxLength: 255, Nullable: false),
        new ColumnDefinition("age", WhizbangDataType.Integer, Nullable: true)
      )
    );
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.BuildCreateTable(table, config.InfrastructurePrefix);

    // Assert
    await Assert.That(sql).Contains("id UUID NOT NULL PRIMARY KEY");
    await Assert.That(sql).Contains("name VARCHAR(255) NOT NULL");
    await Assert.That(sql).Contains("age INTEGER");
  }

  [Test]
  public async Task BuildCreateTable_WithDefaultValue_GeneratesDefaultClauseAsync() {
    // Arrange
    var table = new TableDefinition(
      Name: "events",
      Columns: ImmutableArray.Create(
        new ColumnDefinition("id", WhizbangDataType.Uuid, PrimaryKey: true, Nullable: false),
        new ColumnDefinition(
          "created_at",
          WhizbangDataType.TimestampTz,
          Nullable: false,
          DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
        )
      )
    );
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.BuildCreateTable(table, config.InfrastructurePrefix);

    // Assert
    await Assert.That(sql).Contains("created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP");
  }

  [Test]
  public async Task BuildCreateTable_WithUniqueColumn_GeneratesUniqueConstraintAsync() {
    // Arrange
    var table = new TableDefinition(
      Name: "users",
      Columns: ImmutableArray.Create(
        new ColumnDefinition("id", WhizbangDataType.Uuid, PrimaryKey: true, Nullable: false),
        new ColumnDefinition("email", WhizbangDataType.String, MaxLength: 255, Nullable: false, Unique: true)
      )
    );
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.BuildCreateTable(table, config.InfrastructurePrefix);

    // Assert
    await Assert.That(sql).Contains("email VARCHAR(255) NOT NULL UNIQUE");
  }

  [Test]
  public async Task BuildCreateTable_PerspectivePrefix_UsesPerspectivePrefixAsync() {
    // Arrange
    var table = new TableDefinition(
      Name: "product_dto",
      Columns: ImmutableArray.Create(
        new ColumnDefinition("id", WhizbangDataType.Uuid, PrimaryKey: true, Nullable: false)
      )
    );
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.BuildCreateTable(table, config.PerspectivePrefix);

    // Assert
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wb_per_product_dto");
  }

  [Test]
  public async Task BuildCreateIndex_SimpleIndex_GeneratesCreateIndexAsync() {
    // Arrange
    var index = new IndexDefinition(
      Name: "idx_users_email",
      Columns: ImmutableArray.Create("email")
    );
    var tableName = "users";
    var prefix = "wb_";

    // Act
    var sql = PostgresSchemaBuilder.BuildCreateIndex(index, tableName, prefix);

    // Assert
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_users_email");
    await Assert.That(sql).Contains("ON wb_users (email)");
  }

  [Test]
  public async Task BuildCreateIndex_CompositeIndex_GeneratesMultiColumnIndexAsync() {
    // Arrange
    var index = new IndexDefinition(
      Name: "idx_events_type_created",
      Columns: ImmutableArray.Create("event_type", "created_at")
    );
    var tableName = "events";
    var prefix = "wb_";

    // Act
    var sql = PostgresSchemaBuilder.BuildCreateIndex(index, tableName, prefix);

    // Assert
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_events_type_created");
    await Assert.That(sql).Contains("ON wb_events (event_type, created_at)");
  }

  [Test]
  public async Task BuildCreateIndex_UniqueIndex_GeneratesUniqueIndexAsync() {
    // Arrange
    var index = new IndexDefinition(
      Name: "idx_aggregate_version",
      Columns: ImmutableArray.Create("aggregate_id", "version"),
      Unique: true
    );
    var tableName = "event_store";
    var prefix = "wb_";

    // Act
    var sql = PostgresSchemaBuilder.BuildCreateIndex(index, tableName, prefix);

    // Assert
    await Assert.That(sql).Contains("CREATE UNIQUE INDEX IF NOT EXISTS idx_aggregate_version");
    await Assert.That(sql).Contains("ON wb_event_store (aggregate_id, version)");
  }

  [Test]
  public async Task BuildInfrastructureSchema_GeneratesAllTablesAsync() {
    // Arrange
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wb_inbox");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wb_outbox");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wb_event_store");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wb_request_response");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wb_sequences");
  }

  [Test]
  public async Task BuildInfrastructureSchema_GeneratesAllIndexesAsync() {
    // Arrange
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.BuildInfrastructureSchema(config);

    // Assert
    // Inbox indexes
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_inbox_processed_at");
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_inbox_received_at");

    // Outbox indexes
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_outbox_status_created_at");
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_outbox_published_at");

    // EventStore indexes
    await Assert.That(sql).Contains("CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_aggregate");
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_event_store_aggregate_type");
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_event_store_sequence");

    // RequestResponse indexes
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_request_response_correlation");
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_request_response_status_created");
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_request_response_expires");
  }

  [Test]
  public async Task BuildInfrastructureSchema_InboxTable_HasCorrectStructureAsync() {
    // Arrange
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("message_id UUID NOT NULL PRIMARY KEY");
    await Assert.That(sql).Contains("event_type VARCHAR(500) NOT NULL");
    await Assert.That(sql).Contains("event_data JSONB NOT NULL");
    await Assert.That(sql).Contains("metadata JSONB NOT NULL");
    await Assert.That(sql).Contains("scope JSONB");
    await Assert.That(sql).Contains("processed_at TIMESTAMPTZ");
    await Assert.That(sql).Contains("received_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP");
  }

  [Test]
  public async Task BuildInfrastructureSchema_OutboxTable_HasCorrectDefaultsAsync() {
    // Arrange
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("status VARCHAR(50) NOT NULL DEFAULT 'Pending'");
    await Assert.That(sql).Contains("attempts INTEGER NOT NULL DEFAULT 0");
  }

  [Test]
  public async Task BuildInfrastructureSchema_EventStoreTable_HasUniqueConstraintAsync() {
    // Arrange
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_aggregate");
    await Assert.That(sql).Contains("ON wb_event_store (aggregate_id, version)");
  }

  [Test]
  public async Task BuildInfrastructureSchema_CustomPrefix_UsesCustomPrefixAsync() {
    // Arrange
    var config = new SchemaConfiguration(InfrastructurePrefix: "custom_");

    // Act
    var sql = PostgresSchemaBuilder.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS custom_inbox");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS custom_outbox");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS custom_event_store");
  }
}
