using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for PerspectiveEventsSchema - perspective work tracking table definition.
/// Tests verify table structure, column definitions, defaults, indexes (including
/// the partial claim index), and column name constants.
/// </summary>
public class PerspectiveEventsSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectNameAsync() {
    // Arrange & Act
    var tableName = PerspectiveEventsSchema.Table.Name;

    // Assert
    var constantName = PerspectiveEventsSchema.TABLE_NAME;
    await Assert.That(tableName).IsEqualTo("perspective_events");
    await Assert.That(tableName).IsEqualTo(constantName)
      .Because("The TABLE_NAME constant and the TableDefinition must agree.");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_DefinesAllColumnsWithCorrectTypesAndNullabilityAsync() {
    // Arrange & Act
    var columns = PerspectiveEventsSchema.Table.Columns;

    // Assert - Verify column count
    await Assert.That(columns).Count().IsEqualTo(15);

    var eventWorkId = columns.First(c => c.Name == "event_work_id");
    await Assert.That(eventWorkId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(eventWorkId.PrimaryKey).IsTrue();
    await Assert.That(eventWorkId.Nullable).IsFalse();

    var streamId = columns.First(c => c.Name == "stream_id");
    await Assert.That(streamId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(streamId.PrimaryKey).IsFalse();
    await Assert.That(streamId.Nullable).IsFalse();

    var perspectiveName = columns.First(c => c.Name == "perspective_name");
    await Assert.That(perspectiveName.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(perspectiveName.Nullable).IsFalse();

    var eventId = columns.First(c => c.Name == "event_id");
    await Assert.That(eventId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(eventId.Nullable).IsFalse();

    var sequenceNumber = columns.First(c => c.Name == "sequence_number");
    await Assert.That(sequenceNumber.DataType).IsEqualTo(WhizbangDataType.BIG_INT);
    await Assert.That(sequenceNumber.Nullable).IsFalse();

    // NULL instance_id indicates unclaimed work
    var instanceId = columns.First(c => c.Name == "instance_id");
    await Assert.That(instanceId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(instanceId.Nullable).IsTrue();

    // NULL lease_expiry indicates no lease
    var leaseExpiry = columns.First(c => c.Name == "lease_expiry");
    await Assert.That(leaseExpiry.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(leaseExpiry.Nullable).IsTrue();

    var status = columns.First(c => c.Name == "status");
    await Assert.That(status.DataType).IsEqualTo(WhizbangDataType.INTEGER);
    await Assert.That(status.Nullable).IsFalse();

    var attempts = columns.First(c => c.Name == "attempts");
    await Assert.That(attempts.DataType).IsEqualTo(WhizbangDataType.INTEGER);
    await Assert.That(attempts.Nullable).IsFalse();

    var error = columns.First(c => c.Name == "error");
    await Assert.That(error.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(error.Nullable).IsTrue();

    var createdAt = columns.First(c => c.Name == "created_at");
    await Assert.That(createdAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(createdAt.Nullable).IsFalse();

    var claimedAt = columns.First(c => c.Name == "claimed_at");
    await Assert.That(claimedAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(claimedAt.Nullable).IsTrue();

    var processedAt = columns.First(c => c.Name == "processed_at");
    await Assert.That(processedAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(processedAt.Nullable).IsTrue();

    var scheduledFor = columns.First(c => c.Name == "scheduled_for");
    await Assert.That(scheduledFor.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(scheduledFor.Nullable).IsTrue();

    var failureReason = columns.First(c => c.Name == "failure_reason");
    await Assert.That(failureReason.DataType).IsEqualTo(WhizbangDataType.INTEGER);
    await Assert.That(failureReason.Nullable).IsTrue();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_EventWorkId_IsOnlyPrimaryKeyAsync() {
    // Arrange & Act
    var primaryKeyColumns = PerspectiveEventsSchema.Table.Columns
      .Where(c => c.PrimaryKey)
      .ToList();

    // Assert - single-column primary key on event_work_id
    await Assert.That(primaryKeyColumns.Count).IsEqualTo(1);
    await Assert.That(primaryKeyColumns[0].Name).IsEqualTo("event_work_id");
    await Assert.That(primaryKeyColumns[0].DataType).IsEqualTo(WhizbangDataType.UUID);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ColumnDefaults_AreCorrectAsync() {
    // Arrange & Act
    var columns = PerspectiveEventsSchema.Table.Columns;

    var status = columns.First(c => c.Name == "status");
    var attempts = columns.First(c => c.Name == "attempts");
    var createdAt = columns.First(c => c.Name == "created_at");

    // Assert - status defaults to 0 (pending)
    await Assert.That(status.DefaultValue).IsNotNull();
    await Assert.That(status.DefaultValue).IsTypeOf<IntegerDefault>();
    await Assert.That(((IntegerDefault)status.DefaultValue!).Value).IsEqualTo(0);

    // attempts defaults to 0
    await Assert.That(attempts.DefaultValue).IsNotNull();
    await Assert.That(attempts.DefaultValue).IsTypeOf<IntegerDefault>();
    await Assert.That(((IntegerDefault)attempts.DefaultValue!).Value).IsEqualTo(0);

    // created_at defaults to now()
    await Assert.That(createdAt.DefaultValue).IsNotNull();
    await Assert.That(createdAt.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)createdAt.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DATE_TIME__NOW);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ColumnsWithoutDefaults_HaveNullDefaultValueAsync() {
    // Arrange & Act
    var columns = PerspectiveEventsSchema.Table.Columns;

    // Assert - only status, attempts, and created_at carry defaults
    var columnsWithDefaults = columns.Where(c => c.DefaultValue != null).Select(c => c.Name).ToList();
    await Assert.That(columnsWithDefaults.Count).IsEqualTo(3);
    await Assert.That(columnsWithDefaults).Contains("status");
    await Assert.That(columnsWithDefaults).Contains("attempts");
    await Assert.That(columnsWithDefaults).Contains("created_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_DefinesCorrectIndexesAsync() {
    // Arrange & Act
    var indexes = PerspectiveEventsSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).Count().IsEqualTo(3);

    // Partial claim index: unclaimed/expired work lookup, filtered to unprocessed rows
    var claimIndex = indexes[0];
    await Assert.That(claimIndex.Name).IsEqualTo("idx_perspective_event_claim");
    await Assert.That(claimIndex.Columns).Count().IsEqualTo(3);
    await Assert.That(claimIndex.Columns[0]).IsEqualTo("instance_id");
    await Assert.That(claimIndex.Columns[1]).IsEqualTo("lease_expiry");
    await Assert.That(claimIndex.Columns[2]).IsEqualTo("scheduled_for");
    await Assert.That(claimIndex.Unique).IsFalse();
    await Assert.That(claimIndex.WhereClause).IsEqualTo("processed_at IS NULL");

    // Ordering index: per stream/perspective sequence ordering
    var orderIndex = indexes[1];
    await Assert.That(orderIndex.Name).IsEqualTo("idx_perspective_event_order");
    await Assert.That(orderIndex.Columns).Count().IsEqualTo(3);
    await Assert.That(orderIndex.Columns[0]).IsEqualTo("stream_id");
    await Assert.That(orderIndex.Columns[1]).IsEqualTo("perspective_name");
    await Assert.That(orderIndex.Columns[2]).IsEqualTo("sequence_number");
    await Assert.That(orderIndex.Unique).IsFalse();
    await Assert.That(orderIndex.WhereClause).IsNull();

    // Stream lookup index
    var streamIndex = indexes[2];
    await Assert.That(streamIndex.Name).IsEqualTo("idx_perspective_event_stream");
    await Assert.That(streamIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(streamIndex.Columns[0]).IsEqualTo("stream_id");
    await Assert.That(streamIndex.Unique).IsFalse();
    await Assert.That(streamIndex.WhereClause).IsNull();
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_Constants_MatchColumnNamesAsync() {
    // Arrange & Act - Get all column constants
    var eventWorkId = PerspectiveEventsSchema.Columns.EVENT_WORK_ID;
    var streamId = PerspectiveEventsSchema.Columns.STREAM_ID;
    var perspectiveName = PerspectiveEventsSchema.Columns.PERSPECTIVE_NAME;
    var eventId = PerspectiveEventsSchema.Columns.EVENT_ID;
    var sequenceNumber = PerspectiveEventsSchema.Columns.SEQUENCE_NUMBER;
    var instanceId = PerspectiveEventsSchema.Columns.INSTANCE_ID;
    var leaseExpiry = PerspectiveEventsSchema.Columns.LEASE_EXPIRY;
    var status = PerspectiveEventsSchema.Columns.STATUS;
    var attempts = PerspectiveEventsSchema.Columns.ATTEMPTS;
    var error = PerspectiveEventsSchema.Columns.ERROR;
    var createdAt = PerspectiveEventsSchema.Columns.CREATED_AT;
    var claimedAt = PerspectiveEventsSchema.Columns.CLAIMED_AT;
    var processedAt = PerspectiveEventsSchema.Columns.PROCESSED_AT;
    var scheduledFor = PerspectiveEventsSchema.Columns.SCHEDULED_FOR;
    var failureReason = PerspectiveEventsSchema.Columns.FAILURE_REASON;

    // Assert - Verify constants match column names
    await Assert.That(eventWorkId).IsEqualTo("event_work_id");
    await Assert.That(streamId).IsEqualTo("stream_id");
    await Assert.That(perspectiveName).IsEqualTo("perspective_name");
    await Assert.That(eventId).IsEqualTo("event_id");
    await Assert.That(sequenceNumber).IsEqualTo("sequence_number");
    await Assert.That(instanceId).IsEqualTo("instance_id");
    await Assert.That(leaseExpiry).IsEqualTo("lease_expiry");
    await Assert.That(status).IsEqualTo("status");
    await Assert.That(attempts).IsEqualTo("attempts");
    await Assert.That(error).IsEqualTo("error");
    await Assert.That(createdAt).IsEqualTo("created_at");
    await Assert.That(claimedAt).IsEqualTo("claimed_at");
    await Assert.That(processedAt).IsEqualTo("processed_at");
    await Assert.That(scheduledFor).IsEqualTo("scheduled_for");
    await Assert.That(failureReason).IsEqualTo("failure_reason");
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_Constants_CoverEveryTableColumnAsync() {
    // Arrange - every column defined in the table
    var tableColumnNames = PerspectiveEventsSchema.Table.Columns.Select(c => c.Name).ToList();

    // Act - every constant defined in the Columns class
    var constantValues = new[] {
      PerspectiveEventsSchema.Columns.EVENT_WORK_ID,
      PerspectiveEventsSchema.Columns.STREAM_ID,
      PerspectiveEventsSchema.Columns.PERSPECTIVE_NAME,
      PerspectiveEventsSchema.Columns.EVENT_ID,
      PerspectiveEventsSchema.Columns.SEQUENCE_NUMBER,
      PerspectiveEventsSchema.Columns.INSTANCE_ID,
      PerspectiveEventsSchema.Columns.LEASE_EXPIRY,
      PerspectiveEventsSchema.Columns.STATUS,
      PerspectiveEventsSchema.Columns.ATTEMPTS,
      PerspectiveEventsSchema.Columns.ERROR,
      PerspectiveEventsSchema.Columns.CREATED_AT,
      PerspectiveEventsSchema.Columns.CLAIMED_AT,
      PerspectiveEventsSchema.Columns.PROCESSED_AT,
      PerspectiveEventsSchema.Columns.SCHEDULED_FOR,
      PerspectiveEventsSchema.Columns.FAILURE_REASON
    };

    // Assert - constants and table columns are the same set
    await Assert.That(constantValues.Length).IsEqualTo(tableColumnNames.Count);
    foreach (var constant in constantValues) {
      await Assert.That(tableColumnNames).Contains(constant);
    }
  }
}
