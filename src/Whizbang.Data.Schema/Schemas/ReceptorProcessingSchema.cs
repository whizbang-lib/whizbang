using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the receptor_processing table (event handler tracking).
/// Table name: {prefix}receptor_processing (e.g., wh_receptor_processing)
/// Tracks which receptors have processed which events (log-style, many receptors per event).
/// Used for independent event handlers that don't require ordering.
/// </summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ReceptorProcessingSchemaTests.cs</tests>
public static class ReceptorProcessingSchema {
  /// <summary>
  /// Complete receptor_processing table definition.
  /// NOTE: Unique constraint (event_id, receptor_name) and foreign key constraints
  /// are not yet supported by TableDefinition and must be added in Phase 2.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ReceptorProcessingSchemaTests.cs:Table_HasCorrectNameAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ReceptorProcessingSchemaTests.cs:Table_HasCorrectColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ReceptorProcessingSchemaTests.cs:Table_Id_IsPrimaryKeyAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ReceptorProcessingSchemaTests.cs:Table_EventId_HasCorrectDefinitionAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ReceptorProcessingSchemaTests.cs:Table_ReceptorName_HasCorrectDefinitionAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ReceptorProcessingSchemaTests.cs:Table_Status_HasCorrectDefaultAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ReceptorProcessingSchemaTests.cs:Table_Attempts_HasCorrectDefaultAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ReceptorProcessingSchemaTests.cs:Table_Error_IsNullableAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ReceptorProcessingSchemaTests.cs:Table_StartedAt_HasDateTimeDefaultAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ReceptorProcessingSchemaTests.cs:Table_ProcessedAt_IsNullableAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ReceptorProcessingSchemaTests.cs:Table_HasCorrectIndexesAsync</tests>
  public static readonly TableDefinition Table = new(
    Name: "receptor_processing",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "id",
        DataType: WhizbangDataType.UUID,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "event_id",
        DataType: WhizbangDataType.UUID,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "receptor_name",
        DataType: WhizbangDataType.STRING,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "stream_id",
        DataType: WhizbangDataType.UUID,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "partition_number",
        DataType: WhizbangDataType.INTEGER,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "status",
        DataType: WhizbangDataType.SMALL_INT,
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
        Name: "instance_id",
        DataType: WhizbangDataType.UUID,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "lease_expiry",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "started_at",
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
        Name: "completed_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "processed_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_receptor_processing_event_id",
        Columns: ImmutableArray.Create("event_id")
      ),
      new IndexDefinition(
        Name: "idx_receptor_processing_receptor_name",
        Columns: ImmutableArray.Create("receptor_name")
      ),
      new IndexDefinition(
        Name: "idx_receptor_processing_status",
        Columns: ImmutableArray.Create("status")
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ReceptorProcessingSchemaTests.cs:Columns_Constants_MatchColumnNamesAsync</tests>
  public static class Columns {
    public const string ID = "id";
    public const string EVENT_ID = "event_id";
    public const string RECEPTOR_NAME = "receptor_name";
    public const string STREAM_ID = "stream_id";
    public const string PARTITION_NUMBER = "partition_number";
    public const string STATUS = "status";
    public const string ATTEMPTS = "attempts";
    public const string ERROR = "error";
    public const string INSTANCE_ID = "instance_id";
    public const string LEASE_EXPIRY = "lease_expiry";
    public const string STARTED_AT = "started_at";
    public const string CLAIMED_AT = "claimed_at";
    public const string COMPLETED_AT = "completed_at";
    public const string PROCESSED_AT = "processed_at";
  }
}
