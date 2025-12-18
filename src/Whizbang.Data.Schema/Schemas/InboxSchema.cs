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
        DataType: WhizbangDataType.Uuid,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "handler_name",
        DataType: WhizbangDataType.String,
        MaxLength: 500,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "event_type",
        DataType: WhizbangDataType.String,
        MaxLength: 500,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "event_data",
        DataType: WhizbangDataType.Json,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "metadata",
        DataType: WhizbangDataType.Json,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "scope",
        DataType: WhizbangDataType.Json,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "stream_id",
        DataType: WhizbangDataType.Uuid,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "partition_number",
        DataType: WhizbangDataType.Integer,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "is_event",
        DataType: WhizbangDataType.Boolean,
        Nullable: false,
        DefaultValue: DefaultValue.Boolean(false)
      ),
      new ColumnDefinition(
        Name: "status",
        DataType: WhizbangDataType.Integer,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(1)
      ),
      new ColumnDefinition(
        Name: "attempts",
        DataType: WhizbangDataType.Integer,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(0)
      ),
      new ColumnDefinition(
        Name: "error",
        DataType: WhizbangDataType.String,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "instance_id",
        DataType: WhizbangDataType.Uuid,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "lease_expiry",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "failure_reason",
        DataType: WhizbangDataType.Integer,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(99)
      ),
      new ColumnDefinition(
        Name: "scheduled_for",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "processed_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: true
      ),
      new ColumnDefinition(
        Name: "received_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_inbox_processed_at",
        Columns: ImmutableArray.Create("processed_at")
      ),
      new IndexDefinition(
        Name: "idx_inbox_received_at",
        Columns: ImmutableArray.Create("received_at")
      ),
      new IndexDefinition(
        Name: "idx_inbox_lease_expiry",
        Columns: ImmutableArray.Create("lease_expiry"),
        WhereClause: "lease_expiry IS NOT NULL"
      ),
      new IndexDefinition(
        Name: "idx_inbox_status_lease",
        Columns: ImmutableArray.Create("status", "lease_expiry"),
        WhereClause: "(status & 32768) = 0 AND (status & 2) != 2"
      ),
      new IndexDefinition(
        Name: "idx_inbox_failure_reason",
        Columns: ImmutableArray.Create("failure_reason"),
        WhereClause: "(status & 32768) = 32768"
      ),
      new IndexDefinition(
        Name: "idx_inbox_scheduled_for",
        Columns: ImmutableArray.Create("stream_id", "scheduled_for", "received_at"),
        WhereClause: "scheduled_for IS NOT NULL"
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/InboxSchemaTests.cs:Columns_ShouldProvideAllConstantsAsync</tests>
  public static class Columns {
    public const string MessageId = "message_id";
    public const string HandlerName = "handler_name";
    public const string EventType = "event_type";
    public const string EventData = "event_data";
    public const string Metadata = "metadata";
    public const string Scope = "scope";
    public const string StreamId = "stream_id";
    public const string PartitionNumber = "partition_number";
    public const string IsEvent = "is_event";
    public const string Status = "status";
    public const string Attempts = "attempts";
    public const string Error = "error";
    public const string InstanceId = "instance_id";
    public const string LeaseExpiry = "lease_expiry";
    public const string FailureReason = "failure_reason";
    public const string ScheduledFor = "scheduled_for";
    public const string ProcessedAt = "processed_at";
    public const string ReceivedAt = "received_at";
  }
}
