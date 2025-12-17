using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// <docs>extensibility/database-schema-framework</docs>
/// Schema definition for partition assignments table (distributed work coordination).
/// Tracks which service instance owns which partition for consistent hashing-based work distribution.
/// Based on gold standard SQL: src/Whizbang.Data.EFCore.Postgres.Generators/Templates/Migrations/003_CreateServiceInstancesTable.sql
/// </summary>
public static class PartitionAssignmentsSchema {
  public const string TableName = "partition_assignments";

  // Column names as constants
  public const string PartitionNumber = "partition_number";
  public const string InstanceId = "instance_id";
  public const string AssignedAt = "assigned_at";
  public const string LastHeartbeat = "last_heartbeat";

  // Index names
  public const string IdxInstance = "idx_partition_assignments_instance";

  /// <summary>
  /// Complete table definition for partition assignments.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: TableName,
    Columns: [
      // partition_number INTEGER NOT NULL PRIMARY KEY
      new ColumnDefinition(
        Name: PartitionNumber,
        DataType: WhizbangDataType.Integer,
        Nullable: false,
        PrimaryKey: true,
        MaxLength: null,
        DefaultValue: null
      ),

      // instance_id UUID NOT NULL
      new ColumnDefinition(
        Name: InstanceId,
        DataType: WhizbangDataType.Uuid,
        Nullable: false,
        PrimaryKey: false,
        MaxLength: null,
        DefaultValue: null
      ),

      // assigned_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
      new ColumnDefinition(
        Name: AssignedAt,
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        PrimaryKey: false,
        MaxLength: null,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
      ),

      // last_heartbeat TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
      new ColumnDefinition(
        Name: LastHeartbeat,
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        PrimaryKey: false,
        MaxLength: null,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
      )
    ],
    Indexes: [
      // idx_partition_assignments_instance ON (instance_id, last_heartbeat)
      new IndexDefinition(
        Name: IdxInstance,
        Columns: ImmutableArray.Create(InstanceId, LastHeartbeat),
        Unique: false,
        WhereClause: null
      )
    ]
  );

  /// <summary>
  /// Gets all column definitions in order.
  /// </summary>
  public static ImmutableArray<ColumnDefinition> Columns => Table.Columns;

  /// <summary>
  /// Gets all index definitions.
  /// </summary>
  public static ImmutableArray<IndexDefinition> Indexes => Table.Indexes;
}
