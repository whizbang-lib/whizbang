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
    /// <summary>
    /// Event-categorization bitmask (Slice 2'). Consumer side of the
    /// producer stamp on <c>OutboxSchema</c> — preserved by the transport
    /// consumer worker (Slice 3') so the projection runner can branch on
    /// individual flag bits without re-deserializing the payload.
    /// Stores <c>Whizbang.Core.Messaging.EventFlags</c> as an INTEGER.
    /// </summary>
    public const string FLAGS = "flags";
    public const string RECEIVED_AT = "received_at";
    /// <summary>
    /// The row's effective priority (priority step 1): one integer, lower is more urgent, 150 is the standard
    /// band a row nothing classified lands in. The claim orders streams by it.
    /// </summary>
    public const string PRIORITY = "priority";
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
        Nullable: false
,
        PrimaryKey: true),
      new ColumnDefinition(
        Name: "handler_name",
        DataType: WhizbangDataType.STRING,
        Nullable: false
,
        MaxLength: 500),
      new ColumnDefinition(
        Name: "message_type",
        DataType: WhizbangDataType.STRING,
        Nullable: false
,
        MaxLength: 500),
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
      // BackfillExempt from here on marks a column the work-state split MOVED to wh_inbox_state.
      // It stays declared so a fresh database still gets it from CREATE TABLE and the cutover
      // migration has something to read the state out of, but the ensure must not re-assert it:
      // the ensure runs BEFORE the migrations on every startup, and an
      // ALTER TABLE ADD COLUMN IF NOT EXISTS would put the column back on the boot after the
      // migration dropped it, silently, because every one of these is either nullable or carries a
      // default and so is added without a table rewrite or an error. The result would be a wide
      // table of permanently NULL duplicates, which is the cost the split exists to remove.
      new ColumnDefinition(
        Name: "partition_number",
        DataType: WhizbangDataType.INTEGER,
        Nullable: true,
        BackfillExempt: true
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
        DefaultValue: DefaultValue.Integer(1),
        BackfillExempt: true
      ),
      new ColumnDefinition(
        Name: "attempts",
        DataType: WhizbangDataType.INTEGER,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(0),
        BackfillExempt: true
      ),
      new ColumnDefinition(
        Name: "error",
        DataType: WhizbangDataType.STRING,
        Nullable: true,
        BackfillExempt: true
      ),
      new ColumnDefinition(
        Name: "instance_id",
        DataType: WhizbangDataType.UUID,
        Nullable: true,
        BackfillExempt: true
      ),
      new ColumnDefinition(
        Name: Columns.LEASE_EXPIRY,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true,
        BackfillExempt: true
      ),
      new ColumnDefinition(
        Name: Columns.FAILURE_REASON,
        DataType: WhizbangDataType.INTEGER,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(99),
        BackfillExempt: true
      ),
      new ColumnDefinition(
        Name: Columns.SCHEDULED_FOR,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true,
        BackfillExempt: true
      ),
      new ColumnDefinition(
        Name: Columns.PROCESSED_AT,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true,
        BackfillExempt: true
      ),
      new ColumnDefinition(
        Name: Columns.RECEIVED_AT,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      ),
      new ColumnDefinition(
        Name: Columns.FLAGS,
        DataType: WhizbangDataType.INTEGER,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(0)
      ),
      new ColumnDefinition(
        Name: Columns.PRIORITY,
        DataType: WhizbangDataType.INTEGER,
        Nullable: false,
        DefaultValue: DefaultValue.Integer(150)
      )
    ),
    // Only indexes on columns this table still owns after the work-state split. Seven others used
    // to live here, keyed or filtered on the columns that moved, and every one of them had to go
    // rather than be exempted: there is no BackfillExempt for an index, and the ensure's
    // CREATE INDEX IF NOT EXISTS runs on every startup, so a declaration here re-creates the index
    // on the boot after the migration dropped it. Their equivalents live on wh_inbox_state, which is
    // where the columns they index now live.
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_inbox_received_at",
        Columns: [Columns.RECEIVED_AT]
      )
    )
  );
}
