using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the inbox table (deduplication and idempotency).
/// Table name: {prefix}inbox (e.g., wb_inbox)
/// Stores incoming messages to prevent duplicate processing.
/// </summary>
public static class InboxSchema {
  /// <summary>
  /// Complete inbox table definition.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: "inbox",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "message_id",
        DataType: WhizbangDataType.Uuid,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "event_type",
        DataType: WhizbangDataType.String,
        MaxLength: 500,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "event_data",
        DataType: WhizbangDataType.Json,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "metadata",
        DataType: WhizbangDataType.Json,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "scope",
        DataType: WhizbangDataType.Json,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "processed_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "received_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_inbox_processed_at",
        Columns: ImmutableArray.Create("processed_at")
      ),
      new IndexDefinition(
        Name: "idx_inbox_received_at",
        Columns: ImmutableArray.Create("received_at")
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  public static class Columns {
    public const string MessageId = "message_id";
    public const string EventType = "event_type";
    public const string EventData = "event_data";
    public const string Metadata = "metadata";
    public const string Scope = "scope";
    public const string ProcessedAt = "processed_at";
    public const string ReceivedAt = "received_at";
  }
}
