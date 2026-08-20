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
    Columns:
    [
      new ColumnDefinition(
            Name: "message_id",
            DataType: WhizbangDataType.UUID,
            Nullable: false
,
            PrimaryKey: true),
      new ColumnDefinition(
        Name: "first_seen_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      )
,
      // Topology arc phase 8.5 — durable redelivery-observation counter (poison detection layer 2).
      // This table is already the store-side idempotency record for every message id ever received,
      // so it is where "how many times has the broker handed me this?" belongs; no new table, and
      // no extra round trip (store_inbox_messages already writes here on every delivery).
      // Deliberately NOT wh_inbox.attempts: that counts PROCESSING attempts on a claimed row and
      // feeds wh_dead_letters.attempts_when_dlq. Conflating redeliveries with attempts would
      // corrupt both signals.
      new ColumnDefinition(
        Name: "observation_count",
        DataType: WhizbangDataType.INTEGER,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(1)
      )
,
    ],
    Indexes: [new IndexDefinition(
        Name: "idx_message_dedup_first_seen",
        Columns: ["first_seen_at"]
      )]
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/MessageDeduplicationSchemaTests.cs:Columns_ShouldProvideTypeConstantsAsync</tests>
  public static class Columns {
    public const string MESSAGE_ID = "message_id";
    public const string FIRST_SEEN_AT = "first_seen_at";

    /// <summary>
    /// Durable redelivery-observation counter (topology arc phase 8.5). Incremented by
    /// <c>store_inbox_messages</c> on every delivery of an already-seen message id; poison
    /// detection layer 2 quarantines past the configured bound.
    /// </summary>
    public const string OBSERVATION_COUNT = "observation_count";
  }
}
