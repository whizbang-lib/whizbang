using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the perspective_checkpoints table (read model projection tracking).
/// Table name: {prefix}perspective_checkpoints (e.g., wh_perspective_checkpoints)
/// Tracks last processed event per stream per perspective for checkpoint-based processing.
/// Enables time-travel scenarios where perspectives catch up independently from event history.
/// </summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCheckpointsSchemaTests.cs</tests>
public static class PerspectiveCheckpointsSchema {
  /// <summary>
  /// Complete perspective_checkpoints table definition.
  /// NOTE: This table requires a composite primary key (stream_id, perspective_name).
  /// Current TableDefinition does not support composite PKs, so both columns are marked
  /// as PrimaryKey: true. Phase 2 generator template will add proper CONSTRAINT syntax.
  /// Foreign key constraint (last_event_id → wh_event_store.event_id) also deferred to Phase 2.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCheckpointsSchemaTests.cs:Table_HasCorrectNameAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCheckpointsSchemaTests.cs:Table_HasCorrectColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCheckpointsSchemaTests.cs:Table_StreamId_IsCompositePrimaryKeyAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCheckpointsSchemaTests.cs:Table_PerspectiveName_IsCompositePrimaryKeyAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCheckpointsSchemaTests.cs:Table_LastEventId_HasCorrectDefinitionAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCheckpointsSchemaTests.cs:Table_Status_HasCorrectDefaultAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCheckpointsSchemaTests.cs:Table_ProcessedAt_HasDateTimeDefaultAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCheckpointsSchemaTests.cs:Table_Error_IsNullableAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCheckpointsSchemaTests.cs:Table_HasCorrectIndexesAsync</tests>
  public static readonly TableDefinition Table = new(
    Name: "perspective_checkpoints",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "stream_id",
        DataType: WhizbangDataType.UUID,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "perspective_name",
        DataType: WhizbangDataType.STRING,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "last_event_id",
        DataType: WhizbangDataType.UUID,
        Nullable: true  // Nullable - checkpoints start with no processed events
      ),
      new ColumnDefinition(
        Name: "status",
        DataType: WhizbangDataType.SMALL_INT,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(0)
      ),
      new ColumnDefinition(
        Name: "processed_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      ),
      new ColumnDefinition(
        Name: "error",
        DataType: WhizbangDataType.STRING,
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
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCheckpointsSchemaTests.cs:Columns_Constants_MatchColumnNamesAsync</tests>
  public static class Columns {
    public const string STREAM_ID = "stream_id";
    public const string PERSPECTIVE_NAME = "perspective_name";
    public const string LAST_EVENT_ID = "last_event_id";
    public const string STATUS = "status";
    public const string PROCESSED_AT = "processed_at";
    public const string ERROR = "error";
  }
}
