using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

public class PartitionAssignmentsSchemaTests {
  [Test]
  public async Task Table_ShouldHaveCorrectNameAsync() {
    await Assert.That(PartitionAssignmentsSchema.Table.Name).IsEqualTo("partition_assignments");
  }

  [Test]
  public async Task Table_ShouldHaveFourColumnsAsync() {
    await Assert.That(PartitionAssignmentsSchema.Table.Columns).Count().IsEqualTo(4);
  }

  [Test]
  public async Task Table_ShouldHaveOneIndexAsync() {
    await Assert.That(PartitionAssignmentsSchema.Table.Indexes).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PartitionNumber_ShouldBeIntegerPrimaryKeyAsync() {
    var column = PartitionAssignmentsSchema.Columns[0];
    await Assert.That(column.Name).IsEqualTo(PartitionAssignmentsSchema.PartitionNumber);
    await Assert.That(column.DataType).IsEqualTo(WhizbangDataType.Integer);
    await Assert.That(column.Nullable).IsFalse();
    await Assert.That(column.PrimaryKey).IsTrue();
    await Assert.That(column.DefaultValue).IsNull();
  }

  [Test]
  public async Task InstanceId_ShouldBeRequiredUuidAsync() {
    var column = PartitionAssignmentsSchema.Columns[1];
    await Assert.That(column.Name).IsEqualTo(PartitionAssignmentsSchema.InstanceId);
    await Assert.That(column.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(column.Nullable).IsFalse();
    await Assert.That(column.PrimaryKey).IsFalse();
    await Assert.That(column.DefaultValue).IsNull();
  }

  [Test]
  public async Task AssignedAt_ShouldBeTimestampWithDefaultAsync() {
    var column = PartitionAssignmentsSchema.Columns[2];
    await Assert.That(column.Name).IsEqualTo(PartitionAssignmentsSchema.AssignedAt);
    await Assert.That(column.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(column.Nullable).IsFalse();
    await Assert.That(column.PrimaryKey).IsFalse();
    await Assert.That(column.DefaultValue).IsNotNull();

    var defaultValue = column.DefaultValue as FunctionDefault;
    await Assert.That(defaultValue).IsNotNull();
    await Assert.That(defaultValue!.FunctionType).IsEqualTo(DefaultValueFunction.DateTime_Now);
  }

  [Test]
  public async Task LastHeartbeat_ShouldBeTimestampWithDefaultAsync() {
    var column = PartitionAssignmentsSchema.Columns[3];
    await Assert.That(column.Name).IsEqualTo(PartitionAssignmentsSchema.LastHeartbeat);
    await Assert.That(column.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(column.Nullable).IsFalse();
    await Assert.That(column.PrimaryKey).IsFalse();
    await Assert.That(column.DefaultValue).IsNotNull();

    var defaultValue = column.DefaultValue as FunctionDefault;
    await Assert.That(defaultValue).IsNotNull();
    await Assert.That(defaultValue!.FunctionType).IsEqualTo(DefaultValueFunction.DateTime_Now);
  }

  [Test]
  public async Task Index_ShouldCoverInstanceAndHeartbeatAsync() {
    var index = PartitionAssignmentsSchema.Indexes[0];
    await Assert.That(index.Name).IsEqualTo(PartitionAssignmentsSchema.IdxInstance);
    await Assert.That(index.Columns).Count().IsEqualTo(2);
    await Assert.That(index.Columns[0]).IsEqualTo(PartitionAssignmentsSchema.InstanceId);
    await Assert.That(index.Columns[1]).IsEqualTo(PartitionAssignmentsSchema.LastHeartbeat);
    await Assert.That(index.Unique).IsFalse();
    await Assert.That(index.WhereClause).IsNull();
  }

  [Test]
  public async Task Columns_ShouldProvideAllConstantsAsync() {
    // Verify all column name constants are present
    await Assert.That(PartitionAssignmentsSchema.PartitionNumber).IsNotNull();
    await Assert.That(PartitionAssignmentsSchema.InstanceId).IsNotNull();
    await Assert.That(PartitionAssignmentsSchema.AssignedAt).IsNotNull();
    await Assert.That(PartitionAssignmentsSchema.LastHeartbeat).IsNotNull();

    // Verify column name constants match actual column names
    await Assert.That(PartitionAssignmentsSchema.Columns[0].Name).IsEqualTo(PartitionAssignmentsSchema.PartitionNumber);
    await Assert.That(PartitionAssignmentsSchema.Columns[1].Name).IsEqualTo(PartitionAssignmentsSchema.InstanceId);
    await Assert.That(PartitionAssignmentsSchema.Columns[2].Name).IsEqualTo(PartitionAssignmentsSchema.AssignedAt);
    await Assert.That(PartitionAssignmentsSchema.Columns[3].Name).IsEqualTo(PartitionAssignmentsSchema.LastHeartbeat);
  }
}
