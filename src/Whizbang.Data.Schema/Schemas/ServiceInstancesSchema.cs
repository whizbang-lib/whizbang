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
  /// Column name constants for type-safe access.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/Schemas/ServiceInstancesSchemaTests.cs:Columns_ShouldProvideAllConstantsAsync</tests>
  public static class Columns {
    public const string INSTANCE_ID = "instance_id";
    public const string SERVICE_NAME = "service_name";
    public const string HOST_NAME = "host_name";
    public const string PROCESS_ID = "process_id";
    public const string STARTED_AT = "started_at";
    public const string LAST_HEARTBEAT_AT = "last_heartbeat_at";
    public const string METADATA = "metadata";
  }

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
        DataType: WhizbangDataType.UUID,
        PrimaryKey: true,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "service_name",
        DataType: WhizbangDataType.STRING,
        MaxLength: 200,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "host_name",
        DataType: WhizbangDataType.STRING,
        MaxLength: 200,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "process_id",
        DataType: WhizbangDataType.INTEGER,
        Nullable: false
      ),
      new ColumnDefinition(
        Name: "started_at",
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      ),
      new ColumnDefinition(
        Name: Columns.LAST_HEARTBEAT_AT,
        DataType: WhizbangDataType.TIMESTAMP_TZ,
        Nullable: false,
        DefaultValue: DefaultValue.Function(DefaultValueFunction.DATE_TIME__NOW)
      ),
      new ColumnDefinition(
        Name: "metadata",
        DataType: WhizbangDataType.JSON,
        Nullable: true
      )
    ),
    Indexes: ImmutableArray.Create(
      new IndexDefinition(
        Name: "idx_service_instances_service_name",
        Columns: ImmutableArray.Create(Columns.SERVICE_NAME, Columns.LAST_HEARTBEAT_AT)
      ),
      new IndexDefinition(
        Name: "idx_service_instances_heartbeat",
        Columns: ImmutableArray.Create(Columns.LAST_HEARTBEAT_AT)
      )
    )
  );
}
