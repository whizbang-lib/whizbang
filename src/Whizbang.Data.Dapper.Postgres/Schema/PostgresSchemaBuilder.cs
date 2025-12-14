using System.Text;
using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Dapper.Postgres.Schema;

/// <summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateTable_SimpleTable_GeneratesCreateStatementAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateTable_WithMultipleColumns_GeneratesAllColumnsAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateTable_WithDefaultValue_GeneratesDefaultClauseAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateTable_WithUniqueColumn_GeneratesUniqueConstraintAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateTable_PerspectivePrefix_UsesPerspectivePrefixAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateIndex_SimpleIndex_GeneratesCreateIndexAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateIndex_CompositeIndex_GeneratesMultiColumnIndexAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateIndex_UniqueIndex_GeneratesUniqueIndexAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildInfrastructureSchema_GeneratesAllTablesAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildInfrastructureSchema_GeneratesAllIndexesAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildInfrastructureSchema_InboxTable_HasCorrectStructureAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildInfrastructureSchema_OutboxTable_HasCorrectDefaultsAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildInfrastructureSchema_EventStoreTable_HasUniqueConstraintAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildInfrastructureSchema_CustomPrefix_UsesCustomPrefixAsync</tests>
/// Builds Postgres DDL (Data Definition Language) from database-agnostic schema definitions.
/// Generates CREATE TABLE and CREATE INDEX statements with proper Postgres syntax.
/// </summary>
public static class PostgresSchemaBuilder {
  /// <summary>
  /// Builds a CREATE TABLE statement for a single table definition.
  /// </summary>
  /// <param name="table">Table definition to convert to SQL</param>
  /// <param name="prefix">Table name prefix (e.g., "wb_" or "wb_per_")</param>
  /// <returns>Complete CREATE TABLE statement with all columns and constraints</returns>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateTable_SimpleTable_GeneratesCreateStatementAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateTable_WithMultipleColumns_GeneratesAllColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateTable_WithDefaultValue_GeneratesDefaultClauseAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateTable_WithUniqueColumn_GeneratesUniqueConstraintAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateTable_PerspectivePrefix_UsesPerspectivePrefixAsync</tests>
  public static string BuildCreateTable(TableDefinition table, string prefix) {
    var sb = new StringBuilder();
    var tableName = $"{prefix}{table.Name}";

    sb.AppendLine($"CREATE TABLE IF NOT EXISTS {tableName} (");

    var columnDefinitions = new List<string>();
    foreach (var column in table.Columns) {
      columnDefinitions.Add(BuildColumnDefinition(column));
    }

    sb.AppendLine(string.Join(",\n", columnDefinitions.Select(c => $"  {c}")));
    sb.AppendLine(");");

    return sb.ToString();
  }

  /// <summary>
  /// Builds a single column definition line.
  /// </summary>
  private static string BuildColumnDefinition(ColumnDefinition column) {
    var parts = new List<string>();

    // Column name and type
    var sqlType = PostgresTypeMapper.MapDataType(column.DataType, column.MaxLength);
    parts.Add($"{column.Name} {sqlType}");

    // Nullability
    parts.Add(column.Nullable ? "NULL" : "NOT NULL");

    // Default value
    if (column.DefaultValue is not null) {
      var defaultValue = PostgresTypeMapper.MapDefaultValue(column.DefaultValue);
      parts.Add($"DEFAULT {defaultValue}");
    }

    // Unique constraint
    if (column.Unique) {
      parts.Add("UNIQUE");
    }

    // Primary key
    if (column.PrimaryKey) {
      parts.Add("PRIMARY KEY");
    }

    return string.Join(" ", parts);
  }

  /// <summary>
  /// Builds a CREATE INDEX statement for a single index definition.
  /// </summary>
  /// <param name="index">Index definition to convert to SQL</param>
  /// <param name="tableName">Table name (without prefix)</param>
  /// <param name="prefix">Table name prefix (e.g., "wb_")</param>
  /// <returns>Complete CREATE INDEX statement</returns>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateIndex_SimpleIndex_GeneratesCreateIndexAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateIndex_CompositeIndex_GeneratesMultiColumnIndexAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateIndex_UniqueIndex_GeneratesUniqueIndexAsync</tests>
  public static string BuildCreateIndex(IndexDefinition index, string tableName, string prefix) {
    var fullTableName = $"{prefix}{tableName}";
    var unique = index.Unique ? "UNIQUE " : "";
    var columns = string.Join(", ", index.Columns);

    return $"CREATE {unique}INDEX IF NOT EXISTS {index.Name} ON {fullTableName} ({columns});";
  }

  /// <summary>
  /// Builds complete Postgres schema with all infrastructure tables and indexes.
  /// Generates SQL for inbox, outbox, event_store, request_response, and sequences tables.
  /// </summary>
  /// <param name="config">Schema configuration with prefix settings</param>
  /// <returns>Complete DDL script for all infrastructure tables</returns>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildInfrastructureSchema_GeneratesAllTablesAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildInfrastructureSchema_GeneratesAllIndexesAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildInfrastructureSchema_InboxTable_HasCorrectStructureAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildInfrastructureSchema_OutboxTable_HasCorrectDefaultsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildInfrastructureSchema_EventStoreTable_HasUniqueConstraintAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildInfrastructureSchema_CustomPrefix_UsesCustomPrefixAsync</tests>
  public static string BuildInfrastructureSchema(SchemaConfiguration config) {
    var sb = new StringBuilder();

    sb.AppendLine("-- Whizbang Infrastructure Schema for Postgres");
    sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    sb.AppendLine($"-- Infrastructure Prefix: {config.InfrastructurePrefix}");
    sb.AppendLine($"-- Perspective Prefix: {config.PerspectivePrefix}");
    sb.AppendLine();

    // Build all infrastructure tables
    var tables = new[] {
      (ServiceInstancesSchema.Table, "Service Instances - Distributed work coordination"),
      (MessageDeduplicationSchema.Table, "Message Deduplication - Permanent idempotency tracking"),
      (InboxSchema.Table, "Inbox - Message deduplication and idempotency"),
      (OutboxSchema.Table, "Outbox - Transactional messaging pattern"),
      (EventStoreSchema.Table, "Event Store - Event sourcing and audit trail"),
      (ReceptorProcessingSchema.Table, "Receptor Processing - Event handler tracking (log-style)"),
      (PerspectiveCheckpointsSchema.Table, "Perspective Checkpoints - Read model projection tracking (checkpoint-style)"),
      (RequestResponseSchema.Table, "Request/Response - Async request/response tracking"),
      (SequencesSchema.Table, "Sequences - Distributed sequence generation")
    };

    foreach (var (table, description) in tables) {
      sb.AppendLine($"-- {description}");
      sb.AppendLine(BuildCreateTable(table, config.InfrastructurePrefix));

      // Build indexes for this table
      foreach (var index in table.Indexes) {
        sb.AppendLine(BuildCreateIndex(index, table.Name, config.InfrastructurePrefix));
      }

      sb.AppendLine();
    }

    return sb.ToString();
  }
}
