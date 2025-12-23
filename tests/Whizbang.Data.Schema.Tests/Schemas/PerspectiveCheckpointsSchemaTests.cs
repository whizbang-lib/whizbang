using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for PerspectiveCheckpointsSchema - read model checkpoint tracking table.
/// Tests verify table structure, column definitions, indexes, and column name constants.
/// </summary>

public class PerspectiveCheckpointsSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectNameAsync() {
    // Arrange & Act
    var tableName = PerspectiveCheckpointsSchema.Table.Name;

    // Assert
    await Assert.That(tableName).IsEqualTo("perspective_checkpoints");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectColumnsAsync() {
    // Arrange & Act
    var columns = PerspectiveCheckpointsSchema.Table.Columns;

    // Assert - Verify column count
    await Assert.That(columns).Count().IsEqualTo(6);

    // Verify each column definition
    var streamId = columns[0];
    await Assert.That(streamId.Name).IsEqualTo("stream_id");
    await Assert.That(streamId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(streamId.PrimaryKey).IsTrue();
    await Assert.That(streamId.Nullable).IsFalse();

    var perspectiveName = columns[1];
    await Assert.That(perspectiveName.Name).IsEqualTo("perspective_name");
    await Assert.That(perspectiveName.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(perspectiveName.PrimaryKey).IsTrue();
    await Assert.That(perspectiveName.Nullable).IsFalse();

    var lastEventId = columns[2];
    await Assert.That(lastEventId.Name).IsEqualTo("last_event_id");
    await Assert.That(lastEventId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(lastEventId.Nullable).IsFalse();

    var status = columns[3];
    await Assert.That(status.Name).IsEqualTo("status");
    await Assert.That(status.DataType).IsEqualTo(WhizbangDataType.SmallInt);
    await Assert.That(status.Nullable).IsFalse();

    var processedAt = columns[4];
    await Assert.That(processedAt.Name).IsEqualTo("processed_at");
    await Assert.That(processedAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(processedAt.Nullable).IsFalse();

    var error = columns[5];
    await Assert.That(error.Name).IsEqualTo("error");
    await Assert.That(error.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(error.Nullable).IsTrue();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_StreamId_IsCompositePrimaryKeyAsync() {
    // Arrange & Act
    var columns = PerspectiveCheckpointsSchema.Table.Columns;
    var streamId = columns.First(c => c.Name == "stream_id");

    // Assert
    await Assert.That(streamId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(streamId.PrimaryKey).IsTrue();
    await Assert.That(streamId.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_PerspectiveName_IsCompositePrimaryKeyAsync() {
    // Arrange & Act
    var columns = PerspectiveCheckpointsSchema.Table.Columns;
    var perspectiveName = columns.First(c => c.Name == "perspective_name");

    // Assert
    await Assert.That(perspectiveName.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(perspectiveName.PrimaryKey).IsTrue();
    await Assert.That(perspectiveName.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_LastEventId_HasCorrectDefinitionAsync() {
    // Arrange & Act
    var columns = PerspectiveCheckpointsSchema.Table.Columns;
    var lastEventId = columns.First(c => c.Name == "last_event_id");

    // Assert
    await Assert.That(lastEventId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(lastEventId.Nullable).IsFalse();
    await Assert.That(lastEventId.PrimaryKey).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_Status_HasCorrectDefaultAsync() {
    // Arrange & Act
    var columns = PerspectiveCheckpointsSchema.Table.Columns;
    var status = columns.First(c => c.Name == "status");

    // Assert
    await Assert.That(status.DataType).IsEqualTo(WhizbangDataType.SmallInt);
    await Assert.That(status.Nullable).IsFalse();
    await Assert.That(status.DefaultValue).IsNotNull();
    await Assert.That(status.DefaultValue).IsTypeOf<IntegerDefault>();
    await Assert.That(((IntegerDefault)status.DefaultValue!).Value).IsEqualTo(0);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ProcessedAt_HasDateTimeDefaultAsync() {
    // Arrange & Act
    var columns = PerspectiveCheckpointsSchema.Table.Columns;
    var processedAt = columns.First(c => c.Name == "processed_at");

    // Assert
    await Assert.That(processedAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(processedAt.Nullable).IsFalse();
    await Assert.That(processedAt.DefaultValue).IsNotNull();
    await Assert.That(processedAt.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)processedAt.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DateTime_Now);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_Error_IsNullableAsync() {
    // Arrange & Act
    var columns = PerspectiveCheckpointsSchema.Table.Columns;
    var error = columns.First(c => c.Name == "error");

    // Assert
    await Assert.That(error.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(error.Nullable).IsTrue();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectIndexesAsync() {
    // Arrange & Act
    var indexes = PerspectiveCheckpointsSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).Count().IsEqualTo(2);

    // Verify index on perspective_name
    var perspectiveNameIndex = indexes[0];
    await Assert.That(perspectiveNameIndex.Name).IsEqualTo("idx_perspective_checkpoints_perspective_name");
    await Assert.That(perspectiveNameIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(perspectiveNameIndex.Columns[0]).IsEqualTo("perspective_name");

    // Verify index on last_event_id
    var lastEventIdIndex = indexes[1];
    await Assert.That(lastEventIdIndex.Name).IsEqualTo("idx_perspective_checkpoints_last_event_id");
    await Assert.That(lastEventIdIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(lastEventIdIndex.Columns[0]).IsEqualTo("last_event_id");
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_Constants_MatchColumnNamesAsync() {
    // Arrange & Act - Get all column constants
    var streamId = PerspectiveCheckpointsSchema.Columns.StreamId;
    var perspectiveName = PerspectiveCheckpointsSchema.Columns.PerspectiveName;
    var lastEventId = PerspectiveCheckpointsSchema.Columns.LastEventId;
    var status = PerspectiveCheckpointsSchema.Columns.Status;
    var processedAt = PerspectiveCheckpointsSchema.Columns.ProcessedAt;
    var error = PerspectiveCheckpointsSchema.Columns.Error;

    // Assert - Verify constants match column names
    await Assert.That(streamId).IsEqualTo("stream_id");
    await Assert.That(perspectiveName).IsEqualTo("perspective_name");
    await Assert.That(lastEventId).IsEqualTo("last_event_id");
    await Assert.That(status).IsEqualTo("status");
    await Assert.That(processedAt).IsEqualTo("processed_at");
    await Assert.That(error).IsEqualTo("error");
  }
}
