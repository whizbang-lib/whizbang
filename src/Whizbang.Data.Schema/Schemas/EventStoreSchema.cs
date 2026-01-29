using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the event_store table (event sourcing).
/// Table name: {prefix}event_store (e.g., wb_event_store)
/// Stores domain events for event sourcing and audit trail.
/// </summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/EventStoreSchemaTests.cs</tests>
public static class EventStoreSchema {
  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/EventStoreSchemaTests.cs:Columns_ShouldProvideAllConstantsAsync</tests>
  public static class Columns {
    public const string EVENT_ID = "event_id";
    public const string STREAM_ID = "stream_id";
    public const string AGGREGATE_ID = "aggregate_id";
    public const string AGGREGATE_TYPE = "aggregate_type";
    public const string EVENT_TYPE = "event_type";
    public const string EVENT_DATA = "event_data";
    public const string METADATA = "metadata";
    public const string SCOPE = "scope";
    public const string VERSION = "version";
    public const string CREATED_AT = "created_at";
  }

  /// <summary>
  /// Complete event_store table definition.
  /// Includes stream_id for stream-based event sourcing and scope for security/tenant context.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/EventStoreSchemaTests.cs:Table_ShouldHaveCorrectNameAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/EventStoreSchemaTests.cs:Table_ShouldDefineCorrectColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/EventStoreSchemaTests.cs:Table_ShouldDefineCorrectIndexesAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/EventStoreSchemaTests.cs:Table_ShouldHavePrimaryKeyAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/EventStoreSchemaTests.cs:Table_ShouldHaveUniqueAggregateVersionIndexAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/EventStoreSchemaTests.cs:Table_ColumnDefaults_ShouldBeCorrectAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/EventStoreSchemaTests.cs:Table_StreamIdColumn_ShouldBeCorrectAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/EventStoreSchemaTests.cs:Table_ScopeColumn_ShouldBeCorrectAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/EventStoreSchemaTests.cs:Table_ShouldHaveUniqueStreamVersionIndexAsync</tests>
  public static readonly TableDefinition Table = new(
    Name: "event_store",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "event_id",
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
        Name: "aggregate_id",
        DataType: WhizbangDataType.UUID,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "aggregate_type",
        DataType: WhizbangDataType.STRING,
        MaxLength: 500,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "event_type",
        DataType: WhizbangDataType.STRING,
        MaxLength: 500,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "event_data",
        DataType: WhizbangDataType.JSON,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "metadata",
        DataType: WhizbangDataType.JSON,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "scope",
        DataType: WhizbangDataType.JSON,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: Columns.VERSION,
        DataType: WhizbangDataType.INTEGER,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "created_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_event_store_stream",
        Columns: ImmutableArray.Create(Columns.STREAM_ID, Columns.VERSION),
        Unique: true
      ),
      new IndexDefinition(
        Name: "idx_event_store_aggregate",
        Columns: ImmutableArray.Create(Columns.AGGREGATE_ID, Columns.VERSION),
        Unique: true
      ),
      new IndexDefinition(
        Name: "idx_event_store_aggregate_type",
        Columns: ImmutableArray.Create(Columns.AGGREGATE_TYPE, Columns.CREATED_AT)
      )
    )
  );
}
