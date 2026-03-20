using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for EventStoreSchema - event sourcing table definition.
/// Tests verify table structure, column definitions, indexes, and constraints.
/// </summary>
public class EventStoreSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_ShouldHaveCorrectNameAsync() {
    // Arrange & Act
    var tableName = EventStoreSchema.Table.Name;

    // Assert
    await Assert.That(tableName).IsEqualTo("event_store");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefineCorrectColumnsAsync() {
    // Arrange & Act
    var columns = EventStoreSchema.Table.Columns;

    // Assert - Verify column count
    await Assert.That(columns).Count().IsEqualTo(10);

    // Verify each column definition
    var eventId = columns[0];
    await Assert.That(eventId.Name).IsEqualTo("event_id");
    await Assert.That(eventId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(eventId.PrimaryKey).IsTrue();
    await Assert.That(eventId.Nullable).IsFalse();

    var streamId = columns[1];
    await Assert.That(streamId.Name).IsEqualTo("stream_id");
    await Assert.That(streamId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(streamId.Nullable).IsFalse();

    var aggregateId = columns[2];
    await Assert.That(aggregateId.Name).IsEqualTo("aggregate_id");
    await Assert.That(aggregateId.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(aggregateId.Nullable).IsFalse();

    var aggregateType = columns[3];
    await Assert.That(aggregateType.Name).IsEqualTo("aggregate_type");
    await Assert.That(aggregateType.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(aggregateType.MaxLength).IsEqualTo(500);
    await Assert.That(aggregateType.Nullable).IsFalse();

    var eventType = columns[4];
    await Assert.That(eventType.Name).IsEqualTo("event_type");
    await Assert.That(eventType.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(eventType.MaxLength).IsEqualTo(500);
    await Assert.That(eventType.Nullable).IsFalse();

    var eventData = columns[5];
    await Assert.That(eventData.Name).IsEqualTo("event_data");
    await Assert.That(eventData.DataType).IsEqualTo(WhizbangDataType.JSON);
    await Assert.That(eventData.Nullable).IsFalse();

    var metadata = columns[6];
    await Assert.That(metadata.Name).IsEqualTo("metadata");
    await Assert.That(metadata.DataType).IsEqualTo(WhizbangDataType.JSON);
    await Assert.That(metadata.Nullable).IsFalse();

    var scope = columns[7];
    await Assert.That(scope.Name).IsEqualTo("scope");
    await Assert.That(scope.DataType).IsEqualTo(WhizbangDataType.JSON);
    await Assert.That(scope.Nullable).IsTrue();

    var version = columns[8];
    await Assert.That(version.Name).IsEqualTo("version");
    await Assert.That(version.DataType).IsEqualTo(WhizbangDataType.INTEGER);
    await Assert.That(version.Nullable).IsFalse();

    var createdAt = columns[9];
    await Assert.That(createdAt.Name).IsEqualTo("created_at");
    await Assert.That(createdAt.DataType).IsEqualTo(WhizbangDataType.TIMESTAMP_TZ);
    await Assert.That(createdAt.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefineCorrectIndexesAsync() {
    // Arrange & Act
    var indexes = EventStoreSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).Count().IsEqualTo(3);

    // Verify unique stream index
    var streamIndex = indexes[0];
    await Assert.That(streamIndex.Name).IsEqualTo("idx_event_store_stream");
    await Assert.That(streamIndex.Columns).Count().IsEqualTo(2);
    await Assert.That(streamIndex.Columns[0]).IsEqualTo("stream_id");
    await Assert.That(streamIndex.Columns[1]).IsEqualTo("version");
    await Assert.That(streamIndex.Unique).IsTrue();

    // Verify unique aggregate index
    var aggregateIndex = indexes[1];
    await Assert.That(aggregateIndex.Name).IsEqualTo("idx_event_store_aggregate");
    await Assert.That(aggregateIndex.Columns).Count().IsEqualTo(2);
    await Assert.That(aggregateIndex.Columns[0]).IsEqualTo("aggregate_id");
    await Assert.That(aggregateIndex.Columns[1]).IsEqualTo("version");
    await Assert.That(aggregateIndex.Unique).IsTrue();

    // Verify aggregate_type index
    var aggregateTypeIndex = indexes[2];
    await Assert.That(aggregateTypeIndex.Name).IsEqualTo("idx_event_store_aggregate_type");
    await Assert.That(aggregateTypeIndex.Columns).Count().IsEqualTo(2);
    await Assert.That(aggregateTypeIndex.Columns[0]).IsEqualTo("aggregate_type");
    await Assert.That(aggregateTypeIndex.Columns[1]).IsEqualTo("created_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldHavePrimaryKeyAsync() {
    // Arrange & Act
    var columns = EventStoreSchema.Table.Columns;
    var primaryKeyColumn = columns.FirstOrDefault(c => c.PrimaryKey);

    // Assert
    await Assert.That(primaryKeyColumn).IsNotNull();
    await Assert.That(primaryKeyColumn!.Name).IsEqualTo("event_id");
    await Assert.That(primaryKeyColumn.DataType).IsEqualTo(WhizbangDataType.UUID);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldHaveUniqueAggregateVersionIndexAsync() {
    // Arrange & Act
    var indexes = EventStoreSchema.Table.Indexes;
    var aggregateIndex = indexes.FirstOrDefault(i => i.Name == "idx_event_store_aggregate");

    // Assert
    await Assert.That(aggregateIndex).IsNotNull();
    await Assert.That(aggregateIndex!.Unique).IsTrue();
    await Assert.That(aggregateIndex.Columns).Count().IsEqualTo(2);
    await Assert.That(aggregateIndex.Columns[0]).IsEqualTo("aggregate_id");
    await Assert.That(aggregateIndex.Columns[1]).IsEqualTo("version");
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_ShouldProvideAllConstantsAsync() {
    // Arrange & Act - Get all column constants
    var eventId = EventStoreSchema.Columns.EVENT_ID;
    var streamId = EventStoreSchema.Columns.STREAM_ID;
    var aggregateId = EventStoreSchema.Columns.AGGREGATE_ID;
    var aggregateType = EventStoreSchema.Columns.AGGREGATE_TYPE;
    var eventType = EventStoreSchema.Columns.EVENT_TYPE;
    var eventData = EventStoreSchema.Columns.EVENT_DATA;
    var metadata = EventStoreSchema.Columns.METADATA;
    var scope = EventStoreSchema.Columns.SCOPE;
    var version = EventStoreSchema.Columns.VERSION;
    var createdAt = EventStoreSchema.Columns.CREATED_AT;

    // Assert - Verify constants match column names
    await Assert.That(eventId).IsEqualTo("event_id");
    await Assert.That(streamId).IsEqualTo("stream_id");
    await Assert.That(aggregateId).IsEqualTo("aggregate_id");
    await Assert.That(aggregateType).IsEqualTo("aggregate_type");
    await Assert.That(eventType).IsEqualTo("event_type");
    await Assert.That(eventData).IsEqualTo("event_data");
    await Assert.That(metadata).IsEqualTo("metadata");
    await Assert.That(scope).IsEqualTo("scope");
    await Assert.That(version).IsEqualTo("version");
    await Assert.That(createdAt).IsEqualTo("created_at");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ColumnDefaults_ShouldBeCorrectAsync() {
    // Arrange & Act
    var columns = EventStoreSchema.Table.Columns;
    var createdAtColumn = columns.First(c => c.Name == "created_at");

    // Assert - Verify created_at default value
    await Assert.That(createdAtColumn.DefaultValue).IsNotNull();
    await Assert.That(createdAtColumn.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)createdAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DATE_TIME__NOW);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_StreamIdColumn_ShouldBeCorrectAsync() {
    // Arrange & Act
    var columns = EventStoreSchema.Table.Columns;
    var streamIdColumn = columns.First(c => c.Name == "stream_id");

    // Assert
    await Assert.That(streamIdColumn.DataType).IsEqualTo(WhizbangDataType.UUID);
    await Assert.That(streamIdColumn.Nullable).IsFalse();
    await Assert.That(streamIdColumn.PrimaryKey).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ScopeColumn_ShouldBeCorrectAsync() {
    // Arrange & Act
    var columns = EventStoreSchema.Table.Columns;
    var scopeColumn = columns.First(c => c.Name == "scope");

    // Assert
    await Assert.That(scopeColumn.DataType).IsEqualTo(WhizbangDataType.JSON);
    await Assert.That(scopeColumn.Nullable).IsTrue();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldHaveUniqueStreamVersionIndexAsync() {
    // Arrange & Act
    var indexes = EventStoreSchema.Table.Indexes;
    var streamIndex = indexes.FirstOrDefault(i => i.Name == "idx_event_store_stream");

    // Assert
    await Assert.That(streamIndex).IsNotNull();
    await Assert.That(streamIndex!.Unique).IsTrue();
    await Assert.That(streamIndex.Columns).Count().IsEqualTo(2);
    await Assert.That(streamIndex.Columns[0]).IsEqualTo("stream_id");
    await Assert.That(streamIndex.Columns[1]).IsEqualTo("version");
  }
}
