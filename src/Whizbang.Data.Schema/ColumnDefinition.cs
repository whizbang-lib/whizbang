namespace Whizbang.Data.Schema;

/// <summary>
/// <docs>extending/extensibility/database-schema-framework</docs>
/// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_WithRequiredProperties_CreatesInstanceAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_WithoutOptionalProperties_UsesDefaultsAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_WithPrimaryKey_SetsPropertyAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_WithNullable_SetsPropertyAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_WithUnique_SetsPropertyAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_WithMaxLength_SetsPropertyAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_WithDefaultValue_SetsPropertyAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_WithAllProperties_SetsAllAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_SameValues_AreEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_DifferentName_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_DifferentDataType_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_IsRecordAsync</tests>
/// Defines a table column with database-agnostic types.
/// Uses record with structural equality (critical for incremental generators).
/// </summary>
/// <param name="Name">Column name (snake_case by convention)</param>
/// <param name="DataType">Database-agnostic data type</param>
/// <param name="Nullable">True if column allows NULL values (default: false)</param>
/// <param name="PrimaryKey">True if column is primary key (default: false)</param>
/// <param name="Unique">True if column values must be unique (default: false)</param>
/// <param name="MaxLength">Maximum length for string types (null = unlimited)</param>
/// <param name="DefaultValue">Default value expression (null = no default)</param>
/// <param name="BackfillExempt">
/// True if this column's lifecycle is owned by forward migrations rather than the base schema
/// (default: false). The column is still emitted in <c>CREATE TABLE</c> for fresh databases, but is
/// omitted from the idempotent <c>ALTER TABLE … ADD COLUMN IF NOT EXISTS</c> backfill pass. Set this
/// on columns a later migration may drop or relax (e.g. the event-store's offloaded
/// <c>event_data</c>/<c>metadata</c>, dropped by the full body split): re-asserting a dropped NOT NULL
/// column against a non-empty table fails with Postgres 23502, so the base ensure must defer to the
/// migrations and only move forward.
/// </param>
public sealed record ColumnDefinition(
  string Name,
  WhizbangDataType DataType,
  bool Nullable = false,
  bool PrimaryKey = false,
  bool Unique = false,
  int? MaxLength = null,
  DefaultValue? DefaultValue = null,
  bool BackfillExempt = false
);
