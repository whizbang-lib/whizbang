using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the message_deduplication table (permanent deduplication tracking).
/// Table name: {prefix}message_deduplication (e.g., wh_message_deduplication)
/// Tracks all message IDs ever received for idempotent delivery guarantees (never deleted).
/// </summary>
public static class MessageDeduplicationSchema {
  /// <summary>
  /// Complete message_deduplication table definition.
  /// </summary>
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
  public static class Columns {
    public const string MessageId = "message_id";
    public const string FirstSeenAt = "first_seen_at";
  }
}
