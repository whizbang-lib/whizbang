using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the outbox table (transactional messaging).
/// Table name: {prefix}outbox (e.g., wb_outbox)
/// Stores outgoing messages for reliable delivery with the transactional outbox pattern.
/// </summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/OutboxSchemaTests.cs</tests>
public static class OutboxSchema {
  /// <summary>
  /// Complete outbox table definition.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/OutboxSchemaTests.cs:Table_ShouldHaveCorrectNameAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/OutboxSchemaTests.cs:Table_ShouldDefineCorrectColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/OutboxSchemaTests.cs:Table_ShouldDefineCorrectIndexesAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/OutboxSchemaTests.cs:Table_ShouldHavePrimaryKeyAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/OutboxSchemaTests.cs:Table_ColumnDefaults_ShouldBeCorrectAsync</tests>
  public static readonly TableDefinition Table = new(
    Name: "outbox",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "message_id",
        DataType: WhizbangDataType.UUID,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "destination",
        DataType: WhizbangDataType.STRING,
        MaxLength: 500,
        Nullable: true  // Events don't have destinations, only commands do
      ),
      new ColumnDefinition(
        Name: "message_type",
        DataType: WhizbangDataType.STRING,
        MaxLength: 500,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "envelope_type",
        DataType: WhizbangDataType.STRING,
        MaxLength: 500,
        Nullable: true
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
        Name: "stream_id",
        DataType: WhizbangDataType.UUID,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "partition_number",
        DataType: WhizbangDataType.INTEGER,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "is_event",
        DataType: WhizbangDataType.BOOLEAN,
        Nullable: false,
        DefaultValue: DefaultValue.Boolean(false)
      ),
      new ColumnDefinition(
        Name: "status",
        DataType: WhizbangDataType.INTEGER,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(1)
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
        Name: "failure_reason",
        DataType: WhizbangDataType.INTEGER,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(99)
      ),
      new ColumnDefinition(
        Name: "scheduled_for",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "created_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      ),
      new ColumnDefinition(
        Name: "published_at",
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
        Name: "idx_outbox_status_created_at",
        Columns: ImmutableArray.Create("status", "created_at")
      ),
      new IndexDefinition(
        Name: "idx_outbox_published_at",
        Columns: ImmutableArray.Create("published_at")
      ),
      new IndexDefinition(
        Name: "idx_outbox_lease_expiry",
        Columns: ImmutableArray.Create("lease_expiry"),
        WhereClause: "lease_expiry IS NOT NULL"
      ),
      new IndexDefinition(
        Name: "idx_outbox_status_lease",
        Columns: ImmutableArray.Create("status", "lease_expiry"),
        WhereClause: "(status & 32768) = 0 AND (status & 4) != 4"
      ),
      new IndexDefinition(
        Name: "idx_outbox_failure_reason",
        Columns: ImmutableArray.Create("failure_reason"),
        WhereClause: "(status & 32768) = 32768"
      ),
      new IndexDefinition(
        Name: "idx_outbox_scheduled_for",
        Columns: ImmutableArray.Create("stream_id", "scheduled_for", "created_at"),
        WhereClause: "scheduled_for IS NOT NULL"
      ),
      new IndexDefinition(
        Name: "idx_outbox_partition_claiming",
        Columns: ImmutableArray.Create("partition_number", "scheduled_for", "created_at"),
        WhereClause: "(status & 4) != 4 AND (status & 32768) = 0"
      ),
      new IndexDefinition(
        Name: "idx_outbox_instance_lease",
        Columns: ImmutableArray.Create("instance_id", "lease_expiry"),
        WhereClause: "instance_id IS NOT NULL AND lease_expiry IS NOT NULL"
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/OutboxSchemaTests.cs:Columns_ShouldProvideAllConstantsAsync</tests>
  public static class Columns {
    public const string MESSAGE_ID = "message_id";
    public const string DESTINATION = "destination";
    public const string MESSAGE_TYPE = "message_type";
    public const string ENVELOPE_TYPE = "envelope_type";
    public const string EVENT_DATA = "event_data";
    public const string METADATA = "metadata";
    public const string SCOPE = "scope";
    public const string STREAM_ID = "stream_id";
    public const string PARTITION_NUMBER = "partition_number";
    public const string IS_EVENT = "is_event";
    public const string STATUS = "status";
    public const string ATTEMPTS = "attempts";
    public const string ERROR = "error";
    public const string INSTANCE_ID = "instance_id";
    public const string LEASE_EXPIRY = "lease_expiry";
    public const string FAILURE_REASON = "failure_reason";
    public const string SCHEDULED_FOR = "scheduled_for";
    public const string CREATED_AT = "created_at";
    public const string PUBLISHED_AT = "published_at";
    public const string PROCESSED_AT = "processed_at";
  }
}
