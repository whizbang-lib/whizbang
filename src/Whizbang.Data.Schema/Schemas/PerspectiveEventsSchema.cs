using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for wh_perspective_events table.
/// Tracks perspective work items (events that need to be processed by perspectives).
/// Supports ordered processing per stream/perspective combination and work claiming.
/// </summary>
public static class PerspectiveEventsSchema {
  public const string TABLE_NAME = "perspective_events";

  /// <summary>
  /// Complete perspective_events table definition.
  /// Work tracking table for perspective processing with lease-based claiming.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: TABLE_NAME,
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "event_work_id",
        DataType: WhizbangDataType.UUID,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "stream_id",
        DataType: WhizbangDataType.UUID,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "perspective_name",
        DataType: WhizbangDataType.STRING,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "event_id",
        DataType: WhizbangDataType.UUID,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "sequence_number",
        DataType: WhizbangDataType.BIG_INT,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "instance_id",
        DataType: WhizbangDataType.UUID,
        Nullable: true  // NULL indicates unclaimed work
      ),
      new ColumnDefinition(
        Name: "lease_expiry",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true  // NULL indicates no lease
      ),
      new ColumnDefinition(
        Name: "status",
        DataType: WhizbangDataType.INTEGER,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(0)
      ),
      new ColumnDefinition(
        Name: "attempts",
        DataType: WhizbangDataType.INTEGER,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(0)
      ),
      new ColumnDefinition(
        Name: "error",
        DataType: WhizbangDataType.STRING,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "created_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      ),
      new ColumnDefinition(
        Name: "claimed_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "processed_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "scheduled_for",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "failure_reason",
        DataType: WhizbangDataType.INTEGER,
        Nullable: true
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_perspective_event_claim",
        Columns: ImmutableArray.Create("instance_id", "lease_expiry", "scheduled_for"),
        WhereClause: "processed_at IS NULL"
      ),
      new IndexDefinition(
        Name: "idx_perspective_event_order",
        Columns: ImmutableArray.Create("stream_id", "perspective_name", "sequence_number")
      ),
      new IndexDefinition(
        Name: "idx_perspective_event_stream",
        Columns: ImmutableArray.Create("stream_id")
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  public static class Columns {
    public const string EVENT_WORK_ID = "event_work_id";
    public const string STREAM_ID = "stream_id";
    public const string PERSPECTIVE_NAME = "perspective_name";
    public const string EVENT_ID = "event_id";
    public const string SEQUENCE_NUMBER = "sequence_number";
    public const string INSTANCE_ID = "instance_id";
    public const string LEASE_EXPIRY = "lease_expiry";
    public const string STATUS = "status";
    public const string ATTEMPTS = "attempts";
    public const string ERROR = "error";
    public const string CREATED_AT = "created_at";
    public const string CLAIMED_AT = "claimed_at";
    public const string PROCESSED_AT = "processed_at";
    public const string SCHEDULED_FOR = "scheduled_for";
    public const string FAILURE_REASON = "failure_reason";
  }
}
