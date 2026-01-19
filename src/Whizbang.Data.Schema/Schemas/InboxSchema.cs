using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the inbox table (deduplication and idempotency).
/// Table name: {prefix}inbox (e.g., wb_inbox)
/// Stores incoming messages to prevent duplicate processing.
/// </summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/InboxSchemaTests.cs</tests>
public static class InboxSchema {
  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/InboxSchemaTests.cs:Columns_ShouldProvideAllConstantsAsync</tests>
  public static class Columns {
    public const string MESSAGE_ID = "message_id";
    public const string HANDLER_NAME = "handler_name";
    public const string MESSAGE_TYPE = "message_type";
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
    public const string PROCESSED_AT = "processed_at";
    public const string RECEIVED_AT = "received_at";
  }

  /// <summary>
  /// Complete inbox table definition.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/InboxSchemaTests.cs:Table_ShouldHaveCorrectNameAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/InboxSchemaTests.cs:Table_ShouldDefineCorrectColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/InboxSchemaTests.cs:Table_ShouldDefineCorrectIndexesAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/InboxSchemaTests.cs:Table_ShouldHavePrimaryKeyAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/InboxSchemaTests.cs:Table_ColumnDefaults_ShouldBeCorrectAsync</tests>
  public static readonly TableDefinition Table = new(
    Name: "inbox",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "message_id",
        DataType: WhizbangDataType.UUID,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "handler_name",
        DataType: WhizbangDataType.STRING,
        MaxLength: 500,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "message_type",
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
        Name: Columns.LEASE_EXPIRY,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: Columns.FAILURE_REASON,
        DataType: WhizbangDataType.INTEGER,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(99)
      ),
      new ColumnDefinition(
        Name: Columns.SCHEDULED_FOR,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: Columns.PROCESSED_AT,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: Columns.RECEIVED_AT,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_inbox_processed_at",
        Columns: ImmutableArray.Create(Columns.PROCESSED_AT)
      ),
      new IndexDefinition(
        Name: "idx_inbox_received_at",
        Columns: ImmutableArray.Create(Columns.RECEIVED_AT)
      ),
      new IndexDefinition(
        Name: "idx_inbox_lease_expiry",
        Columns: ImmutableArray.Create(Columns.LEASE_EXPIRY),
        WhereClause: "lease_expiry IS NOT NULL"
      ),
      new IndexDefinition(
        Name: "idx_inbox_status_lease",
        Columns: ImmutableArray.Create(Columns.STATUS, Columns.LEASE_EXPIRY),
        WhereClause: "(status & 32768) = 0 AND (status & 2) != 2"
      ),
      new IndexDefinition(
        Name: "idx_inbox_failure_reason",
        Columns: ImmutableArray.Create(Columns.FAILURE_REASON),
        WhereClause: "(status & 32768) = 32768"
      ),
      new IndexDefinition(
        Name: "idx_inbox_scheduled_for",
        Columns: ImmutableArray.Create(Columns.STREAM_ID, Columns.SCHEDULED_FOR, Columns.RECEIVED_AT),
        WhereClause: "scheduled_for IS NOT NULL"
      ),
      new IndexDefinition(
        Name: "idx_inbox_partition_claiming",
        Columns: ImmutableArray.Create(Columns.PARTITION_NUMBER, Columns.SCHEDULED_FOR, Columns.RECEIVED_AT),
        WhereClause: "(status & 2) != 2 AND (status & 32768) = 0"
      ),
      new IndexDefinition(
        Name: "idx_inbox_instance_lease",
        Columns: ImmutableArray.Create(Columns.INSTANCE_ID, Columns.LEASE_EXPIRY),
        WhereClause: "instance_id IS NOT NULL AND lease_expiry IS NOT NULL"
      )
    )
  );
}
