using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the perspective_snapshots table.
/// Table name: {prefix}perspective_snapshots (e.g., wh_perspective_snapshots)
/// Stores periodic snapshots of perspective state for efficient rewind after late-arriving events.
/// Each snapshot captures the full model state at a specific event, enabling replay from that point
/// instead of replaying from event zero.
/// </summary>
public static class PerspectiveSnapshotsSchema {
  /// <summary>
  /// Complete perspective_snapshots table definition.
  /// Composite primary key: (stream_id, perspective_name, snapshot_event_id).
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: "perspective_snapshots",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "stream_id",
        DataType: WhizbangDataType.UUID,
        Nullable: false,
        PrimaryKey: true
      ),
      new ColumnDefinition(
        Name: "perspective_name",
        DataType: WhizbangDataType.STRING,
        Nullable: false,
        PrimaryKey: true
      ),
      new ColumnDefinition(
        Name: "snapshot_event_id",
        DataType: WhizbangDataType.UUID,
        Nullable: false,
        PrimaryKey: true
      ),
      new ColumnDefinition(
        Name: "snapshot_data",
        DataType: WhizbangDataType.JSON,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "sequence_number",
        DataType: WhizbangDataType.BIG_INT,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "created_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      )
    ),
    Indexes: [
      new IndexDefinition(
        Name: "idx_perspective_snapshots_lookup",
        Columns: ["stream_id", "perspective_name", "sequence_number"]
      )
    ]
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  public static class Columns {
    public const string STREAM_ID = "stream_id";
    public const string PERSPECTIVE_NAME = "perspective_name";
    public const string SNAPSHOT_EVENT_ID = "snapshot_event_id";
    public const string SNAPSHOT_DATA = "snapshot_data";
    public const string SEQUENCE_NUMBER = "sequence_number";
    public const string CREATED_AT = "created_at";
  }
}
