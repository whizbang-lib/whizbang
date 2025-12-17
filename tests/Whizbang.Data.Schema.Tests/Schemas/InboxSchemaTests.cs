using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for InboxSchema - inbox deduplication table definition.
/// Tests verify table structure, column definitions, indexes, and constraints.
/// </summary>
public class InboxSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_ShouldHaveCorrectNameAsync() {
    // Arrange & Act
    var tableName = InboxSchema.Table.Name;

    // Assert
    await Assert.That(tableName).IsEqualTo("inbox");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefineCorrectColumnsAsync() {
    // Arrange & Act
    var columns = InboxSchema.Table.Columns;

    // Assert - Verify column count
    await Assert.That(columns).HasCount().EqualTo(17);

    // Verify each column definition
    var messageId = columns[0];
    await Assert.That(messageId.Name).IsEqualTo("message_id");
    await Assert.That(messageId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(messageId.PrimaryKey).IsTrue();
    await Assert.That(messageId.Nullable).IsFalse();

    var handlerName = columns[1];
    await Assert.That(handlerName.Name).IsEqualTo("handler_name");
    await Assert.That(handlerName.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(handlerName.MaxLength).IsEqualTo(500);
    await Assert.That(handlerName.Nullable).IsFalse();

    var eventType = columns[2];
    await Assert.That(eventType.Name).IsEqualTo("event_type");
    await Assert.That(eventType.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(eventType.MaxLength).IsEqualTo(500);
    await Assert.That(eventType.Nullable).IsFalse();

    var eventData = columns[3];
    await Assert.That(eventData.Name).IsEqualTo("event_data");
    await Assert.That(eventData.DataType).IsEqualTo(WhizbangDataType.Json);
    await Assert.That(eventData.Nullable).IsFalse();

    var metadata = columns[4];
    await Assert.That(metadata.Name).IsEqualTo("metadata");
    await Assert.That(metadata.DataType).IsEqualTo(WhizbangDataType.Json);
    await Assert.That(metadata.Nullable).IsFalse();

    var scope = columns[5];
    await Assert.That(scope.Name).IsEqualTo("scope");
    await Assert.That(scope.DataType).IsEqualTo(WhizbangDataType.Json);
    await Assert.That(scope.Nullable).IsTrue();

    // Verify key columns by finding them (schema has 17 columns total)
    var processedAt = columns.First(c => c.Name == "processed_at");
    await Assert.That(processedAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(processedAt.Nullable).IsTrue();

    var receivedAt = columns.First(c => c.Name == "received_at");
    await Assert.That(receivedAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(receivedAt.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefineCorrectIndexesAsync() {
    // Arrange & Act
    var indexes = InboxSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).HasCount().EqualTo(6);

    // Verify processed_at index
    var processedAtIndex = indexes[0];
    await Assert.That(processedAtIndex.Name).IsEqualTo("idx_inbox_processed_at");
    await Assert.That(processedAtIndex.Columns).HasCount().EqualTo(1);
    await Assert.That(processedAtIndex.Columns[0]).IsEqualTo("processed_at");

    // Verify received_at index
    var receivedAtIndex = indexes[1];
    await Assert.That(receivedAtIndex.Name).IsEqualTo("idx_inbox_received_at");
    await Assert.That(receivedAtIndex.Columns).HasCount().EqualTo(1);
    await Assert.That(receivedAtIndex.Columns[0]).IsEqualTo("received_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldHavePrimaryKeyAsync() {
    // Arrange & Act
    var columns = InboxSchema.Table.Columns;
    var primaryKeyColumn = columns.FirstOrDefault(c => c.PrimaryKey);

    // Assert
    await Assert.That(primaryKeyColumn).IsNotNull();
    await Assert.That(primaryKeyColumn!.Name).IsEqualTo("message_id");
    await Assert.That(primaryKeyColumn.DataType).IsEqualTo(WhizbangDataType.Uuid);
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_ShouldProvideAllConstantsAsync() {
    // Arrange & Act - Get all column constants
    var messageId = InboxSchema.Columns.MessageId;
    var handlerName = InboxSchema.Columns.HandlerName;
    var eventType = InboxSchema.Columns.EventType;
    var eventData = InboxSchema.Columns.EventData;
    var metadata = InboxSchema.Columns.Metadata;
    var scope = InboxSchema.Columns.Scope;
    var streamId = InboxSchema.Columns.StreamId;
    var partitionNumber = InboxSchema.Columns.PartitionNumber;
    var status = InboxSchema.Columns.Status;
    var attempts = InboxSchema.Columns.Attempts;
    var error = InboxSchema.Columns.Error;
    var instanceId = InboxSchema.Columns.InstanceId;
    var leaseExpiry = InboxSchema.Columns.LeaseExpiry;
    var failureReason = InboxSchema.Columns.FailureReason;
    var scheduledFor = InboxSchema.Columns.ScheduledFor;
    var processedAt = InboxSchema.Columns.ProcessedAt;
    var receivedAt = InboxSchema.Columns.ReceivedAt;

    // Assert - Verify constants match column names
    await Assert.That(messageId).IsEqualTo("message_id");
    await Assert.That(handlerName).IsEqualTo("handler_name");
    await Assert.That(eventType).IsEqualTo("event_type");
    await Assert.That(eventData).IsEqualTo("event_data");
    await Assert.That(metadata).IsEqualTo("metadata");
    await Assert.That(scope).IsEqualTo("scope");
    await Assert.That(streamId).IsEqualTo("stream_id");
    await Assert.That(partitionNumber).IsEqualTo("partition_number");
    await Assert.That(status).IsEqualTo("status");
    await Assert.That(attempts).IsEqualTo("attempts");
    await Assert.That(error).IsEqualTo("error");
    await Assert.That(instanceId).IsEqualTo("instance_id");
    await Assert.That(leaseExpiry).IsEqualTo("lease_expiry");
    await Assert.That(failureReason).IsEqualTo("failure_reason");
    await Assert.That(scheduledFor).IsEqualTo("scheduled_for");
    await Assert.That(processedAt).IsEqualTo("processed_at");
    await Assert.That(receivedAt).IsEqualTo("received_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ColumnDefaults_ShouldBeCorrectAsync() {
    // Arrange & Act
    var columns = InboxSchema.Table.Columns;
    var receivedAtColumn = columns.First(c => c.Name == "received_at");

    // Assert - Verify received_at default value
    await Assert.That(receivedAtColumn.DefaultValue).IsNotNull();
    await Assert.That(receivedAtColumn.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)receivedAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DateTime_Now);
  }
}
