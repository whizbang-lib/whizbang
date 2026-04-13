using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

#pragma warning disable RCS1118 // Mark local variable as const — conflicts with TUnit's TUnitAssertions0005 (can't pass const to Assert.That)

/// <summary>
/// Tests for PerspectiveCursorsSchema - read model checkpoint tracking table.
/// Tests verify table structure, column definitions, indexes, and column name constants.
/// </summary>

public class PerspectiveCursorsSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectNameAsync() {
    // Arrange & Act
    var tableName = PerspectiveCursorsSchema.Table.Name;

    // Assert
    await Assert.That(tableName).IsEqualTo("perspective_cursors");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectColumnsAsync() {
    // Arrange & Act
    var columns = PerspectiveCursorsSchema.Table.Columns;

    // Assert - Verify column count (6 original + 6 rewind/locking columns)
    await Assert.That(columns).Count().IsEqualTo(12);

    // Verify each column definition (use First to avoid order dependency)
    var streamId = columns.First(c => c.Name == "stream_id");
    await Assert.That(streamId.Name).IsEqualTo("stream_id");
    await Assert.That(streamId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(streamId.PrimaryKey).IsTrue();
    await Assert.That(streamId.Nullable).IsFalse();

    var perspectiveName = columns.First(c => c.Name == "perspective_name");
    await Assert.That(perspectiveName.Name).IsEqualTo("perspective_name");
    await Assert.That(perspectiveName.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(perspectiveName.PrimaryKey).IsTrue();
    await Assert.That(perspectiveName.Nullable).IsFalse();

    var lastEventId = columns.First(c => c.Name == "last_event_id");
    await Assert.That(lastEventId.Name).IsEqualTo("last_event_id");
    await Assert.That(lastEventId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(lastEventId.Nullable).IsTrue();

    var status = columns.First(c => c.Name == "status");
    await Assert.That(status.Name).IsEqualTo("status");
    await Assert.That(status.DataType).IsEqualTo(WhizbangDataType.SMALL_INT);
    await Assert.That(status.Nullable).IsFalse();

    var processedAt = columns.First(c => c.Name == "processed_at");
    await Assert.That(processedAt.Name).IsEqualTo("processed_at");
    await Assert.That(processedAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(processedAt.Nullable).IsFalse();

    var error = columns.First(c => c.Name == "error");
    await Assert.That(error.Name).IsEqualTo("error");
    await Assert.That(error.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(error.Nullable).IsTrue();

    var rewindTriggerEventId = columns.First(c => c.Name == "rewind_trigger_event_id");
    await Assert.That(rewindTriggerEventId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(rewindTriggerEventId.Nullable).IsTrue();

    var streamLockInstanceId = columns.First(c => c.Name == "stream_lock_instance_id");
    await Assert.That(streamLockInstanceId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(streamLockInstanceId.Nullable).IsTrue();

    var streamLockExpiry = columns.First(c => c.Name == "stream_lock_expiry");
    await Assert.That(streamLockExpiry.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(streamLockExpiry.Nullable).IsTrue();

    var streamLockReason = columns.First(c => c.Name == "stream_lock_reason");
    await Assert.That(streamLockReason.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(streamLockReason.Nullable).IsTrue();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_StreamId_IsCompositePrimaryKeyAsync() {
    // Arrange & Act
    var columns = PerspectiveCursorsSchema.Table.Columns;
    var streamId = columns.First(c => c.Name == "stream_id");

    // Assert
    await Assert.That(streamId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(streamId.PrimaryKey).IsTrue();
    await Assert.That(streamId.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_PerspectiveName_IsCompositePrimaryKeyAsync() {
    // Arrange & Act
    var columns = PerspectiveCursorsSchema.Table.Columns;
    var perspectiveName = columns.First(c => c.Name == "perspective_name");

    // Assert
    await Assert.That(perspectiveName.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(perspectiveName.PrimaryKey).IsTrue();
    await Assert.That(perspectiveName.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_LastEventId_HasCorrectDefinitionAsync() {
    // Arrange & Act
    var columns = PerspectiveCursorsSchema.Table.Columns;
    var lastEventId = columns.First(c => c.Name == "last_event_id");

    // Assert
    await Assert.That(lastEventId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(lastEventId.Nullable).IsTrue();
    await Assert.That(lastEventId.PrimaryKey).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_Status_HasCorrectDefaultAsync() {
    // Arrange & Act
    var columns = PerspectiveCursorsSchema.Table.Columns;
    var status = columns.First(c => c.Name == "status");

    // Assert
    await Assert.That(status.DataType).IsEqualTo(WhizbangDataType.SMALL_INT);
    await Assert.That(status.Nullable).IsFalse();
    await Assert.That(status.DefaultValue).IsNotNull();
    await Assert.That(status.DefaultValue).IsTypeOf<IntegerDefault>();
    await Assert.That(((IntegerDefault)status.DefaultValue!).Value).IsEqualTo(0);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ProcessedAt_HasDateTimeDefaultAsync() {
    // Arrange & Act
    var columns = PerspectiveCursorsSchema.Table.Columns;
    var processedAt = columns.First(c => c.Name == "processed_at");

    // Assert
    await Assert.That(processedAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(processedAt.Nullable).IsFalse();
    await Assert.That(processedAt.DefaultValue).IsNotNull();
    await Assert.That(processedAt.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)processedAt.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DATE_TIME__NOW);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_Error_IsNullableAsync() {
    // Arrange & Act
    var columns = PerspectiveCursorsSchema.Table.Columns;
    var error = columns.First(c => c.Name == "error");

    // Assert
    await Assert.That(error.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(error.Nullable).IsTrue();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectIndexesAsync() {
    // Arrange & Act
    var indexes = PerspectiveCursorsSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).Count().IsEqualTo(2);

    // Verify index on perspective_name
    var perspectiveNameIndex = indexes[0];
    await Assert.That(perspectiveNameIndex.Name).IsEqualTo("idx_perspective_cursors_perspective_name");
    await Assert.That(perspectiveNameIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(perspectiveNameIndex.Columns[0]).IsEqualTo("perspective_name");

    // Verify index on last_event_id
    var lastEventIdIndex = indexes[1];
    await Assert.That(lastEventIdIndex.Name).IsEqualTo("idx_perspective_cursors_last_event_id");
    await Assert.That(lastEventIdIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(lastEventIdIndex.Columns[0]).IsEqualTo("last_event_id");
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_Constants_MatchColumnNamesAsync() {
    // Arrange & Act - Get all column constants
    var streamId = PerspectiveCursorsSchema.Columns.STREAM_ID;
    var perspectiveName = PerspectiveCursorsSchema.Columns.PERSPECTIVE_NAME;
    var lastEventId = PerspectiveCursorsSchema.Columns.LAST_EVENT_ID;
    var status = PerspectiveCursorsSchema.Columns.STATUS;
    var processedAt = PerspectiveCursorsSchema.Columns.PROCESSED_AT;
    var error = PerspectiveCursorsSchema.Columns.ERROR;
    var rewindTriggerEventId = PerspectiveCursorsSchema.Columns.REWIND_TRIGGER_EVENT_ID;
    var rewindFlaggedAt = PerspectiveCursorsSchema.Columns.REWIND_FLAGGED_AT;
    var streamLockInstanceId = PerspectiveCursorsSchema.Columns.STREAM_LOCK_INSTANCE_ID;
    var streamLockExpiry = PerspectiveCursorsSchema.Columns.STREAM_LOCK_EXPIRY;
    var streamLockReason = PerspectiveCursorsSchema.Columns.STREAM_LOCK_REASON;

    // Assert - Verify constants match column names
    await Assert.That(streamId).IsEqualTo("stream_id");
    await Assert.That(perspectiveName).IsEqualTo("perspective_name");
    await Assert.That(lastEventId).IsEqualTo("last_event_id");
    await Assert.That(status).IsEqualTo("status");
    await Assert.That(processedAt).IsEqualTo("processed_at");
    await Assert.That(error).IsEqualTo("error");
    await Assert.That(rewindTriggerEventId).IsEqualTo("rewind_trigger_event_id");
    await Assert.That(rewindFlaggedAt).IsEqualTo("rewind_flagged_at");
    var rewindFirstFlaggedAt = PerspectiveCursorsSchema.Columns.REWIND_FIRST_FLAGGED_AT;
    await Assert.That(rewindFirstFlaggedAt).IsEqualTo("rewind_first_flagged_at");
    await Assert.That(streamLockInstanceId).IsEqualTo("stream_lock_instance_id");
    await Assert.That(streamLockExpiry).IsEqualTo("stream_lock_expiry");
    await Assert.That(streamLockReason).IsEqualTo("stream_lock_reason");
  }
}

#pragma warning restore RCS1118
