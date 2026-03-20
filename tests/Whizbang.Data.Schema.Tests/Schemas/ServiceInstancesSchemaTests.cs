using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for ServiceInstancesSchema - distributed work coordination table schema.
/// Tests verify table definition structure, columns, types, constraints, and indexes.
/// </summary>

public class ServiceInstancesSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectNameAsync() {
    // Arrange & Act
    var tableName = ServiceInstancesSchema.Table.Name;

    // Assert
    await Assert.That(tableName).IsEqualTo("service_instances");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectColumnsAsync() {
    // Arrange & Act
    var columns = ServiceInstancesSchema.Table.Columns;

    // Assert - Verify column count
    await Assert.That(columns).Count().IsEqualTo(7);

    // Verify each column definition
    var instanceId = columns[0];
    await Assert.That(instanceId.Name).IsEqualTo("instance_id");
    await Assert.That(instanceId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(instanceId.PrimaryKey).IsTrue();
    await Assert.That(instanceId.Nullable).IsFalse();

    var serviceName = columns[1];
    await Assert.That(serviceName.Name).IsEqualTo("service_name");
    await Assert.That(serviceName.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(serviceName.MaxLength).IsEqualTo(200);
    await Assert.That(serviceName.Nullable).IsFalse();

    var hostName = columns[2];
    await Assert.That(hostName.Name).IsEqualTo("host_name");
    await Assert.That(hostName.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(hostName.MaxLength).IsEqualTo(200);
    await Assert.That(hostName.Nullable).IsFalse();

    var processId = columns[3];
    await Assert.That(processId.Name).IsEqualTo("process_id");
    await Assert.That(processId.DataType).IsEqualTo(WhizbangDataType.INTEGER);
    await Assert.That(processId.Nullable).IsFalse();

    var startedAt = columns[4];
    await Assert.That(startedAt.Name).IsEqualTo("started_at");
    await Assert.That(startedAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(startedAt.Nullable).IsFalse();

    var lastHeartbeatAt = columns[5];
    await Assert.That(lastHeartbeatAt.Name).IsEqualTo("last_heartbeat_at");
    await Assert.That(lastHeartbeatAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(lastHeartbeatAt.Nullable).IsFalse();

    var metadata = columns[6];
    await Assert.That(metadata.Name).IsEqualTo("metadata");
    await Assert.That(metadata.DataType).IsEqualTo(WhizbangDataType.JSON);
    await Assert.That(metadata.Nullable).IsTrue();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_InstanceId_IsPrimaryKeyAsync() {
    // Arrange & Act
    var columns = ServiceInstancesSchema.Table.Columns;
    var primaryKeyColumn = columns.FirstOrDefault(c => c.PrimaryKey);

    // Assert
    await Assert.That(primaryKeyColumn).IsNotNull();
    await Assert.That(primaryKeyColumn!.Name).IsEqualTo("instance_id");
    await Assert.That(primaryKeyColumn.DataType).IsEqualTo(WhizbangDataType.UUID);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectIndexesAsync() {
    // Arrange & Act
    var indexes = ServiceInstancesSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).Count().IsEqualTo(2);

    // Verify service name index
    var serviceNameIndex = indexes[0];
    await Assert.That(serviceNameIndex.Name).IsEqualTo("idx_service_instances_service_name");
    await Assert.That(serviceNameIndex.Columns).Count().IsEqualTo(2);
    await Assert.That(serviceNameIndex.Columns[0]).IsEqualTo("service_name");
    await Assert.That(serviceNameIndex.Columns[1]).IsEqualTo("last_heartbeat_at");

    // Verify heartbeat index
    var heartbeatIndex = indexes[1];
    await Assert.That(heartbeatIndex.Name).IsEqualTo("idx_service_instances_heartbeat");
    await Assert.That(heartbeatIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(heartbeatIndex.Columns[0]).IsEqualTo("last_heartbeat_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ServiceNameIndex_HasCorrectColumnsAsync() {
    // Arrange & Act
    var indexes = ServiceInstancesSchema.Table.Indexes;
    var serviceNameIndex = indexes.First(i => i.Name == "idx_service_instances_service_name");

    // Assert
    await Assert.That(serviceNameIndex.Name).IsEqualTo("idx_service_instances_service_name");
    await Assert.That(serviceNameIndex.Columns).Count().IsEqualTo(2);
    await Assert.That(serviceNameIndex.Columns[0]).IsEqualTo("service_name");
    await Assert.That(serviceNameIndex.Columns[1]).IsEqualTo("last_heartbeat_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HeartbeatIndex_HasCorrectColumnsAsync() {
    // Arrange & Act
    var indexes = ServiceInstancesSchema.Table.Indexes;
    var heartbeatIndex = indexes.First(i => i.Name == "idx_service_instances_heartbeat");

    // Assert
    await Assert.That(heartbeatIndex.Name).IsEqualTo("idx_service_instances_heartbeat");
    await Assert.That(heartbeatIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(heartbeatIndex.Columns[0]).IsEqualTo("last_heartbeat_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_StartedAt_HasDefaultNowAsync() {
    // Arrange & Act
    var columns = ServiceInstancesSchema.Table.Columns;
    var startedAtColumn = columns.First(c => c.Name == "started_at");

    // Assert
    await Assert.That(startedAtColumn.DefaultValue).IsNotNull();
    await Assert.That(startedAtColumn.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)startedAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DATE_TIME__NOW);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_LastHeartbeatAt_HasDefaultNowAsync() {
    // Arrange & Act
    var columns = ServiceInstancesSchema.Table.Columns;
    var lastHeartbeatAtColumn = columns.First(c => c.Name == "last_heartbeat_at");

    // Assert
    await Assert.That(lastHeartbeatAtColumn.DefaultValue).IsNotNull();
    await Assert.That(lastHeartbeatAtColumn.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)lastHeartbeatAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DATE_TIME__NOW);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_Metadata_IsNullableAsync() {
    // Arrange & Act
    var columns = ServiceInstancesSchema.Table.Columns;
    var metadataColumn = columns.First(c => c.Name == "metadata");

    // Assert
    await Assert.That(metadataColumn.Nullable).IsTrue();
    await Assert.That(metadataColumn.DataType).IsEqualTo(WhizbangDataType.JSON);
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_HasAllConstantsAsync() {
    // Arrange & Act - Get all column constants
    var instanceId = ServiceInstancesSchema.Columns.INSTANCE_ID;
    var serviceName = ServiceInstancesSchema.Columns.SERVICE_NAME;
    var hostName = ServiceInstancesSchema.Columns.HOST_NAME;
    var processId = ServiceInstancesSchema.Columns.PROCESS_ID;
    var startedAt = ServiceInstancesSchema.Columns.STARTED_AT;
    var lastHeartbeatAt = ServiceInstancesSchema.Columns.LAST_HEARTBEAT_AT;
    var metadata = ServiceInstancesSchema.Columns.METADATA;

    // Assert - Verify constants match column names
    await Assert.That(instanceId).IsEqualTo("instance_id");
    await Assert.That(serviceName).IsEqualTo("service_name");
    await Assert.That(hostName).IsEqualTo("host_name");
    await Assert.That(processId).IsEqualTo("process_id");
    await Assert.That(startedAt).IsEqualTo("started_at");
    await Assert.That(lastHeartbeatAt).IsEqualTo("last_heartbeat_at");
    await Assert.That(metadata).IsEqualTo("metadata");
  }
}
