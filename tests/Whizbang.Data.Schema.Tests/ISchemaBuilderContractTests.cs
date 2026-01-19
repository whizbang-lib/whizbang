using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Tests;

/// <summary>
/// Abstract contract test class that all ISchemaBuilder implementations must inherit from.
/// Ensures all schema builders follow consistent patterns and generate valid DDL.
/// </summary>
/// <docs>data-access/schema-generation-pattern</docs>
public abstract class ISchemaBuilderContractTests {
  /// <summary>
  /// Returns the ISchemaBuilder implementation to test.
  /// Derived classes must implement this to provide their specific builder.
  /// </summary>
  protected abstract ISchemaBuilder CreateBuilder();

  /// <summary>
  /// Returns the expected database engine name for this builder.
  /// </summary>
  protected abstract string ExpectedDatabaseEngine { get; }

  [Test]
  public async Task DatabaseEngine_ReturnsExpectedNameAsync() {
    // Arrange
    var builder = CreateBuilder();

    // Act
    var engine = builder.DatabaseEngine;

    // Assert
    await Assert.That(engine).IsEqualTo(ExpectedDatabaseEngine);
  }

  [Test]
  public async Task BuildCreateTable_SimpleTable_GeneratesValidDDLAsync() {
    // Arrange
    var builder = CreateBuilder();
    var table = new TableDefinition(
      Name: "test_table",
      Columns: ImmutableArray.Create(
        new ColumnDefinition("id", WhizbangDataType.UUID, PrimaryKey: true, Nullable: false)
      )
    );
    var prefix = "wh_";

    // Act
    var sql = builder.BuildCreateTable(table, prefix);

    // Assert
    await Assert.That(sql).IsNotEmpty();
    await Assert.That(sql).Contains("wh_test_table");
    await Assert.That(sql).Contains("id");
  }

  [Test]
  public async Task BuildCreateTable_WithMultipleColumns_IncludesAllColumnsAsync() {
    // Arrange
    var builder = CreateBuilder();
    var table = new TableDefinition(
      Name: "users",
      Columns: ImmutableArray.Create(
        new ColumnDefinition("id", WhizbangDataType.UUID, PrimaryKey: true, Nullable: false),
        new ColumnDefinition("name", WhizbangDataType.STRING, MaxLength: 255, Nullable: false),
        new ColumnDefinition("age", WhizbangDataType.INTEGER, Nullable: true)
      )
    );
    var prefix = "wh_";

    // Act
    var sql = builder.BuildCreateTable(table, prefix);

    // Assert
    await Assert.That(sql).Contains("id");
    await Assert.That(sql).Contains("name");
    await Assert.That(sql).Contains("age");
  }

  [Test]
  public async Task BuildCreateTable_WithNullableColumn_SupportsNullAsync() {
    // Arrange
    var builder = CreateBuilder();
    var table = new TableDefinition(
      Name: "test",
      Columns: ImmutableArray.Create(
        new ColumnDefinition("id", WhizbangDataType.UUID, PrimaryKey: true, Nullable: false),
        new ColumnDefinition("optional_field", WhizbangDataType.STRING, Nullable: true)
      )
    );
    var prefix = "wh_";

    // Act
    var sql = builder.BuildCreateTable(table, prefix);

    // Assert
    await Assert.That(sql).Contains("optional_field");
  }

  [Test]
  public async Task BuildCreateIndex_SimpleIndex_GeneratesValidDDLAsync() {
    // Arrange
    var builder = CreateBuilder();
    var index = new IndexDefinition(
      Name: "idx_users_email",
      Columns: ImmutableArray.Create("email")
    );
    var tableName = "users";
    var prefix = "wh_";

    // Act
    var sql = builder.BuildCreateIndex(index, tableName, prefix);

    // Assert
    await Assert.That(sql).IsNotEmpty();
    await Assert.That(sql).Contains("idx_users_email");
    await Assert.That(sql).Contains("email");
  }

  [Test]
  public async Task BuildCreateIndex_CompositeIndex_IncludesAllColumnsAsync() {
    // Arrange
    var builder = CreateBuilder();
    var index = new IndexDefinition(
      Name: "idx_events_type_created",
      Columns: ImmutableArray.Create("event_type", "created_at")
    );
    var tableName = "events";
    var prefix = "wh_";

    // Act
    var sql = builder.BuildCreateIndex(index, tableName, prefix);

    // Assert
    await Assert.That(sql).Contains("event_type");
    await Assert.That(sql).Contains("created_at");
  }

