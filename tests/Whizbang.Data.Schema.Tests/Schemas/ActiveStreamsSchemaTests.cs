using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for ActiveStreamsSchema - ephemeral stream coordination table schema.
/// Tests verify table definition structure, columns, types, constraints, and indexes.
/// </summary>
public class ActiveStreamsSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectNameAsync() {
    // Arrange & Act
    var tableName = ActiveStreamsSchema.Table.Name;

    // Assert
    await Assert.That(tableName).IsEqualTo("active_streams");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectColumnsAsync() {
    // Arrange & Act
    var columns = ActiveStreamsSchema.Table.Columns;

    // Assert - Verify column count
    await Assert.That(columns).Count().IsEqualTo(6);

    // Verify each column definition
    var streamId = columns[0];
    await Assert.That(streamId.Name).IsEqualTo("stream_id");
    await Assert.That(streamId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(streamId.PrimaryKey).IsTrue();
    await Assert.That(streamId.Nullable).IsFalse();

    var partitionNumber = columns[1];
    await Assert.That(partitionNumber.Name).IsEqualTo("partition_number");
    await Assert.That(partitionNumber.DataType).IsEqualTo(WhizbangDataType.INTEGER);
    await Assert.That(partitionNumber.Nullable).IsFalse();

    var assignedInstanceId = columns[2];
    await Assert.That(assignedInstanceId.Name).IsEqualTo("assigned_instance_id");
    await Assert.That(assignedInstanceId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(assignedInstanceId.Nullable).IsTrue();  // NULL = orphaned

    var leaseExpiry = columns[3];
    await Assert.That(leaseExpiry.Name).IsEqualTo("lease_expiry");
    await Assert.That(leaseExpiry.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(leaseExpiry.Nullable).IsTrue();  // NULL = no lease

    var createdAt = columns[4];
    await Assert.That(createdAt.Name).IsEqualTo("created_at");
    await Assert.That(createdAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(createdAt.Nullable).IsFalse();

    var lastActivityAt = columns[5];
    await Assert.That(lastActivityAt.Name).IsEqualTo("last_activity_at");
    await Assert.That(lastActivityAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(lastActivityAt.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_StreamId_IsPrimaryKeyAsync() {
    // Arrange & Act
    var columns = ActiveStreamsSchema.Table.Columns;
    var primaryKeyColumn = columns.FirstOrDefault(c => c.PrimaryKey);

    // Assert
    await Assert.That(primaryKeyColumn).IsNotNull();
    await Assert.That(primaryKeyColumn!.Name).IsEqualTo("stream_id");
    await Assert.That(primaryKeyColumn.DataType).IsEqualTo(WhizbangDataType.UUID);
  }

  // Note: Foreign key constraint is defined in migration SQL, not in schema definition

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectIndexesAsync() {
    // Arrange & Act
    var indexes = ActiveStreamsSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).Count().IsEqualTo(3);

    // Verify instance index
    var instanceIndex = indexes[0];
    await Assert.That(instanceIndex.Name).IsEqualTo("idx_active_streams_instance");
    await Assert.That(instanceIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(instanceIndex.Columns[0]).IsEqualTo("assigned_instance_id");
    await Assert.That(instanceIndex.WhereClause).IsEqualTo("assigned_instance_id IS NOT NULL");

    // Verify partition index
    var partitionIndex = indexes[1];
    await Assert.That(partitionIndex.Name).IsEqualTo("idx_active_streams_partition");
    await Assert.That(partitionIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(partitionIndex.Columns[0]).IsEqualTo("partition_number");
    await Assert.That(partitionIndex.WhereClause).IsEqualTo("assigned_instance_id IS NULL");

    // Verify lease expiry index
    var leaseExpiryIndex = indexes[2];
    await Assert.That(leaseExpiryIndex.Name).IsEqualTo("idx_active_streams_lease_expiry");
    await Assert.That(leaseExpiryIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(leaseExpiryIndex.Columns[0]).IsEqualTo("lease_expiry");
    await Assert.That(leaseExpiryIndex.WhereClause).IsEqualTo("lease_expiry IS NOT NULL");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_CreatedAt_HasDefaultNowAsync() {
    // Arrange & Act
    var columns = ActiveStreamsSchema.Table.Columns;
    var createdAtColumn = columns.First(c => c.Name == "created_at");

    // Assert
    await Assert.That(createdAtColumn.DefaultValue).IsNotNull();
    await Assert.That(createdAtColumn.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)createdAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DATE_TIME__NOW);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_UpdatedAt_HasDefaultNowAsync() {
    // Arrange & Act
    var columns = ActiveStreamsSchema.Table.Columns;
    var lastActivityAtColumn = columns.First(c => c.Name == "last_activity_at");

    // Assert
    await Assert.That(lastActivityAtColumn.DefaultValue).IsNotNull();
    await Assert.That(lastActivityAtColumn.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)lastActivityAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DATE_TIME__NOW);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_AssignedInstanceId_IsNullableAsync() {
    // Arrange & Act
    var columns = ActiveStreamsSchema.Table.Columns;
    var assignedInstanceIdColumn = columns.First(c => c.Name == "assigned_instance_id");

    // Assert
    await Assert.That(assignedInstanceIdColumn.Nullable).IsTrue();
    await Assert.That(assignedInstanceIdColumn.DataType).IsEqualTo(WhizbangDataType.UUID);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_LeaseExpiry_IsNullableAsync() {
    // Arrange & Act
    var columns = ActiveStreamsSchema.Table.Columns;
    var leaseExpiryColumn = columns.First(c => c.Name == "lease_expiry");

    // Assert
    await Assert.That(leaseExpiryColumn.Nullable).IsTrue();
    await Assert.That(leaseExpiryColumn.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_HasAllConstantsAsync() {
    // Arrange & Act - Get all column constants
    var streamId = ActiveStreamsSchema.Columns.STREAM_ID;
    var partitionNumber = ActiveStreamsSchema.Columns.PARTITION_NUMBER;
    var assignedInstanceId = ActiveStreamsSchema.Columns.ASSIGNED_INSTANCE_ID;
    var leaseExpiry = ActiveStreamsSchema.Columns.LEASE_EXPIRY;
    var createdAt = ActiveStreamsSchema.Columns.CREATED_AT;
    var lastActivityAt = ActiveStreamsSchema.Columns.LAST_ACTIVITY_AT;

    // Assert - Verify constants match column names
    await Assert.That(streamId).IsEqualTo("stream_id");
    await Assert.That(partitionNumber).IsEqualTo("partition_number");
    await Assert.That(assignedInstanceId).IsEqualTo("assigned_instance_id");
    await Assert.That(leaseExpiry).IsEqualTo("lease_expiry");
    await Assert.That(createdAt).IsEqualTo("created_at");
    await Assert.That(lastActivityAt).IsEqualTo("last_activity_at");
  }
}
