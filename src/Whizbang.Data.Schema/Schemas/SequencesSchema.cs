using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the sequences table (distributed sequence generation).
/// Table name: {prefix}sequences (e.g., wb_sequences)
/// Provides named sequence generators for distributed systems.
/// </summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/SequencesSchemaTests.cs</tests>
public static class SequencesSchema {
  /// <summary>
  /// Complete sequences table definition.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/SequencesSchemaTests.cs:Table_HasCorrectNameAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/SequencesSchemaTests.cs:Table_HasCorrectColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/SequencesSchemaTests.cs:Table_SequenceName_IsPrimaryKeyAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/SequencesSchemaTests.cs:Table_HasNoAdditionalIndexesAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/SequencesSchemaTests.cs:Table_CurrentValue_HasDefaultZeroAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/SequencesSchemaTests.cs:Table_IncrementBy_HasDefaultOneAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/SequencesSchemaTests.cs:Table_LastUpdatedAt_HasDefaultNowAsync</tests>
  public static readonly TableDefinition Table = new(
    Name: "sequences",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "sequence_name",
        DataType: WhizbangDataType.STRING,
        MaxLength: 200,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "current_value",
        DataType: WhizbangDataType.BIG_INT,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(0)
      ),
      new ColumnDefinition(
        Name: "increment_by",
        DataType: WhizbangDataType.INTEGER,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(1)
      ),
      new ColumnDefinition(
        Name: "last_updated_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      )
    ),
    Indexes: ImmutableArray.Create<IndexDefinition>() // Primary key only, no additional indexes
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/SequencesSchemaTests.cs:Columns_HasAllConstantsAsync</tests>
  public static class Columns {
    public const string SEQUENCE_NAME = "sequence_name";
    public const string CURRENT_VALUE = "current_value";
    public const string INCREMENT_BY = "increment_by";
    public const string LAST_UPDATED_AT = "last_updated_at";
  }
}
