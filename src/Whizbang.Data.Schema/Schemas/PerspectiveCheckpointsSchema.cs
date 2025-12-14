using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the perspective_checkpoints table (read model projection tracking).
/// Table name: {prefix}perspective_checkpoints (e.g., wh_perspective_checkpoints)
/// Tracks last processed event per stream per perspective for checkpoint-based processing.
/// Enables time-travel scenarios where perspectives catch up independently from event history.
/// </summary>
public static class PerspectiveCheckpointsSchema {
  /// <summary>
  /// Complete perspective_checkpoints table definition.
  /// NOTE: This table requires a composite primary key (stream_id, perspective_name).
  /// Current TableDefinition does not support composite PKs, so both columns are marked
  /// as PrimaryKey: true. Phase 2 generator template will add proper CONSTRAINT syntax.
  /// Foreign key constraint (last_event_id → wh_event_store.event_id) also deferred to Phase 2.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: "perspective_checkpoints",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "stream_id",
        DataType: WhizbangDataType.Uuid,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "perspective_name",
        DataType: WhizbangDataType.String,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "last_event_id",
        DataType: WhizbangDataType.Uuid,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "status",
        DataType: WhizbangDataType.SmallInt,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(0)
      ),
      new ColumnDefinition(
        Name: "processed_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
      ),
      new ColumnDefinition(
        Name: "error",
        DataType: WhizbangDataType.String,
        Nullable: true
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_perspective_checkpoints_perspective_name",
        Columns: ImmutableArray.Create("perspective_name")
      ),
      new IndexDefinition(
        Name: "idx_perspective_checkpoints_last_event_id",
        Columns: ImmutableArray.Create("last_event_id")
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  public static class Columns {
    public const string StreamId = "stream_id";
    public const string PerspectiveName = "perspective_name";
    public const string LastEventId = "last_event_id";
    public const string Status = "status";
    public const string ProcessedAt = "processed_at";
    public const string Error = "error";
  }
}
