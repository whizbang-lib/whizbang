using System.Collections.Immutable;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests;

[InheritsTests]

/// <summary>
/// Tests for PostgresSchemaBuilder - generates Postgres DDL from schema definitions.
/// Inherits from ISchemaBuilderContractTests to ensure compliance with ISchemaBuilder interface.
/// </summary>
public class PostgresSchemaBuilderTests : ISchemaBuilderContractTests {
  protected override ISchemaBuilder CreateBuilder() => new PostgresSchemaBuilder();
  protected override string ExpectedDatabaseEngine => "Postgres";
  [Test]
  public async Task BuildCreateTable_SimpleTable_GeneratesCreateStatementAsync() {
    // Arrange
    var table = new TableDefinition(
      Name: "test_table",
      Columns: ImmutableArray.Create(
        new ColumnDefinition("id", WhizbangDataType.UUID, PrimaryKey: true, Nullable: false)
      )
    );
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateTable(table, config.InfrastructurePrefix);

    // Assert
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_test_table");
    await Assert.That(sql).Contains("id UUID NOT NULL PRIMARY KEY");
  }

  [Test]
  public async Task BuildCreateTable_WithMultipleColumns_GeneratesAllColumnsAsync() {
    // Arrange
    var table = new TableDefinition(
      Name: "users",
      Columns: ImmutableArray.Create(
        new ColumnDefinition("id", WhizbangDataType.UUID, PrimaryKey: true, Nullable: false),
        new ColumnDefinition("name", WhizbangDataType.STRING, MaxLength: 255, Nullable: false),
        new ColumnDefinition("age", WhizbangDataType.INTEGER, Nullable: true)
      )
    );
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateTable(table, config.InfrastructurePrefix);

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
        new ColumnDefinition("id", WhizbangDataType.UUID, PrimaryKey: true, Nullable: false),
        new ColumnDefinition(
          "created_at",
          WhizbangDataType.TIMESTAMP_TZ,
          Nullable: false,
          DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
        )
      )
    );
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateTable(table, config.InfrastructurePrefix);

    // Assert
    await Assert.That(sql).Contains("created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP");
  }

  [Test]
  public async Task BuildCreateTable_WithUniqueColumn_GeneratesUniqueConstraintAsync() {
    // Arrange
    var table = new TableDefinition(
      Name: "users",
      Columns: ImmutableArray.Create(
        new ColumnDefinition("id", WhizbangDataType.UUID, PrimaryKey: true, Nullable: false),
        new ColumnDefinition("email", WhizbangDataType.STRING, MaxLength: 255, Nullable: false, Unique: true)
      )
    );
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateTable(table, config.InfrastructurePrefix);

    // Assert
    await Assert.That(sql).Contains("email VARCHAR(255) NOT NULL UNIQUE");
  }

  [Test]
  public async Task BuildCreateTable_PerspectivePrefix_UsesPerspectivePrefixAsync() {
    // Arrange
    var table = new TableDefinition(
      Name: "product_dto",
      Columns: ImmutableArray.Create(
        new ColumnDefinition("id", WhizbangDataType.UUID, PrimaryKey: true, Nullable: false)
      )
    );
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateTable(table, config.PerspectivePrefix);

    // Assert
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_per_product_dto");
  }

  [Test]
  public async Task BuildCreateIndex_SimpleIndex_GeneratesCreateIndexAsync() {
    // Arrange
    var index = new IndexDefinition(
      Name: "idx_users_email",
      Columns: ImmutableArray.Create("email")
    );
    var tableName = "users";
    var prefix = "wh_";

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateIndex(index, tableName, prefix);

    // Assert
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_users_email");
    await Assert.That(sql).Contains("ON wh_users (email)");
  }

  [Test]
  public async Task BuildCreateIndex_CompositeIndex_GeneratesMultiColumnIndexAsync() {
    // Arrange
    var index = new IndexDefinition(
      Name: "idx_events_type_created",
      Columns: ImmutableArray.Create("event_type", "created_at")
    );
    var tableName = "events";
    var prefix = "wh_";

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateIndex(index, tableName, prefix);

    // Assert
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_events_type_created");
    await Assert.That(sql).Contains("ON wh_events (event_type, created_at)");
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
    var prefix = "wh_";

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateIndex(index, tableName, prefix);

    // Assert
    await Assert.That(sql).Contains("CREATE UNIQUE INDEX IF NOT EXISTS idx_aggregate_version");
    await Assert.That(sql).Contains("ON wh_event_store (aggregate_id, version)");
  }

  [Test]
  public async Task BuildInfrastructureSchema_GeneratesAllTablesAsync() {
    // Arrange
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_inbox");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_outbox");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_event_store");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_request_response");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_sequences");
  }

  [Test]
  public async Task BuildInfrastructureSchema_GeneratesAllIndexesAsync() {
    // Arrange
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert
    // Inbox indexes
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_inbox_processed_at");
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_inbox_received_at");

    // Outbox indexes
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_outbox_status_created_at");
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_outbox_published_at");

    // EventStore indexes
    await Assert.That(sql).Contains("CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_stream");
    await Assert.That(sql).Contains("CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_aggregate");
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_event_store_aggregate_type");

    // RequestResponse indexes
    await Assert.That(sql).Contains("CREATE UNIQUE INDEX IF NOT EXISTS idx_request_response_correlation");
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_request_response_status_created");
    await Assert.That(sql).Contains("CREATE INDEX IF NOT EXISTS idx_request_response_expires");
  }

  [Test]
  public async Task BuildInfrastructureSchema_InboxTable_HasCorrectStructureAsync() {
    // Arrange
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("message_id UUID NOT NULL PRIMARY KEY");
    await Assert.That(sql).Contains("message_type VARCHAR(500) NOT NULL");
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
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert - OutboxSchema uses integer status with bitflags
    await Assert.That(sql).Contains("status INTEGER NOT NULL DEFAULT 1");
    await Assert.That(sql).Contains("attempts INTEGER NOT NULL DEFAULT 0");
  }

  [Test]
  public async Task BuildInfrastructureSchema_EventStoreTable_HasUniqueConstraintAsync() {
    // Arrange
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("CREATE UNIQUE INDEX IF NOT EXISTS idx_event_store_aggregate");
    await Assert.That(sql).Contains("ON wh_event_store (aggregate_id, version)");
  }

  [Test]
  public new async Task BuildInfrastructureSchema_CustomPrefix_UsesCustomPrefixAsync() {
    // Arrange
    var config = new SchemaConfiguration(InfrastructurePrefix: "custom_");

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS custom_inbox");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS custom_outbox");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS custom_event_store");
  }

  [Test]
  public async Task BuildCreateSequence_SimpleSequence_GeneratesCreateSequenceAsync() {
    // Arrange
    var sequence = new SequenceDefinition("event_sequence");
    var prefix = "wh_";

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateSequence(sequence, prefix);

    // Assert
    await Assert.That(sql).Contains("CREATE SEQUENCE IF NOT EXISTS wh_event_sequence");
    await Assert.That(sql).Contains("START WITH 1");
    await Assert.That(sql).Contains("INCREMENT BY 1");
  }

  [Test]
  public async Task BuildCreateSequence_WithCustomStartValue_GeneratesCorrectStartAsync() {
    // Arrange
    var sequence = new SequenceDefinition("order_sequence", StartValue: 1000);
    var prefix = "wh_";

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateSequence(sequence, prefix);

    // Assert
    await Assert.That(sql).Contains("START WITH 1000");
  }

  [Test]
  public async Task BuildCreateSequence_WithCustomIncrement_GeneratesCorrectIncrementAsync() {
    // Arrange
    var sequence = new SequenceDefinition("batch_sequence", IncrementBy: 10);
    var prefix = "wh_";

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateSequence(sequence, prefix);

    // Assert
    await Assert.That(sql).Contains("INCREMENT BY 10");
  }

  [Test]
  public async Task BuildInfrastructureSchema_GeneratesEventSequenceAsync() {
    // Arrange
    var config = new SchemaConfiguration();

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("CREATE SEQUENCE IF NOT EXISTS wh_event_sequence");
    await Assert.That(sql).Contains("START WITH 1 INCREMENT BY 1");
  }

  [Test]
  public async Task BuildInfrastructureSchema_EventSequence_UsesCorrectPrefixAsync() {
    // Arrange
    var config = new SchemaConfiguration(InfrastructurePrefix: "custom_");

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("CREATE SEQUENCE IF NOT EXISTS custom_event_sequence");
  }

  // =============================================================================
  // Schema Quoting Tests - Ensures PostgreSQL reserved keywords are properly quoted
  // =============================================================================

  [Test]
  public async Task BuildInfrastructureSchema_WithReservedKeywordSchema_QuotesSchemaNameAsync() {
    // Arrange
    // "user" is a PostgreSQL reserved keyword that causes syntax errors if unquoted
    var config = new SchemaConfiguration(SchemaName: "user");

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert - Schema should be quoted with double quotes to handle reserved keywords
    await Assert.That(sql).Contains("CREATE SCHEMA IF NOT EXISTS \"user\"");
    await Assert.That(sql).Contains("\"user\".wh_inbox");
    await Assert.That(sql).Contains("\"user\".wh_outbox");
  }

  [Test]
  public async Task BuildInfrastructureSchema_WithSelectSchema_QuotesSchemaNameAsync() {
    // Arrange
    // "select" is another PostgreSQL reserved keyword
    var config = new SchemaConfiguration(SchemaName: "select");

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("CREATE SCHEMA IF NOT EXISTS \"select\"");
    await Assert.That(sql).Contains("\"select\".wh_event_store");
  }

  [Test]
  public async Task BuildInfrastructureSchema_WithTableSchema_QuotesSchemaNameAsync() {
    // Arrange
    // "table" is a PostgreSQL reserved keyword
    var config = new SchemaConfiguration(SchemaName: "table");

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("CREATE SCHEMA IF NOT EXISTS \"table\"");
  }

  [Test]
  public async Task BuildCreateTable_WithReservedKeywordSchema_QuotesSchemaInTableNameAsync() {
    // Arrange
    var table = new TableDefinition(
      Name: "test_table",
      Columns: ImmutableArray.Create(
        new ColumnDefinition("id", WhizbangDataType.UUID, PrimaryKey: true, Nullable: false)
      )
    );

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateTable(table, "wh_", "user");

    // Assert - Schema should be quoted
    await Assert.That(sql).Contains("\"user\".wh_test_table");
  }

  [Test]
  public async Task BuildCreateIndex_WithReservedKeywordSchema_QuotesSchemaInTableNameAsync() {
    // Arrange
    var index = new IndexDefinition(
      Name: "idx_test",
      Columns: ImmutableArray.Create("email")
    );

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateIndex(index, "users", "wh_", "user");

    // Assert - Schema should be quoted in the ON clause
    await Assert.That(sql).Contains("ON \"user\".wh_users");
  }

  [Test]
  public async Task BuildCreateSequence_WithReservedKeywordSchema_QuotesSchemaNameAsync() {
    // Arrange
    var sequence = new SequenceDefinition("event_sequence");

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildCreateSequence(sequence, "wh_", "user");

    // Assert - Schema should be quoted
    await Assert.That(sql).Contains("\"user\".wh_event_sequence");
  }

  [Test]
  public async Task BuildInfrastructureSchema_WithNormalSchema_StillQuotesSchemaNameAsync() {
    // Arrange
    // Even non-reserved schema names should be quoted for consistency
    var config = new SchemaConfiguration(SchemaName: "inventory");

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert - All schema names should be quoted for safety
    await Assert.That(sql).Contains("CREATE SCHEMA IF NOT EXISTS \"inventory\"");
    await Assert.That(sql).Contains("\"inventory\".wh_inbox");
  }

  [Test]
  public async Task BuildInfrastructureSchema_WithPublicSchema_OmitsSchemaQualificationAsync() {
    // Arrange
    // public schema is the default and doesn't need explicit qualification
    var config = new SchemaConfiguration(SchemaName: "public");

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert - public schema shouldn't create a separate CREATE SCHEMA statement
    // and table names shouldn't be prefixed with "public."
    await Assert.That(sql).DoesNotContain("public.wh_inbox");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_inbox");
  }

  [Test]
  public async Task BuildInfrastructureSchema_WithEmptySchema_OmitsSchemaQualificationAsync() {
    // Arrange
    var config = new SchemaConfiguration(SchemaName: "");

    // Act
    var sql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(config);

    // Assert - Empty schema should not add schema qualification
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_inbox");
    await Assert.That(sql).DoesNotContain("\"\".wh_inbox");
  }

}
