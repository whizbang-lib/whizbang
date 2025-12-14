using System.Collections.Immutable;

namespace Whizbang.Data.Schema;

/// <summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/TableDefinitionTests.cs:TableDefinition_WithRequiredProperties_CreatesInstanceAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/TableDefinitionTests.cs:TableDefinition_WithoutIndexes_UsesDefaultAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/TableDefinitionTests.cs:TableDefinition_WithIndexes_StoresAllAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/TableDefinitionTests.cs:TableDefinition_SameValues_AreEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/TableDefinitionTests.cs:TableDefinition_DifferentName_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/TableDefinitionTests.cs:TableDefinition_IsRecordAsync</tests>
/// Complete table definition including columns and indexes.
/// Uses record with structural equality (critical for incremental generators).
/// ImmutableArray provides value equality for collections.
/// </summary>
/// <param name="Name">Table name (without prefix - prefix added by schema builder)</param>
/// <param name="Columns">Column definitions</param>
/// <param name="Indexes">Index definitions (default: empty array)</param>
public sealed record TableDefinition(
  string Name,
  ImmutableArray<ColumnDefinition> Columns,
  ImmutableArray<IndexDefinition> Indexes = default
) {
  /// <summary>
  /// Indexes with default value coalescing to empty array.
  /// </summary>
  public ImmutableArray<IndexDefinition> Indexes { get; init; } = Indexes.IsDefault ? [] : Indexes;
}
