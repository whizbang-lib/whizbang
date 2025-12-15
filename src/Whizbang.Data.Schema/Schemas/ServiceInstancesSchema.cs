using System.Collections.Immutable;

namespace Whizbang.Data.Schema.Schemas;

/// <summary>
/// Schema definition for the service_instances table (distributed work coordination).
/// Table name: {prefix}service_instances (e.g., wh_service_instances)
/// Tracks active service instances for distributed work coordination and partition assignment.
/// </summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ServiceInstancesSchemaTests.cs</tests>
public static class ServiceInstancesSchema {
  /// <summary>
  /// Complete service_instances table definition.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ServiceInstancesSchemaTests.cs:Table_HasCorrectNameAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ServiceInstancesSchemaTests.cs:Table_HasCorrectColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ServiceInstancesSchemaTests.cs:Table_InstanceId_IsPrimaryKeyAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ServiceInstancesSchemaTests.cs:Table_HasCorrectIndexesAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ServiceInstancesSchemaTests.cs:Table_ServiceNameIndex_HasCorrectColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ServiceInstancesSchemaTests.cs:Table_HeartbeatIndex_HasCorrectColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ServiceInstancesSchemaTests.cs:Table_StartedAt_HasDefaultNowAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ServiceInstancesSchemaTests.cs:Table_LastHeartbeatAt_HasDefaultNowAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ServiceInstancesSchemaTests.cs:Table_Metadata_IsNullableAsync</tests>
  public static readonly TableDefinition Table = new(
    Name: "service_instances",
    Columns: ImmutableArray.Create(
      new ColumnDefinition(
        Name: "instance_id",
        DataType: WhizbangDataType.Uuid,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "service_name",
        DataType: WhizbangDataType.String,
        MaxLength: 200,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "host_name",
        DataType: WhizbangDataType.String,
        MaxLength: 200,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "process_id",
        DataType: WhizbangDataType.Integer,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "started_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
      ),
      new ColumnDefinition(
        Name: "last_heartbeat_at",
        DataType: WhizbangDataType.TimestampTz,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DateTime_Now)
      ),
      new ColumnDefinition(
        Name: "metadata",
        DataType: WhizbangDataType.Json,
        Nullable: true
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_service_instances_service_name",
        Columns: ImmutableArray.Create("service_name", "last_heartbeat_at")
      ),
      new IndexDefinition(
        Name: "idx_service_instances_heartbeat",
        Columns: ImmutableArray.Create("last_heartbeat_at")
      )
    )
  );

  /// <summary>
  /// Column name constants for type-safe access.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ServiceInstancesSchemaTests.cs:Columns_HasAllConstantsAsync</tests>
  public static class Columns {
    public const string InstanceId = "instance_id";
    public const string ServiceName = "service_name";
    public const string HostName = "host_name";
    public const string ProcessId = "process_id";
    public const string StartedAt = "started_at";
    public const string LastHeartbeatAt = "last_heartbeat_at";
    public const string Metadata = "metadata";
  }
}
