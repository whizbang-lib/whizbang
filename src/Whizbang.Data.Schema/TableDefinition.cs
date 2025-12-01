using System.Collections.Immutable;

namespace Whizbang.Data.Schema;

/// <summary>
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
