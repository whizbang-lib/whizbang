namespace Whizbang.Data.Schema;

/// <summary>
/// Interface for database-specific schema builders.
/// Implementations generate DDL (Data Definition Language) from database-agnostic schema definitions.
/// Each database engine (Postgres, SQLite, MySQL, etc.) must implement this interface.
///
/// Pattern:
/// 1. Read schema definitions from Whizbang.Data.Schema.Schemas (InboxSchema, OutboxSchema, etc.)
/// 2. Transform to database-specific DDL syntax
/// 3. Return complete SQL scripts
///
/// Consumers:
/// - EF Core generators (AOT-compatible schema initialization)
/// - Dapper implementations (embedded schema resources)
/// - CLI tools (schema generation/validation)
/// - Tests (schema verification)
/// </summary>
/// <docs>data-access/schema-generation-pattern</docs>
public interface ISchemaBuilder {
  /// <summary>
  /// Database engine name (e.g., "Postgres", "SQLite", "MySQL").
  /// </summary>
  string DatabaseEngine { get; }

  /// <summary>
  /// Builds CREATE TABLE DDL for a single table.
  /// </summary>
  /// <param name="table">Table definition (database-agnostic)</param>
  /// <param name="prefix">Table name prefix (e.g., "wh_" or "wh_per_")</param>
  /// <param name="schema">Optional schema name (e.g., "inventory", "bff"). If null, no schema qualification is used.</param>
  /// <returns>Complete CREATE TABLE statement with all columns and inline constraints</returns>
  string BuildCreateTable(TableDefinition table, string prefix, string? schema = null);

  /// <summary>
  /// Builds CREATE INDEX DDL for a single index.
  /// </summary>
  /// <param name="index">Index definition (database-agnostic)</param>
  /// <param name="tableName">Table name without prefix</param>
  /// <param name="prefix">Table name prefix</param>
  /// <param name="schema">Optional schema name (e.g., "inventory", "bff"). If null, no schema qualification is used.</param>
  /// <returns>Complete CREATE INDEX statement</returns>
  string BuildCreateIndex(IndexDefinition index, string tableName, string prefix, string? schema = null);

  /// <summary>
  /// Builds CREATE SEQUENCE DDL for a single sequence.
  /// </summary>
  /// <param name="sequence">Sequence definition</param>
  /// <param name="prefix">Sequence name prefix</param>
  /// <param name="schema">Optional schema name (e.g., "inventory", "bff"). If null, no schema qualification is used.</param>
  /// <returns>Complete CREATE SEQUENCE statement</returns>
  string BuildCreateSequence(SequenceDefinition sequence, string prefix, string? schema = null);

  /// <summary>
  /// Builds complete infrastructure schema DDL.
  /// Includes all infrastructure tables (Inbox, Outbox, EventStore, etc.) with indexes and sequences.
  /// This is the AUTHORITATIVE method - all consumers MUST use this for consistency.
  /// </summary>
  /// <param name="config">Schema configuration (prefixes, options)</param>
  /// <returns>Complete DDL script for all infrastructure tables, indexes, and sequences</returns>
  string BuildInfrastructureSchema(SchemaConfiguration config);

  /// <summary>
  /// Builds perspective table DDL.
  /// Perspectives have fixed schema: id (PK), data (JSON), metadata (JSON), scope (JSON),
  /// created_at, updated_at, version.
  /// </summary>
  /// <param name="tableName">Full table name with prefix (e.g., "wh_per_product_dto")</param>
  /// <returns>Complete CREATE TABLE statement for perspective table</returns>
  string BuildPerspectiveTable(string tableName);
}
