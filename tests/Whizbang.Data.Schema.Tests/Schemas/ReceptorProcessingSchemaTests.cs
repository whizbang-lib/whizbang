using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for ReceptorProcessingSchema - event handler tracking table.
/// Tests verify table structure, column definitions, indexes, and column name constants.
/// </summary>

public class ReceptorProcessingSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectNameAsync() {
    // Arrange & Act
    var tableName = ReceptorProcessingSchema.Table.Name;

    // Assert
    await Assert.That(tableName).IsEqualTo("receptor_processing");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectColumnsAsync() {
    // Arrange & Act
    var columns = ReceptorProcessingSchema.Table.Columns;

    // Assert - Verify column count
    await Assert.That(columns).Count().IsEqualTo(8);

    // Verify each column definition
    var id = columns[0];
    await Assert.That(id.Name).IsEqualTo("id");
    await Assert.That(id.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(id.PrimaryKey).IsTrue();
    await Assert.That(id.Nullable).IsFalse();

    var eventId = columns[1];
    await Assert.That(eventId.Name).IsEqualTo("event_id");
    await Assert.That(eventId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(eventId.Nullable).IsFalse();

    var receptorName = columns[2];
    await Assert.That(receptorName.Name).IsEqualTo("receptor_name");
    await Assert.That(receptorName.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(receptorName.Nullable).IsFalse();

    var status = columns[3];
    await Assert.That(status.Name).IsEqualTo("status");
    await Assert.That(status.DataType).IsEqualTo(WhizbangDataType.SmallInt);
    await Assert.That(status.Nullable).IsFalse();

    var attempts = columns[4];
    await Assert.That(attempts.Name).IsEqualTo("attempts");
    await Assert.That(attempts.DataType).IsEqualTo(WhizbangDataType.Integer);
    await Assert.That(attempts.Nullable).IsFalse();

    var error = columns[5];
    await Assert.That(error.Name).IsEqualTo("error");
    await Assert.That(error.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(error.Nullable).IsTrue();

    var startedAt = columns[6];
    await Assert.That(startedAt.Name).IsEqualTo("started_at");
    await Assert.That(startedAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(startedAt.Nullable).IsFalse();

    var processedAt = columns[7];
    await Assert.That(processedAt.Name).IsEqualTo("processed_at");
    await Assert.That(processedAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(processedAt.Nullable).IsTrue();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_Id_IsPrimaryKeyAsync() {
    // Arrange & Act
    var columns = ReceptorProcessingSchema.Table.Columns;
    var primaryKeyColumn = columns.FirstOrDefault(c => c.PrimaryKey);

    // Assert
    await Assert.That(primaryKeyColumn).IsNotNull();
    await Assert.That(primaryKeyColumn!.Name).IsEqualTo("id");
    await Assert.That(primaryKeyColumn.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(primaryKeyColumn.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_EventId_HasCorrectDefinitionAsync() {
    // Arrange & Act
    var columns = ReceptorProcessingSchema.Table.Columns;
    var eventIdColumn = columns.First(c => c.Name == "event_id");

    // Assert
    await Assert.That(eventIdColumn.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(eventIdColumn.Nullable).IsFalse();
    await Assert.That(eventIdColumn.PrimaryKey).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ReceptorName_HasCorrectDefinitionAsync() {
    // Arrange & Act
    var columns = ReceptorProcessingSchema.Table.Columns;
    var receptorNameColumn = columns.First(c => c.Name == "receptor_name");

    // Assert
    await Assert.That(receptorNameColumn.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(receptorNameColumn.Nullable).IsFalse();
    await Assert.That(receptorNameColumn.PrimaryKey).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_Status_HasCorrectDefaultAsync() {
    // Arrange & Act
    var columns = ReceptorProcessingSchema.Table.Columns;
    var statusColumn = columns.First(c => c.Name == "status");

    // Assert
    await Assert.That(statusColumn.DataType).IsEqualTo(WhizbangDataType.SmallInt);
    await Assert.That(statusColumn.Nullable).IsFalse();
    await Assert.That(statusColumn.DefaultValue).IsNotNull();
    await Assert.That(statusColumn.DefaultValue).IsTypeOf<IntegerDefault>();
    await Assert.That(((IntegerDefault)statusColumn.DefaultValue!).Value).IsEqualTo(0);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_Attempts_HasCorrectDefaultAsync() {
    // Arrange & Act
    var columns = ReceptorProcessingSchema.Table.Columns;
    var attemptsColumn = columns.First(c => c.Name == "attempts");

    // Assert
    await Assert.That(attemptsColumn.DataType).IsEqualTo(WhizbangDataType.Integer);
    await Assert.That(attemptsColumn.Nullable).IsFalse();
    await Assert.That(attemptsColumn.DefaultValue).IsNotNull();
    await Assert.That(attemptsColumn.DefaultValue).IsTypeOf<IntegerDefault>();
    await Assert.That(((IntegerDefault)attemptsColumn.DefaultValue!).Value).IsEqualTo(0);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_Error_IsNullableAsync() {
    // Arrange & Act
    var columns = ReceptorProcessingSchema.Table.Columns;
    var errorColumn = columns.First(c => c.Name == "error");

    // Assert
    await Assert.That(errorColumn.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(errorColumn.Nullable).IsTrue();
    await Assert.That(errorColumn.PrimaryKey).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_StartedAt_HasDateTimeDefaultAsync() {
    // Arrange & Act
    var columns = ReceptorProcessingSchema.Table.Columns;
    var startedAtColumn = columns.First(c => c.Name == "started_at");

    // Assert
    await Assert.That(startedAtColumn.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(startedAtColumn.Nullable).IsFalse();
    await Assert.That(startedAtColumn.DefaultValue).IsNotNull();
    await Assert.That(startedAtColumn.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)startedAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DateTime_Now);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ProcessedAt_IsNullableAsync() {
    // Arrange & Act
    var columns = ReceptorProcessingSchema.Table.Columns;
    var processedAtColumn = columns.First(c => c.Name == "processed_at");

    // Assert
    await Assert.That(processedAtColumn.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(processedAtColumn.Nullable).IsTrue();
    await Assert.That(processedAtColumn.PrimaryKey).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectIndexesAsync() {
    // Arrange & Act
    var indexes = ReceptorProcessingSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).Count().IsEqualTo(3);

    // Verify index on event_id
    var eventIdIndex = indexes[0];
    await Assert.That(eventIdIndex.Name).IsEqualTo("idx_receptor_processing_event_id");
    await Assert.That(eventIdIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(eventIdIndex.Columns[0]).IsEqualTo("event_id");

    // Verify index on receptor_name
    var receptorNameIndex = indexes[1];
    await Assert.That(receptorNameIndex.Name).IsEqualTo("idx_receptor_processing_receptor_name");
    await Assert.That(receptorNameIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(receptorNameIndex.Columns[0]).IsEqualTo("receptor_name");

    // Verify index on status
    var statusIndex = indexes[2];
    await Assert.That(statusIndex.Name).IsEqualTo("idx_receptor_processing_status");
    await Assert.That(statusIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(statusIndex.Columns[0]).IsEqualTo("status");
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_Constants_MatchColumnNamesAsync() {
    // Arrange & Act - Get all column constants
    var id = ReceptorProcessingSchema.Columns.Id;
    var eventId = ReceptorProcessingSchema.Columns.EventId;
    var receptorName = ReceptorProcessingSchema.Columns.ReceptorName;
    var status = ReceptorProcessingSchema.Columns.Status;
    var attempts = ReceptorProcessingSchema.Columns.Attempts;
    var error = ReceptorProcessingSchema.Columns.Error;
    var startedAt = ReceptorProcessingSchema.Columns.StartedAt;
    var processedAt = ReceptorProcessingSchema.Columns.ProcessedAt;

    // Assert - Verify constants match column names
    await Assert.That(id).IsEqualTo("id");
    await Assert.That(eventId).IsEqualTo("event_id");
    await Assert.That(receptorName).IsEqualTo("receptor_name");
    await Assert.That(status).IsEqualTo("status");
    await Assert.That(attempts).IsEqualTo("attempts");
    await Assert.That(error).IsEqualTo("error");
    await Assert.That(startedAt).IsEqualTo("started_at");
    await Assert.That(processedAt).IsEqualTo("processed_at");
  }
}
