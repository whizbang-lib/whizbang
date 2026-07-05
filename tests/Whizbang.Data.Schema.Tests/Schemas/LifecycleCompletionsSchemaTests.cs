using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for LifecycleCompletionsSchema - durable PostLifecycle completion marker table.
/// Tests verify table structure, column definitions, defaults, indexes, and column name constants.
/// </summary>
public class LifecycleCompletionsSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectNameAsync() {
    // Arrange & Act
    var tableName = LifecycleCompletionsSchema.Table.Name;

    // Assert
    var constantName = LifecycleCompletionsSchema.TABLE_NAME;
    await Assert.That(tableName).IsEqualTo("lifecycle_completions");
    await Assert.That(tableName).IsEqualTo(constantName)
      .Because("The TABLE_NAME constant and the TableDefinition must agree.");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_DefinesAllColumnsWithCorrectTypesAndNullabilityAsync() {
    // Arrange & Act
    var columns = LifecycleCompletionsSchema.Table.Columns;

    // Assert - lightweight marker table: exactly three columns
    await Assert.That(columns).Count().IsEqualTo(3);

    var eventId = columns.First(c => c.Name == "event_id");
    await Assert.That(eventId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(eventId.PrimaryKey).IsTrue();
    await Assert.That(eventId.Nullable).IsFalse();

    var instanceId = columns.First(c => c.Name == "instance_id");
    await Assert.That(instanceId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(instanceId.PrimaryKey).IsFalse();
    await Assert.That(instanceId.Nullable).IsFalse();

    var completedAt = columns.First(c => c.Name == "completed_at");
    await Assert.That(completedAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(completedAt.PrimaryKey).IsFalse();
    await Assert.That(completedAt.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_EventId_IsOnlyPrimaryKeyAsync() {
    // Arrange & Act - one row per event that completed PostLifecycle
    var primaryKeyColumns = LifecycleCompletionsSchema.Table.Columns
      .Where(c => c.PrimaryKey)
      .ToList();

    // Assert
    await Assert.That(primaryKeyColumns.Count).IsEqualTo(1);
    await Assert.That(primaryKeyColumns[0].Name).IsEqualTo("event_id");
    await Assert.That(primaryKeyColumns[0].DataType).IsEqualTo(WhizbangDataType.UUID);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_CompletedAt_HasDateTimeNowDefaultAsync() {
    // Arrange & Act
    var completedAt = LifecycleCompletionsSchema.Table.Columns.First(c => c.Name == "completed_at");

    // Assert - completed_at defaults to now()
    await Assert.That(completedAt.DefaultValue).IsNotNull();
    await Assert.That(completedAt.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)completedAt.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DATE_TIME__NOW);

    // event_id and instance_id carry no defaults
    var eventId = LifecycleCompletionsSchema.Table.Columns.First(c => c.Name == "event_id");
    var instanceId = LifecycleCompletionsSchema.Table.Columns.First(c => c.Name == "instance_id");
    await Assert.That(eventId.DefaultValue).IsNull();
    await Assert.That(instanceId.DefaultValue).IsNull();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_DefinesCompletedAtIndexAsync() {
    // Arrange & Act
    var indexes = LifecycleCompletionsSchema.Table.Indexes;

    // Assert - single index supporting time-ranged reconciliation scans
    await Assert.That(indexes).Count().IsEqualTo(1);

    var completedAtIndex = indexes[0];
    await Assert.That(completedAtIndex.Name).IsEqualTo("idx_lifecycle_completions_completed_at");
    await Assert.That(completedAtIndex.Columns).Count().IsEqualTo(1);
    await Assert.That(completedAtIndex.Columns[0]).IsEqualTo("completed_at");
    await Assert.That(completedAtIndex.Unique).IsFalse();
    await Assert.That(completedAtIndex.WhereClause).IsNull();
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_Constants_MatchColumnNamesAsync() {
    // Arrange & Act - Get all column constants
    var eventId = LifecycleCompletionsSchema.Columns.EVENT_ID;
    var instanceId = LifecycleCompletionsSchema.Columns.INSTANCE_ID;
    var completedAt = LifecycleCompletionsSchema.Columns.COMPLETED_AT;

    // Assert - Verify constants match column names
    await Assert.That(eventId).IsEqualTo("event_id");
    await Assert.That(instanceId).IsEqualTo("instance_id");
    await Assert.That(completedAt).IsEqualTo("completed_at");

    // Constants cover every table column
    var tableColumnNames = LifecycleCompletionsSchema.Table.Columns.Select(c => c.Name).ToList();
    await Assert.That(tableColumnNames).Contains(eventId);
    await Assert.That(tableColumnNames).Contains(instanceId);
    await Assert.That(tableColumnNames).Contains(completedAt);
    await Assert.That(tableColumnNames.Count).IsEqualTo(3);
  }
}
