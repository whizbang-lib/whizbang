using System.Collections.Immutable;

namespace Whizbang.Data.Schema;

/// <summary>
/// <docs>extensibility/database-schema-framework</docs>
/// <tests>tests/Whizbang.Data.Schema.Tests/IndexDefinitionTests.cs:IndexDefinition_WithRequiredProperties_CreatesInstanceAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/IndexDefinitionTests.cs:IndexDefinition_WithoutOptionalProperties_UsesDefaultsAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/IndexDefinitionTests.cs:IndexDefinition_WithMultipleColumns_StoresAllAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/IndexDefinitionTests.cs:IndexDefinition_WithUnique_SetsPropertyAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/IndexDefinitionTests.cs:IndexDefinition_SameValues_HasMatchingPropertiesAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/IndexDefinitionTests.cs:IndexDefinition_DifferentName_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/IndexDefinitionTests.cs:IndexDefinition_DifferentColumns_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/IndexDefinitionTests.cs:IndexDefinition_DifferentUnique_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/IndexDefinitionTests.cs:IndexDefinition_IsRecordAsync</tests>
/// Defines a table index.
/// Uses record with structural equality (critical for incremental generators).
/// ImmutableArray provides value equality for the Columns collection.
/// </summary>
/// <param name="Name">Index name</param>
/// <param name="Columns">Column names to include in the index</param>
/// <param name="Unique">True if index enforces uniqueness (default: false)</param>
/// <param name="WhereClause">Optional WHERE clause for partial indexes (e.g., "scheduled_for IS NOT NULL")</param>
public sealed record IndexDefinition(
  string Name,
  ImmutableArray<string> Columns,
  bool Unique = false,
  string? WhereClause = null
);
