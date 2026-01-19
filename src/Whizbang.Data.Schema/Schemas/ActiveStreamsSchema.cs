using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for wh_active_streams table.
/// Ephemeral coordination table tracking which instance owns each active stream.
/// Enables sticky assignment and cross-subsystem coordination (outbox/inbox/perspectives).
/// </summary>
public static class ActiveStreamsSchema {
  public const string TABLE_NAME = "active_streams";

  /// <summary>
  /// Complete active_streams table definition.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: TABLE_NAME,
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "stream_id",
        DataType: WhizbangDataType.UUID,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "partition_number",
        DataType: WhizbangDataType.INTEGER,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "assigned_instance_id",
        DataType: WhizbangDataType.UUID,
        Nullable: true  // NULL indicates orphaned stream (FK in migration)
      ),
      new ColumnDefinition(
        Name: "lease_expiry",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: true  // NULL indicates no lease
      ),
      new ColumnDefinition(
        Name: "created_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      ),
      new ColumnDefinition(
        Name: "last_activity_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_active_streams_instance",
        Columns: ImmutableArray.Create("assigned_instance_id"),
        WhereClause: "assigned_instance_id IS NOT NULL"
      ),
      new IndexDefinition(
        Name: "idx_active_streams_partition",
        Columns: ImmutableArray.Create("partition_number"),
        WhereClause: "assigned_instance_id IS NULL"
      ),
      new IndexDefinition(
        Name: "idx_active_streams_lease_expiry",
        Columns: ImmutableArray.Create("lease_expiry"),
        WhereClause: "lease_expiry IS NOT NULL"
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  public static class Columns {
    public const string STREAM_ID = "stream_id";
    public const string PARTITION_NUMBER = "partition_number";
    public const string ASSIGNED_INSTANCE_ID = "assigned_instance_id";
    public const string LEASE_EXPIRY = "lease_expiry";
    public const string CREATED_AT = "created_at";
    public const string LAST_ACTIVITY_AT = "last_activity_at";
  }
}
