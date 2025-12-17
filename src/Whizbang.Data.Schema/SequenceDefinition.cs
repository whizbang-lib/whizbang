namespace Whizbang.Data.Schema;

/// <summary>
/// <docs>extensibility/database-schema-framework</docs>
/// Defines a database sequence for generating sequential values.
/// Uses record with structural equality (critical for incremental generators).
/// PostgreSQL: CREATE SEQUENCE name START WITH start INCREMENT BY increment
/// SQLite: Not supported (sequences are emulated via tables)
/// </summary>
/// <param name="Name">Sequence name (without prefix)</param>
/// <param name="StartValue">Starting value for the sequence (default: 1)</param>
/// <param name="IncrementBy">Increment step (default: 1)</param>
public sealed record SequenceDefinition(
  string Name,
  long StartValue = 1,
  int IncrementBy = 1
);
