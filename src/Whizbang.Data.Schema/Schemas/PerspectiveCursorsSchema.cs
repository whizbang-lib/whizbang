using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the perspective_cursors table (read model projection tracking).
/// Table name: {prefix}perspective_cursors (e.g., wh_perspective_cursors)
/// Tracks last processed event per stream per perspective for cursor-based processing.
/// Enables time-travel scenarios where perspectives catch up independently from event history.
/// </summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCursorsSchemaTests.cs</tests>
public static class PerspectiveCursorsSchema {
  /// <summary>
  /// Complete perspective_cursors table definition.
  /// NOTE: This table requires a composite primary key (stream_id, perspective_name).
  /// Current TableDefinition does not support composite PKs, so both columns are marked
  /// as PrimaryKey: true. Phase 2 generator template will add proper CONSTRAINT syntax.
  /// Foreign key constraint (last_event_id → wh_event_store.event_id) also deferred to Phase 2.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCursorsSchemaTests.cs:Table_HasCorrectNameAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCursorsSchemaTests.cs:Table_HasCorrectColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCursorsSchemaTests.cs:Table_StreamId_IsCompositePrimaryKeyAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCursorsSchemaTests.cs:Table_PerspectiveName_IsCompositePrimaryKeyAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCursorsSchemaTests.cs:Table_LastEventId_HasCorrectDefinitionAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCursorsSchemaTests.cs:Table_Status_HasCorrectDefaultAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCursorsSchemaTests.cs:Table_ProcessedAt_HasDateTimeDefaultAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCursorsSchemaTests.cs:Table_Error_IsNullableAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCursorsSchemaTests.cs:Table_HasCorrectIndexesAsync</tests>
  public static readonly TableDefinition Table = new(
    Name: "perspective_cursors",
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
        Name: "last_event_id",
        DataType: WhizbangDataType.UUID,
        Nullable: true  // Nullable - cursors start with no processed events
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
      ),
      new ColumnDefinition(
        Name: "rewind_trigger_event_id",
        DataType: WhizbangDataType.UUID,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "rewind_flagged_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "stream_lock_instance_id",
        DataType: WhizbangDataType.UUID,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "stream_lock_expiry",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "stream_lock_reason",
        DataType: WhizbangDataType.STRING,
        Nullable: true,
        MaxLength: 50
      )
    ),
    Indexes: [
      new IndexDefinition(
        Name: "idx_perspective_cursors_perspective_name",
        Columns: ["perspective_name"]
      ),
      new IndexDefinition(
        Name: "idx_perspective_cursors_last_event_id",
        Columns: ["last_event_id"]
      )
    ]
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/PerspectiveCursorsSchemaTests.cs:Columns_Constants_MatchColumnNamesAsync</tests>
  public static class Columns {
    public const string STREAM_ID = "stream_id";
    public const string PERSPECTIVE_NAME = "perspective_name";
    public const string LAST_EVENT_ID = "last_event_id";
    public const string STATUS = "status";
    public const string PROCESSED_AT = "processed_at";
    public const string ERROR = "error";
    public const string REWIND_TRIGGER_EVENT_ID = "rewind_trigger_event_id";
    public const string REWIND_FLAGGED_AT = "rewind_flagged_at";
    public const string STREAM_LOCK_INSTANCE_ID = "stream_lock_instance_id";
    public const string STREAM_LOCK_EXPIRY = "stream_lock_expiry";
    public const string STREAM_LOCK_REASON = "stream_lock_reason";
  }
}
