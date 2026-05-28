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
        Name: Columns.STREAM_ID,
        DataType: WhizbangDataType.UUID,
        Nullable: false,
        PrimaryKey: true
      ),
      new ColumnDefinition(
        Name: Columns.PERSPECTIVE_NAME,
        DataType: WhizbangDataType.STRING,
        Nullable: false,
        PrimaryKey: true
      ),
      new ColumnDefinition(
        Name: Columns.SNAPSHOT_EVENT_ID,
        DataType: WhizbangDataType.UUID,
        Nullable: false,
        PrimaryKey: true
      ),
      new ColumnDefinition(
        Name: Columns.SNAPSHOT_DATA,
        DataType: WhizbangDataType.JSON,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: Columns.SEQUENCE_NUMBER,
        DataType: WhizbangDataType.BIG_INT,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: Columns.CREATED_AT,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      ),
      // Slice 26.11 — commit_sequence as of this snapshot. NULL on existing rows; populated
      // by the runner template via IEventStore.GetCommitSequenceAsync. Mig 048 backfills
      // schema (ALTER + index) on existing JDX databases.
      new ColumnDefinition(
        Name: Columns.SNAPSHOT_COMMIT_SEQUENCE,
        DataType: WhizbangDataType.BIG_INT,
        Nullable: true
      )
    ),
    Indexes: [
      new IndexDefinition(
        Name: "idx_perspective_snapshots_lookup",
        Columns: [Columns.STREAM_ID, Columns.PERSPECTIVE_NAME, Columns.SEQUENCE_NUMBER]
      ),
      new IndexDefinition(
        Name: "idx_perspective_snapshots_commit_sequence",
        Columns: [Columns.STREAM_ID, Columns.PERSPECTIVE_NAME, Columns.SNAPSHOT_COMMIT_SEQUENCE],
        WhereClause: $"{Columns.SNAPSHOT_COMMIT_SEQUENCE} IS NOT NULL"
      )
      // This partial index works against the snapshot_commit_sequence column added
      // in migration 048. Existing databases that pre-date that migration get the
      // column backfilled by PostgresSchemaBuilder.BuildCreateTable's per-column
      // `ALTER TABLE ADD COLUMN IF NOT EXISTS` pass that runs BEFORE the CREATE
      // INDEX, so the index creation is safe on both fresh and upgraded DBs.
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
    /// <summary>Slice 26.11 — commit_sequence as of this snapshot (added via mig 048).</summary>
    public const string SNAPSHOT_COMMIT_SEQUENCE = "snapshot_commit_sequence";
  }
}
