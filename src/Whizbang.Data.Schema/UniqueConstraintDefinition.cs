using System.Collections.Immutable;

namespace Whizbang.Data.Schema;

/// <summary>
/// Defines a unique constraint on one or more columns.
/// Uses record with structural equality (critical for incremental generators).
/// ImmutableArray provides value equality for collections.
/// </summary>
/// <param name="Name">Constraint name (e.g., "uq_message_association")</param>
/// <param name="Columns">Columns that form the unique constraint</param>
public sealed record UniqueConstraintDefinition(
  string Name,
  ImmutableArray<string> Columns
);
