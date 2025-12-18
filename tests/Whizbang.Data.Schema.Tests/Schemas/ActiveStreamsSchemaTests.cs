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
    await Assert.That(columns).HasCount().EqualTo(6);

    // Verify each column definition
    var streamId = columns[0];
    await Assert.That(streamId.Name).IsEqualTo("stream_id");
    await Assert.That(streamId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(streamId.PrimaryKey).IsTrue();
    await Assert.That(streamId.Nullable).IsFalse();

    var partitionNumber = columns[1];
    await Assert.That(partitionNumber.Name).IsEqualTo("partition_number");
    await Assert.That(partitionNumber.DataType).IsEqualTo(WhizbangDataType.Integer);
    await Assert.That(partitionNumber.Nullable).IsFalse();

    var assignedInstanceId = columns[2];
    await Assert.That(assignedInstanceId.Name).IsEqualTo("assigned_instance_id");
    await Assert.That(assignedInstanceId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(assignedInstanceId.Nullable).IsTrue();  // NULL = orphaned

    var leaseExpiry = columns[3];
    await Assert.That(leaseExpiry.Name).IsEqualTo("lease_expiry");
    await Assert.That(leaseExpiry.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(leaseExpiry.Nullable).IsTrue();  // NULL = no lease

    var createdAt = columns[4];
    await Assert.That(createdAt.Name).IsEqualTo("created_at");
    await Assert.That(createdAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(createdAt.Nullable).IsFalse();

    var updatedAt = columns[5];
    await Assert.That(updatedAt.Name).IsEqualTo("updated_at");
    await Assert.That(updatedAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(updatedAt.Nullable).IsFalse();
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
    await Assert.That(primaryKeyColumn.DataType).IsEqualTo(WhizbangDataType.Uuid);
  }

  // Note: Foreign key constraint is defined in migration SQL, not in schema definition

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectIndexesAsync() {
    // Arrange & Act
    var indexes = ActiveStreamsSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).HasCount().EqualTo(3);

    // Verify instance index
    var instanceIndex = indexes[0];
    await Assert.That(instanceIndex.Name).IsEqualTo("idx_active_streams_instance");
    await Assert.That(instanceIndex.Columns).HasCount().EqualTo(1);
    await Assert.That(instanceIndex.Columns[0]).IsEqualTo("assigned_instance_id");
    await Assert.That(instanceIndex.WhereClause).IsEqualTo("assigned_instance_id IS NOT NULL");

    // Verify partition index
    var partitionIndex = indexes[1];
    await Assert.That(partitionIndex.Name).IsEqualTo("idx_active_streams_partition");
    await Assert.That(partitionIndex.Columns).HasCount().EqualTo(1);
    await Assert.That(partitionIndex.Columns[0]).IsEqualTo("partition_number");
    await Assert.That(partitionIndex.WhereClause).IsEqualTo("assigned_instance_id IS NULL");

    // Verify lease expiry index
    var leaseExpiryIndex = indexes[2];
    await Assert.That(leaseExpiryIndex.Name).IsEqualTo("idx_active_streams_lease_expired");
    await Assert.That(leaseExpiryIndex.Columns).HasCount().EqualTo(1);
    await Assert.That(leaseExpiryIndex.Columns[0]).IsEqualTo("lease_expiry");
    await Assert.That(leaseExpiryIndex.WhereClause).IsEqualTo("lease_expiry < NOW()");
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
    await Assert.That(((FunctionDefault)createdAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DateTime_Now);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_UpdatedAt_HasDefaultNowAsync() {
    // Arrange & Act
    var columns = ActiveStreamsSchema.Table.Columns;
    var updatedAtColumn = columns.First(c => c.Name == "updated_at");

    // Assert
    await Assert.That(updatedAtColumn.DefaultValue).IsNotNull();
    await Assert.That(updatedAtColumn.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)updatedAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DateTime_Now);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_AssignedInstanceId_IsNullableAsync() {
    // Arrange & Act
    var columns = ActiveStreamsSchema.Table.Columns;
    var assignedInstanceIdColumn = columns.First(c => c.Name == "assigned_instance_id");

    // Assert
    await Assert.That(assignedInstanceIdColumn.Nullable).IsTrue();
    await Assert.That(assignedInstanceIdColumn.DataType).IsEqualTo(WhizbangDataType.Uuid);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_LeaseExpiry_IsNullableAsync() {
    // Arrange & Act
    var columns = ActiveStreamsSchema.Table.Columns;
    var leaseExpiryColumn = columns.First(c => c.Name == "lease_expiry");

    // Assert
    await Assert.That(leaseExpiryColumn.Nullable).IsTrue();
    await Assert.That(leaseExpiryColumn.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_HasAllConstantsAsync() {
    // Arrange & Act - Get all column constants
    var streamId = ActiveStreamsSchema.Columns.StreamId;
    var partitionNumber = ActiveStreamsSchema.Columns.PartitionNumber;
    var assignedInstanceId = ActiveStreamsSchema.Columns.AssignedInstanceId;
    var leaseExpiry = ActiveStreamsSchema.Columns.LeaseExpiry;
    var createdAt = ActiveStreamsSchema.Columns.CreatedAt;
    var updatedAt = ActiveStreamsSchema.Columns.UpdatedAt;

    // Assert - Verify constants match column names
    await Assert.That(streamId).IsEqualTo("stream_id");
    await Assert.That(partitionNumber).IsEqualTo("partition_number");
    await Assert.That(assignedInstanceId).IsEqualTo("assigned_instance_id");
    await Assert.That(leaseExpiry).IsEqualTo("lease_expiry");
    await Assert.That(createdAt).IsEqualTo("created_at");
    await Assert.That(updatedAt).IsEqualTo("updated_at");
  }
}
