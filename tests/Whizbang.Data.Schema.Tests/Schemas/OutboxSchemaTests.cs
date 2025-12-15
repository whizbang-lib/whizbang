using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for OutboxSchema - transactional outbox table definition.
/// Tests verify table structure, column definitions, indexes, and constraints.
/// </summary>
public class OutboxSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_ShouldHaveCorrectNameAsync() {
    // Arrange & Act
    var tableName = OutboxSchema.Table.Name;

    // Assert
    await Assert.That(tableName).IsEqualTo("outbox");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefineCorrectColumnsAsync() {
    // Arrange & Act
    var columns = OutboxSchema.Table.Columns;

    // Assert - Verify column count
    await Assert.That(columns).HasCount().EqualTo(10);

    // Verify each column definition
    var messageId = columns[0];
    await Assert.That(messageId.Name).IsEqualTo("message_id");
    await Assert.That(messageId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(messageId.PrimaryKey).IsTrue();
    await Assert.That(messageId.Nullable).IsFalse();

    var destination = columns[1];
    await Assert.That(destination.Name).IsEqualTo("destination");
    await Assert.That(destination.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(destination.MaxLength).IsEqualTo(500);
    await Assert.That(destination.Nullable).IsFalse();

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

    var status = columns[6];
    await Assert.That(status.Name).IsEqualTo("status");
    await Assert.That(status.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(status.MaxLength).IsEqualTo(50);
    await Assert.That(status.Nullable).IsFalse();

    var attempts = columns[7];
    await Assert.That(attempts.Name).IsEqualTo("attempts");
    await Assert.That(attempts.DataType).IsEqualTo(WhizbangDataType.Integer);
    await Assert.That(attempts.Nullable).IsFalse();

    var createdAt = columns[8];
    await Assert.That(createdAt.Name).IsEqualTo("created_at");
    await Assert.That(createdAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(createdAt.Nullable).IsFalse();

    var publishedAt = columns[9];
    await Assert.That(publishedAt.Name).IsEqualTo("published_at");
    await Assert.That(publishedAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(publishedAt.Nullable).IsTrue();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefineCorrectIndexesAsync() {
    // Arrange & Act
    var indexes = OutboxSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).HasCount().EqualTo(2);

    // Verify composite index on status and created_at
    var statusCreatedIndex = indexes[0];
    await Assert.That(statusCreatedIndex.Name).IsEqualTo("idx_outbox_status_created_at");
    await Assert.That(statusCreatedIndex.Columns).HasCount().EqualTo(2);
    await Assert.That(statusCreatedIndex.Columns[0]).IsEqualTo("status");
    await Assert.That(statusCreatedIndex.Columns[1]).IsEqualTo("created_at");

    // Verify index on published_at
    var publishedAtIndex = indexes[1];
    await Assert.That(publishedAtIndex.Name).IsEqualTo("idx_outbox_published_at");
    await Assert.That(publishedAtIndex.Columns).HasCount().EqualTo(1);
    await Assert.That(publishedAtIndex.Columns[0]).IsEqualTo("published_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldHavePrimaryKeyAsync() {
    // Arrange & Act
    var columns = OutboxSchema.Table.Columns;
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
    var messageId = OutboxSchema.Columns.MessageId;
    var destination = OutboxSchema.Columns.Destination;
    var eventType = OutboxSchema.Columns.EventType;
    var eventData = OutboxSchema.Columns.EventData;
    var metadata = OutboxSchema.Columns.Metadata;
    var scope = OutboxSchema.Columns.Scope;
    var status = OutboxSchema.Columns.Status;
    var attempts = OutboxSchema.Columns.Attempts;
    var createdAt = OutboxSchema.Columns.CreatedAt;
    var publishedAt = OutboxSchema.Columns.PublishedAt;

    // Assert - Verify constants match column names
    await Assert.That(messageId).IsEqualTo("message_id");
    await Assert.That(destination).IsEqualTo("destination");
    await Assert.That(eventType).IsEqualTo("event_type");
    await Assert.That(eventData).IsEqualTo("event_data");
    await Assert.That(metadata).IsEqualTo("metadata");
    await Assert.That(scope).IsEqualTo("scope");
    await Assert.That(status).IsEqualTo("status");
    await Assert.That(attempts).IsEqualTo("attempts");
    await Assert.That(createdAt).IsEqualTo("created_at");
    await Assert.That(publishedAt).IsEqualTo("published_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ColumnDefaults_ShouldBeCorrectAsync() {
    // Arrange & Act
    var columns = OutboxSchema.Table.Columns;

    // Find columns with defaults
    var statusColumn = columns.First(c => c.Name == "status");
    var attemptsColumn = columns.First(c => c.Name == "attempts");
    var createdAtColumn = columns.First(c => c.Name == "created_at");

    // Assert - Verify default values
    await Assert.That(statusColumn.DefaultValue).IsNotNull();
    await Assert.That(statusColumn.DefaultValue).IsTypeOf<StringDefault>();
    await Assert.That(((StringDefault)statusColumn.DefaultValue!).Value).IsEqualTo("Pending");

    await Assert.That(attemptsColumn.DefaultValue).IsNotNull();
    await Assert.That(attemptsColumn.DefaultValue).IsTypeOf<IntegerDefault>();
    await Assert.That(((IntegerDefault)attemptsColumn.DefaultValue!).Value).IsEqualTo(0);

    await Assert.That(createdAtColumn.DefaultValue).IsNotNull();
    await Assert.That(createdAtColumn.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)createdAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DateTime_Now);
  }
}