  [Test]
  public async Task BuildCreateSequence_SimpleSequence_GeneratesValidDDLAsync() {
    // Arrange
    var builder = CreateBuilder();
    var sequence = new SequenceDefinition("event_sequence");
    var prefix = "wh_";

    // Act
    var sql = builder.BuildCreateSequence(sequence, prefix);

    // Assert
    await Assert.That(sql).IsNotEmpty();
    await Assert.That(sql).Contains("wh_event_sequence");
  }

  [Test]
  public async Task BuildInfrastructureSchema_IncludesAllRequiredTablesAsync() {
    // Arrange
    var builder = CreateBuilder();
    var config = new SchemaConfiguration();

    // Act
    var sql = builder.BuildInfrastructureSchema(config);

    // Assert - Verify all core infrastructure tables are included
    await Assert.That(sql).Contains("wh_inbox");
    await Assert.That(sql).Contains("wh_outbox");
    await Assert.That(sql).Contains("wh_event_store");
    await Assert.That(sql).Contains("wh_service_instances");
    await Assert.That(sql).Contains("wh_active_streams");
    await Assert.That(sql).Contains("wh_partition_assignments");
    await Assert.That(sql).Contains("wh_message_deduplication");
    await Assert.That(sql).Contains("wh_receptor_processing");
    await Assert.That(sql).Contains("wh_perspective_checkpoints");
    await Assert.That(sql).Contains("wh_request_response");
    await Assert.That(sql).Contains("wh_sequences");
  }

  [Test]
  public async Task BuildInfrastructureSchema_IncludesAllRequiredIndexesAsync() {
    // Arrange
    var builder = CreateBuilder();
    var config = new SchemaConfiguration();

    // Act
    var sql = builder.BuildInfrastructureSchema(config);

    // Assert - Verify critical indexes exist
    await Assert.That(sql).Contains("idx_inbox_processed_at");
    await Assert.That(sql).Contains("idx_outbox_published_at");
    await Assert.That(sql).Contains("idx_event_store_aggregate");
  }

  [Test]
  public async Task BuildInfrastructureSchema_CustomPrefix_UsesCustomPrefixAsync() {
    // Arrange
    var builder = CreateBuilder();
    var config = new SchemaConfiguration(InfrastructurePrefix: "custom_");

    // Act
    var sql = builder.BuildInfrastructureSchema(config);

    // Assert
    await Assert.That(sql).Contains("custom_inbox");
    await Assert.That(sql).Contains("custom_outbox");
    await Assert.That(sql).Contains("custom_event_store");
  }

  [Test]
  public async Task BuildInfrastructureSchema_IsIdempotent_CanRunMultipleTimesAsync() {
    // Arrange
    var builder = CreateBuilder();
    var config = new SchemaConfiguration();

    // Act
    var sql1 = builder.BuildInfrastructureSchema(config);
    var sql2 = builder.BuildInfrastructureSchema(config);

    // Assert - Same config should produce identical SQL
    await Assert.That(sql1).IsEqualTo(sql2);
  }

  [Test]
  public async Task BuildPerspectiveTable_GeneratesValidDDLAsync() {
    // Arrange
    var builder = CreateBuilder();
    var tableName = "wh_per_product_dto";

    // Act
    var sql = builder.BuildPerspectiveTable(tableName);

    // Assert
    await Assert.That(sql).IsNotEmpty();
    await Assert.That(sql).Contains("wh_per_product_dto");
  }

  [Test]
  public async Task BuildPerspectiveTable_IncludesRequiredColumnsAsync() {
    // Arrange
    var builder = CreateBuilder();
    var tableName = "wh_per_test";

    // Act
    var sql = builder.BuildPerspectiveTable(tableName);

    // Assert - Perspective tables have fixed schema
    await Assert.That(sql).Contains("stream_id");
    await Assert.That(sql).Contains("data");
    await Assert.That(sql).Contains("version");
    await Assert.That(sql).Contains("updated_at");
  }
}
