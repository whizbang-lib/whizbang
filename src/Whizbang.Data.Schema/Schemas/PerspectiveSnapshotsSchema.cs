// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
      // schema (ALTER + index) on existing consumer databases.
      new ColumnDefinition(
        Name: Columns.SNAPSHOT_COMMIT_SEQUENCE,
        DataType: WhizbangDataType.BIG_INT,
        Nullable: true
      ),
      // The stream version this snapshot was taken at, which is what places it in the stream. Commit
      // sequence is assignment order and inverts against the stream for retroactively stamped rows, so
      // it cannot answer "is this snapshot before that event". NULL on rows written before migration 209;
      // selection treats a NULL as no usable snapshot and replays further rather than guessing.
      new ColumnDefinition(
        Name: Columns.SNAPSHOT_VERSION,
        DataType: WhizbangDataType.INTEGER,
        Nullable: true
      ),
      // How far an archive of the stream reaches, when one covers this snapshot (#1738): a rewind from it, or a full
      // rebuild, reads through the archive. NULL when nothing the snapshot needs is archived; a restore clears it. Mig 271
      // adds the column on existing databases.
      new ColumnDefinition(
        Name: Columns.ARCHIVED_THROUGH_VERSION,
        DataType: WhizbangDataType.BIG_INT,
        Nullable: true
      ),
      // The commit sequence through which the row's collectives were complete when the snapshot was taken (#1963): a
      // replay from it folds the collectives past it. NULL on a snapshot taken before the mark existed, which keeps the
      // snapshot-event rule. Not indexed: read with the snapshot, never searched by. Mig 275 adds it on existing databases.
      new ColumnDefinition(
        Name: Columns.COLLECTIVES_THROUGH_SEQUENCE,
        DataType: WhizbangDataType.BIG_INT,
        Nullable: true
      )
    ),
    Indexes: [
      new IndexDefinition(
        Name: "idx_perspective_snapshots_lookup",
        Columns: [Columns.STREAM_ID, Columns.PERSPECTIVE_NAME, Columns.SEQUENCE_NUMBER]
      ),
      // These partial indexes work against columns added after the table (048, 209). A database that
      // pre-dates either one gets the column backfilled by PostgresSchemaBuilder.BuildCreateTable's
      // per-column `ALTER TABLE ADD COLUMN IF NOT EXISTS` pass, which runs BEFORE the CREATE INDEX, so
      // index creation is safe on both fresh and upgraded databases.
      new IndexDefinition(
        Name: "idx_perspective_snapshots_commit_sequence",
        Columns: [Columns.STREAM_ID, Columns.PERSPECTIVE_NAME, Columns.SNAPSHOT_COMMIT_SEQUENCE],
        WhereClause: $"{Columns.SNAPSHOT_COMMIT_SEQUENCE} IS NOT NULL"
      ),
      new IndexDefinition(
        Name: "idx_perspective_snapshots_version",
        Columns: [Columns.STREAM_ID, Columns.PERSPECTIVE_NAME, Columns.SNAPSHOT_VERSION],
        WhereClause: $"{Columns.SNAPSHOT_VERSION} IS NOT NULL"
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
    /// <summary>Slice 26.11 — commit_sequence as of this snapshot (added via mig 048).</summary>
    public const string SNAPSHOT_COMMIT_SEQUENCE = "snapshot_commit_sequence";
    /// <summary>The stream version this snapshot was taken at, which is what orders it against the stream (added via mig 209).</summary>
    public const string SNAPSHOT_VERSION = "snapshot_version";
    /// <summary>How far an archive of the stream reaches, when one covers this snapshot (#1738).</summary>
    public const string ARCHIVED_THROUGH_VERSION = "archived_through_version";
    /// <summary>The commit sequence through which the row's collectives were complete when the snapshot was taken (#1963).</summary>
    public const string COLLECTIVES_THROUGH_SEQUENCE = "collectives_through_sequence";
  }
}
