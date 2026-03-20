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
    await Assert.That(columns).Count().IsEqualTo(18);

    // Verify each column definition
    var messageId = columns[0];
    await Assert.That(messageId.Name).IsEqualTo("message_id");
    await Assert.That(messageId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(messageId.PrimaryKey).IsTrue();
    await Assert.That(messageId.Nullable).IsFalse();

    var handlerName = columns[1];
    await Assert.That(handlerName.Name).IsEqualTo("handler_name");
    await Assert.That(handlerName.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(handlerName.MaxLength).IsEqualTo(500);
    await Assert.That(handlerName.Nullable).IsFalse();

    var messageType = columns[2];
    await Assert.That(messageType.Name).IsEqualTo("message_type");
    await Assert.That(messageType.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(messageType.MaxLength).IsEqualTo(500);
    await Assert.That(messageType.Nullable).IsFalse();

    var eventData = columns[3];
    await Assert.That(eventData.Name).IsEqualTo("event_data");
    await Assert.That(eventData.DataType).IsEqualTo(WhizbangDataType.JSON);
    await Assert.That(eventData.Nullable).IsFalse();

    var metadata = columns[4];
    await Assert.That(metadata.Name).IsEqualTo("metadata");
    await Assert.That(metadata.DataType).IsEqualTo(WhizbangDataType.JSON);
    await Assert.That(metadata.Nullable).IsFalse();

    var scope = columns[5];
    await Assert.That(scope.Name).IsEqualTo("scope");
    await Assert.That(scope.DataType).IsEqualTo(WhizbangDataType.JSON);
    await Assert.That(scope.Nullable).IsTrue();

    // Verify key columns by finding them (schema has 17 columns total)
    var processedAt = columns.First(c => c.Name == "processed_at");
    await Assert.That(processedAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(processedAt.Nullable).IsTrue();

    var receivedAt = columns.First(c => c.Name == "received_at");
    await Assert.That(receivedAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(receivedAt.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefineCorrectIndexesAsync() {
    // Arrange & Act
    var indexes = InboxSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).Count().IsEqualTo(8);

    // Verify processed_at index
    var processedAtIndex = indexes[0];
    await Assert.That(processedAtIndex.Name).IsEqualTo("idx_inbox_processed_at");
    await Assert.That(processedAtIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(processedAtIndex.Columns[0]).IsEqualTo("processed_at");

    // Verify received_at index
    var receivedAtIndex = indexes[1];
    await Assert.That(receivedAtIndex.Name).IsEqualTo("idx_inbox_received_at");
    await Assert.That(receivedAtIndex.Columns).Count().IsEqualTo(1);
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
    await Assert.That(primaryKeyColumn.DataType).IsEqualTo(WhizbangDataType.UUID);
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_ShouldProvideAllConstantsAsync() {
    // Arrange & Act - Get all column constants
    const string messageId = InboxSchema.Columns.MESSAGE_ID;
    const string handlerName = InboxSchema.Columns.HANDLER_NAME;
    const string messageType = InboxSchema.Columns.MESSAGE_TYPE;
    const string eventData = InboxSchema.Columns.EVENT_DATA;
    const string metadata = InboxSchema.Columns.METADATA;
    const string scope = InboxSchema.Columns.SCOPE;
    const string streamId = InboxSchema.Columns.STREAM_ID;
    const string partitionNumber = InboxSchema.Columns.PARTITION_NUMBER;
    const string status = InboxSchema.Columns.STATUS;
    const string attempts = InboxSchema.Columns.ATTEMPTS;
    const string error = InboxSchema.Columns.ERROR;
    const string instanceId = InboxSchema.Columns.INSTANCE_ID;
    const string leaseExpiry = InboxSchema.Columns.LEASE_EXPIRY;
    const string failureReason = InboxSchema.Columns.FAILURE_REASON;
    const string scheduledFor = InboxSchema.Columns.SCHEDULED_FOR;
    const string processedAt = InboxSchema.Columns.PROCESSED_AT;
    const string receivedAt = InboxSchema.Columns.RECEIVED_AT;

    // Assert - Verify constants match column names
    await Assert.That(messageId).IsEqualTo("message_id");
    await Assert.That(handlerName).IsEqualTo("handler_name");
    await Assert.That(messageType).IsEqualTo("message_type");
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
    await Assert.That(((FunctionDefault)receivedAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DATE_TIME__NOW);
  }
}
