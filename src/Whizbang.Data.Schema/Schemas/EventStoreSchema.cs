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
    /// <summary>
    /// Event-categorization bitmask (Slice 2'). Stores
    /// <c>Whizbang.Core.Messaging.EventFlags</c> as an INTEGER. Default
    /// 0 (no flags = ordinary per-stream event). Collective events set
    /// bit 0; composite events set bit 1. New categories ship by adding
    /// flag values — no schema migration required.
    /// </summary>
    public const string FLAGS = "flags";
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
        Nullable: false
,
        PrimaryKey: true),
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
        Nullable: false
,
        MaxLength: 500),
      new ColumnDefinition(
        Name: "event_type",
        DataType: WhizbangDataType.STRING,
        Nullable: false
,
        MaxLength: 500),
      // event_data/metadata are offloaded to wh_event_body and DROPPED from wh_event_store by the
      // full body-split migration (078). They belong in CREATE for fresh databases, but the base
      // schema ensure must NOT re-add them via ADD COLUMN IF NOT EXISTS on an existing (split) DB —
      // re-asserting a dropped NOT NULL column against a non-empty table fails with Postgres 23502.
      // BackfillExempt defers their lifecycle to the forward migrations.
      new ColumnDefinition(
        Name: "event_data",
        DataType: WhizbangDataType.JSON,
        Nullable: false,
        BackfillExempt: true
      ),
      new ColumnDefinition(
        Name: "metadata",
        DataType: WhizbangDataType.JSON,
        Nullable: false,
        BackfillExempt: true
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
      ),
      new ColumnDefinition(
        Name: Columns.FLAGS,
        DataType: WhizbangDataType.INTEGER,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(0)
      )
    ),
    Indexes:

    [
      new IndexDefinition(
            Name: "idx_event_store_stream",
            Columns: [Columns.STREAM_ID, Columns.VERSION],
            Unique: true
          ),
      new IndexDefinition(
        Name: "idx_event_store_aggregate",
        Columns: [Columns.AGGREGATE_ID, Columns.VERSION],
        Unique: true
      ),
      new IndexDefinition(
        Name: "idx_event_store_aggregate_type",
        Columns: [Columns.AGGREGATE_TYPE, Columns.CREATED_AT]
      )
,
    ]);
}
