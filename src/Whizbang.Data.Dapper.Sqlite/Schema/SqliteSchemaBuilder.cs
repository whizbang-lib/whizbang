using System.Text;
using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Dapper.Sqlite.Schema;

/// <summary>
/// Builds SQLite DDL (Data Definition Language) from database-agnostic schema definitions.
/// Generates CREATE TABLE and CREATE INDEX statements with proper SQLite syntax.
///
/// SQLite DDL Notes:
/// - Supports IF NOT EXISTS clause for idempotent schema creation
/// - Type affinity system (TEXT, INTEGER, REAL, BLOB, NULL)
/// - AUTOINCREMENT only for INTEGER PRIMARY KEY
/// - No VARCHAR(n) enforcement (all become TEXT)
/// - UNIQUE constraints supported at column and table level
/// </summary>
/// <docs>data-access/schema-generation-pattern</docs>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateTable_SimpleTable_GeneratesCreateStatementAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateTable_WithMultipleColumns_GeneratesAllColumnsAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateTable_WithDefaultValue_GeneratesDefaultClauseAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateTable_WithUniqueColumn_GeneratesUniqueConstraintAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateTable_PerspectivePrefix_UsesPerspectivePrefixAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateIndex_SimpleIndex_GeneratesCreateIndexAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateIndex_CompositeIndex_GeneratesMultiColumnIndexAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateIndex_UniqueIndex_GeneratesUniqueIndexAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildInfrastructureSchema_GeneratesAllTablesAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildInfrastructureSchema_GeneratesAllIndexesAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildInfrastructureSchema_InboxTable_HasCorrectStructureAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildInfrastructureSchema_OutboxTable_HasCorrectDefaultsAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildInfrastructureSchema_EventStoreTable_HasUniqueConstraintAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildInfrastructureSchema_CustomPrefix_UsesCustomPrefixAsync</tests>
public class SqliteSchemaBuilder : ISchemaBuilder {
  /// <inheritdoc />
  public string DatabaseEngine => "SQLite";

  /// <summary>
  /// Singleton instance for easy static access (backward compatibility).
  /// </summary>
  public static readonly SqliteSchemaBuilder Instance = new();
  /// <summary>
  /// Builds a CREATE TABLE statement for a single table definition.
  /// </summary>
  /// <param name="table">Table definition to convert to SQL</param>
  /// <param name="prefix">Table name prefix (e.g., "wb_" or "wb_per_")</param>
  /// <returns>Complete CREATE TABLE statement with all columns and constraints</returns>
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateTable_SimpleTable_GeneratesCreateStatementAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateTable_WithMultipleColumns_GeneratesAllColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateTable_WithDefaultValue_GeneratesDefaultClauseAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateTable_WithUniqueColumn_GeneratesUniqueConstraintAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateTable_PerspectivePrefix_UsesPerspectivePrefixAsync</tests>
  public string BuildCreateTable(TableDefinition table, string prefix) {
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
    var sqlType = SqliteTypeMapper.MapDataType(column.DataType, column.MaxLength);
    parts.Add($"{column.Name} {sqlType}");

    // Nullability
    parts.Add(column.Nullable ? "NULL" : "NOT NULL");

    // Default value
    if (column.DefaultValue is not null) {
      var defaultValue = SqliteTypeMapper.MapDefaultValue(column.DefaultValue);
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
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateIndex_SimpleIndex_GeneratesCreateIndexAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateIndex_CompositeIndex_GeneratesMultiColumnIndexAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildCreateIndex_UniqueIndex_GeneratesUniqueIndexAsync</tests>
  public string BuildCreateIndex(IndexDefinition index, string tableName, string prefix) {
    var fullTableName = $"{prefix}{tableName}";
    var unique = index.Unique ? "UNIQUE " : "";
    var columns = string.Join(", ", index.Columns);

    return $"CREATE {unique}INDEX IF NOT EXISTS {index.Name} ON {fullTableName} ({columns});";
  }

  /// <summary>
  /// Builds complete SQLite schema with all infrastructure tables and indexes.
  /// Generates SQL for inbox, outbox, event_store, request_response, and sequences tables.
  /// </summary>
  /// <param name="config">Schema configuration with prefix settings</param>
  /// <returns>Complete DDL script for all infrastructure tables</returns>
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildInfrastructureSchema_GeneratesAllTablesAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildInfrastructureSchema_GeneratesAllIndexesAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildInfrastructureSchema_InboxTable_HasCorrectStructureAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildInfrastructureSchema_OutboxTable_HasCorrectDefaultsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildInfrastructureSchema_EventStoreTable_HasUniqueConstraintAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/SqliteSchemaBuilderTests.cs:BuildInfrastructureSchema_CustomPrefix_UsesCustomPrefixAsync</tests>
  public string BuildInfrastructureSchema(SchemaConfiguration config) {
    var sb = new StringBuilder();

    sb.AppendLine("-- Whizbang Infrastructure Schema for SQLite");
    sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    sb.AppendLine($"-- Infrastructure Prefix: {config.InfrastructurePrefix}");
    sb.AppendLine($"-- Perspective Prefix: {config.PerspectivePrefix}");
    sb.AppendLine();
    sb.AppendLine("-- SQLite Type Notes:");
    sb.AppendLine("--   UUIDs: TEXT (hex format)");
    sb.AppendLine("--   JSON: TEXT (use JSON1 extension for querying)");
    sb.AppendLine("--   Timestamps: TEXT (ISO8601 format)");
    sb.AppendLine("--   Booleans: INTEGER (0 = false, 1 = true)");
    sb.AppendLine();

    // Build all infrastructure tables
    var tables = new[] {
      (InboxSchema.Table, "Inbox - Message deduplication and idempotency"),
      (OutboxSchema.Table, "Outbox - Transactional messaging pattern"),
      (EventStoreSchema.Table, "Event Store - Event sourcing and audit trail"),
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

  /// <summary>
  /// Builds a CREATE SEQUENCE statement for a single sequence definition.
  /// SQLite does not have native sequences - this method returns an empty string as a no-op.
  /// Use AUTOINCREMENT with INTEGER PRIMARY KEY for auto-incrementing columns instead.
  /// </summary>
  /// <param name="sequence">Sequence definition (ignored for SQLite)</param>
  /// <param name="prefix">Sequence name prefix (ignored for SQLite)</param>
  /// <returns>Empty string (SQLite doesn't support sequences)</returns>
  public string BuildCreateSequence(SequenceDefinition sequence, string prefix) {
    // SQLite doesn't support CREATE SEQUENCE - use AUTOINCREMENT instead
    return string.Empty;
  }

  /// <summary>
  /// Builds a CREATE TABLE statement for a perspective table.
  /// Perspectives have fixed schema: stream_id (TEXT PK), data (TEXT/JSON), version (INTEGER), updated_at (TEXT/ISO8601).
  /// </summary>
  /// <param name="tableName">Full table name with prefix (e.g., "wh_per_product_dto")</param>
  /// <returns>Complete CREATE TABLE statement for perspective table</returns>
  public string BuildPerspectiveTable(string tableName) {
    var sb = new StringBuilder();

    sb.AppendLine($"CREATE TABLE IF NOT EXISTS {tableName} (");
    sb.AppendLine("  stream_id TEXT NOT NULL PRIMARY KEY,");
    sb.AppendLine("  data TEXT NOT NULL,");
    sb.AppendLine("  version INTEGER NOT NULL,");
    sb.AppendLine("  updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP");
    sb.AppendLine(");");

    return sb.ToString();
  }
}
