using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the outbox table (transactional messaging).
/// Table name: {prefix}outbox (e.g., wb_outbox)
/// Stores outgoing messages for reliable delivery with the transactional outbox pattern.
/// </summary>
public static class OutboxSchema {
  /// <summary>
  /// Complete outbox table definition.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: "outbox",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "message_id",
        DataType: WhizbangDataType.Uuid,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "destination",
        DataType: WhizbangDataType.String,
        MaxLength: 500,
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
        Name: "status",
        DataType: WhizbangDataType.String,
        MaxLength: 50,
        Nullable: false,
        DefaultValue: DefaultValue.String("Pending")
      ),
      new ColumnDefinition(
        Name: "attempts",
        DataType: WhizbangDataType.Integer,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(0)
      ),
      new ColumnDefinition(
        Name: "created_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
      ),
      new ColumnDefinition(
        Name: "published_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: true
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_outbox_status_created_at",
        Columns: ImmutableArray.Create("status", "created_at")
      ),
      new IndexDefinition(
        Name: "idx_outbox_published_at",
        Columns: ImmutableArray.Create("published_at")
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  public static class Columns {
    public const string MessageId = "message_id";
    public const string Destination = "destination";
    public const string EventType = "event_type";
    public const string EventData = "event_data";
    public const string Metadata = "metadata";
    public const string Scope = "scope";
    public const string Status = "status";
    public const string Attempts = "attempts";
    public const string CreatedAt = "created_at";
    public const string PublishedAt = "published_at";
  }
}
