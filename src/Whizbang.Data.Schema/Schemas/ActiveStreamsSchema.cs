using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for wh_active_streams table.
/// Ephemeral coordination table tracking which instance owns each active stream.
/// Enables sticky assignment and cross-subsystem coordination (outbox/inbox/perspectives).
/// </summary>
public static class ActiveStreamsSchema {
  public const string TableName = "active_streams";

  /// <summary>
  /// Complete active_streams table definition.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: TableName,
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "stream_id",
        DataType: WhizbangDataType.Uuid,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "partition_number",
        DataType: WhizbangDataType.Integer,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "assigned_instance_id",
        DataType: WhizbangDataType.Uuid,
        Nullable: true  // NULL indicates orphaned stream (FK in migration)
      ),
      new ColumnDefinition(
        Name: "lease_expiry",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: true  // NULL indicates no lease
      ),
      new ColumnDefinition(
        Name: "created_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
      ),
      new ColumnDefinition(
        Name: "updated_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
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
    public const string StreamId = "stream_id";
    public const string PartitionNumber = "partition_number";
    public const string AssignedInstanceId = "assigned_instance_id";
    public const string LeaseExpiry = "lease_expiry";
    public const string CreatedAt = "created_at";
    public const string UpdatedAt = "updated_at";
  }
}
