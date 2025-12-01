using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the event_store table (event sourcing).
/// Table name: {prefix}event_store (e.g., wb_event_store)
/// Stores domain events for event sourcing and audit trail.
/// </summary>
public static class EventStoreSchema {
  /// <summary>
  /// Complete event_store table definition.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: "event_store",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "event_id",
        DataType: WhizbangDataType.Uuid,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "aggregate_id",
        DataType: WhizbangDataType.Uuid,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "aggregate_type",
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
        Name: "sequence_number",
        DataType: WhizbangDataType.BigInt,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "version",
        DataType: WhizbangDataType.Integer,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "created_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_event_store_aggregate",
        Columns: ImmutableArray.Create("aggregate_id", "version"),
        Unique: true
      ),
      new IndexDefinition(
        Name: "idx_event_store_aggregate_type",
        Columns: ImmutableArray.Create("aggregate_type", "created_at")
      ),
      new IndexDefinition(
        Name: "idx_event_store_sequence",
        Columns: ImmutableArray.Create("sequence_number")
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  public static class Columns {
    public const string EventId = "event_id";
    public const string AggregateId = "aggregate_id";
    public const string AggregateType = "aggregate_type";
    public const string EventType = "event_type";
    public const string EventData = "event_data";
    public const string Metadata = "metadata";
    public const string SequenceNumber = "sequence_number";
    public const string Version = "version";
    public const string CreatedAt = "created_at";
  }
}
