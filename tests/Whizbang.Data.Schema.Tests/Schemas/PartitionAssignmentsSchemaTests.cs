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
    await Assert.That(column.Name).IsEqualTo(PartitionAssignmentsSchema.PARTITION_NUMBER);
    await Assert.That(column.DataType).IsEqualTo(WhizbangDataType.INTEGER);
    await Assert.That(column.Nullable).IsFalse();
    await Assert.That(column.PrimaryKey).IsTrue();
    await Assert.That(column.DefaultValue).IsNull();
  }

  [Test]
  public async Task InstanceId_ShouldBeRequiredUuidAsync() {
    var column = PartitionAssignmentsSchema.Columns[1];
    await Assert.That(column.Name).IsEqualTo(PartitionAssignmentsSchema.INSTANCE_ID);
    await Assert.That(column.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(column.Nullable).IsFalse();
    await Assert.That(column.PrimaryKey).IsFalse();
    await Assert.That(column.DefaultValue).IsNull();
  }

  [Test]
  public async Task AssignedAt_ShouldBeTimestampWithDefaultAsync() {
    var column = PartitionAssignmentsSchema.Columns[2];
    await Assert.That(column.Name).IsEqualTo(PartitionAssignmentsSchema.ASSIGNED_AT);
    await Assert.That(column.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(column.Nullable).IsFalse();
    await Assert.That(column.PrimaryKey).IsFalse();
    await Assert.That(column.DefaultValue).IsNotNull();

    var defaultValue = column.DefaultValue as FunctionDefault;
    await Assert.That(defaultValue).IsNotNull();
    await Assert.That(defaultValue!.FunctionType).IsEqualTo(DefaultValueFunction.DATE_TIME__NOW);
  }

  [Test]
  public async Task LastHeartbeat_ShouldBeTimestampWithDefaultAsync() {
    var column = PartitionAssignmentsSchema.Columns[3];
    await Assert.That(column.Name).IsEqualTo(PartitionAssignmentsSchema.LAST_HEARTBEAT);
    await Assert.That(column.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(column.Nullable).IsFalse();
    await Assert.That(column.PrimaryKey).IsFalse();
    await Assert.That(column.DefaultValue).IsNotNull();

    var defaultValue = column.DefaultValue as FunctionDefault;
    await Assert.That(defaultValue).IsNotNull();
    await Assert.That(defaultValue!.FunctionType).IsEqualTo(DefaultValueFunction.DATE_TIME__NOW);
  }

  [Test]
  public async Task Index_ShouldCoverInstanceAndHeartbeatAsync() {
    var index = PartitionAssignmentsSchema.Indexes[0];
    await Assert.That(index.Name).IsEqualTo(PartitionAssignmentsSchema.IDX_INSTANCE);
    await Assert.That(index.Columns).Count().IsEqualTo(2);
    await Assert.That(index.Columns[0]).IsEqualTo(PartitionAssignmentsSchema.INSTANCE_ID);
    await Assert.That(index.Columns[1]).IsEqualTo(PartitionAssignmentsSchema.LAST_HEARTBEAT);
    await Assert.That(index.Unique).IsFalse();
    await Assert.That(index.WhereClause).IsNull();
  }

  [Test]
  public async Task Columns_ShouldProvideAllConstantsAsync() {
    // Verify column name constants match actual column names
    await Assert.That(PartitionAssignmentsSchema.Columns[0].Name).IsEqualTo(PartitionAssignmentsSchema.PARTITION_NUMBER);
    await Assert.That(PartitionAssignmentsSchema.Columns[1].Name).IsEqualTo(PartitionAssignmentsSchema.INSTANCE_ID);
    await Assert.That(PartitionAssignmentsSchema.Columns[2].Name).IsEqualTo(PartitionAssignmentsSchema.ASSIGNED_AT);
    await Assert.That(PartitionAssignmentsSchema.Columns[3].Name).IsEqualTo(PartitionAssignmentsSchema.LAST_HEARTBEAT);
  }
}
