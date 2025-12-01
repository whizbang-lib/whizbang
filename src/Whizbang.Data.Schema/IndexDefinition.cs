using System.Collections.Immutable;

namespace Whizbang.Data.Schema;

/// <summary>
/// Defines a table index.
/// Uses record with structural equality (critical for incremental generators).
/// ImmutableArray provides value equality for the Columns collection.
/// </summary>
/// <param name="Name">Index name</param>
/// <param name="Columns">Column names to include in the index</param>
/// <param name="Unique">True if index enforces uniqueness (default: false)</param>
public sealed record IndexDefinition(
  string Name,
  ImmutableArray<string> Columns,
  bool Unique = false
);
