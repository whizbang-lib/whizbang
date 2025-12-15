using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the message_deduplication table (permanent deduplication tracking).
/// Table name: {prefix}message_deduplication (e.g., wh_message_deduplication)
/// Tracks all message IDs ever received for idempotent delivery guarantees (never deleted).
/// </summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/MessageDeduplicationSchemaTests.cs</tests>
public static class MessageDeduplicationSchema {
  /// <summary>
  /// Complete message_deduplication table definition.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/MessageDeduplicationSchemaTests.cs:Table_ShouldHaveCorrectNameAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/MessageDeduplicationSchemaTests.cs:Table_ShouldDefineCorrectColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/MessageDeduplicationSchemaTests.cs:Table_ShouldDefinePrimaryKeyAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/MessageDeduplicationSchemaTests.cs:Table_ShouldDefineIndexesAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/MessageDeduplicationSchemaTests.cs:Table_FirstSeenAtIndex_ShouldBeDefinedAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/MessageDeduplicationSchemaTests.cs:Table_FirstSeenAtColumn_ShouldHaveDefaultValueAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/MessageDeduplicationSchemaTests.cs:Table_ShouldBeMinimalAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/MessageDeduplicationSchemaTests.cs:Table_MessageIdColumn_ShouldNotBeNullableAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/MessageDeduplicationSchemaTests.cs:Table_FirstSeenAtColumn_ShouldNotBeNullableAsync</tests>
  public static readonly TableDefinition Table = new(
    Name: "message_deduplication",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "message_id",
        DataType: WhizbangDataType.Uuid,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "first_seen_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_message_dedup_first_seen",
        Columns: ImmutableArray.Create("first_seen_at")
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/MessageDeduplicationSchemaTests.cs:Columns_ShouldProvideTypeConstantsAsync</tests>
  public static class Columns {
    public const string MessageId = "message_id";
    public const string FirstSeenAt = "first_seen_at";
  }
}
