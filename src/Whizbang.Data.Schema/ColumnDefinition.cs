namespace Whizbang.Data.Schema;

/// <summary>
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
public sealed record ColumnDefinition(
  string Name,
  WhizbangDataType DataType,
  bool Nullable = false,
  bool PrimaryKey = false,
  bool Unique = false,
  int? MaxLength = null,
  DefaultValue? DefaultValue = null
);
