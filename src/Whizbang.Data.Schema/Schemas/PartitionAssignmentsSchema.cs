using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// <docs>extensibility/database-schema-framework</docs>
/// Schema definition for partition assignments table (distributed work coordination).
/// Tracks which service instance owns which partition for consistent hashing-based work distribution.
/// Based on gold standard SQL: src/Whizbang.Data.EFCore.Postgres.Generators/Templates/Migrations/003_CreateServiceInstancesTable.sql
/// </summary>
public static class PartitionAssignmentsSchema {
  public const string TABLE_NAME = "partition_assignments";

  // Column names as constants
  public const string PARTITION_NUMBER = "partition_number";
  public const string INSTANCE_ID = "instance_id";
  public const string ASSIGNED_AT = "assigned_at";
  public const string LAST_HEARTBEAT = "last_heartbeat";

  // Index names
  public const string IDX_INSTANCE = "idx_partition_assignments_instance";

  /// <summary>
  /// Complete table definition for partition assignments.
  /// </summary>
  public static readonly TableDefinition Table = new(
    Name: TABLE_NAME,
    Columns: [
      // partition_number INTEGER NOT NULL PRIMARY KEY
      new ColumnDefinition(
        Name: PARTITION_NUMBER,
        DataType: WhizbangDataType.INTEGER,
        Nullable: false,
        PrimaryKey: true,
        MaxLength: null,
        DefaultValue: null
      ),

      // instance_id UUID NOT NULL
      new ColumnDefinition(
        Name: INSTANCE_ID,
        DataType: WhizbangDataType.UUID,
        Nullable: false,
        PrimaryKey: false,
        MaxLength: null,
        DefaultValue: null
      ),

      // assigned_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
      new ColumnDefinition(
        Name: ASSIGNED_AT,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        PrimaryKey: false,
        MaxLength: null,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      ),

      // last_heartbeat TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
      new ColumnDefinition(
        Name: LAST_HEARTBEAT,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        PrimaryKey: false,
        MaxLength: null,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      )
    ],
    Indexes: [
      // idx_partition_assignments_instance ON (instance_id, last_heartbeat)
      new IndexDefinition(
        Name: IDX_INSTANCE,
        Columns: ImmutableArray.Create(INSTANCE_ID, LAST_HEARTBEAT),
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
