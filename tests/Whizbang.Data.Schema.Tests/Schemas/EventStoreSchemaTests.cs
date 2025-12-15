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
    await Assert.That(columns).HasCount().EqualTo(9);

    // Verify each column definition
    var eventId = columns[0];
    await Assert.That(eventId.Name).IsEqualTo("event_id");
    await Assert.That(eventId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(eventId.PrimaryKey).IsTrue();
    await Assert.That(eventId.Nullable).IsFalse();

    var aggregateId = columns[1];
    await Assert.That(aggregateId.Name).IsEqualTo("aggregate_id");
    await Assert.That(aggregateId.DataType).IsEqualTo(WhizbangDataType.Uuid);
    await Assert.That(aggregateId.Nullable).IsFalse();

    var aggregateType = columns[2];
    await Assert.That(aggregateType.Name).IsEqualTo("aggregate_type");
    await Assert.That(aggregateType.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(aggregateType.MaxLength).IsEqualTo(500);
    await Assert.That(aggregateType.Nullable).IsFalse();

    var eventType = columns[3];
    await Assert.That(eventType.Name).IsEqualTo("event_type");
    await Assert.That(eventType.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(eventType.MaxLength).IsEqualTo(500);
    await Assert.That(eventType.Nullable).IsFalse();

    var eventData = columns[4];
    await Assert.That(eventData.Name).IsEqualTo("event_data");
    await Assert.That(eventData.DataType).IsEqualTo(WhizbangDataType.Json);
    await Assert.That(eventData.Nullable).IsFalse();

    var metadata = columns[5];
    await Assert.That(metadata.Name).IsEqualTo("metadata");
    await Assert.That(metadata.DataType).IsEqualTo(WhizbangDataType.Json);
    await Assert.That(metadata.Nullable).IsFalse();

    var sequenceNumber = columns[6];
    await Assert.That(sequenceNumber.Name).IsEqualTo("sequence_number");
    await Assert.That(sequenceNumber.DataType).IsEqualTo(WhizbangDataType.BigInt);
    await Assert.That(sequenceNumber.Nullable).IsFalse();

    var version = columns[7];
    await Assert.That(version.Name).IsEqualTo("version");
    await Assert.That(version.DataType).IsEqualTo(WhizbangDataType.Integer);
    await Assert.That(version.Nullable).IsFalse();

    var createdAt = columns[8];
    await Assert.That(createdAt.Name).IsEqualTo("created_at");
    await Assert.That(createdAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(createdAt.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_ShouldDefineCorrectIndexesAsync() {
    // Arrange & Act
    var indexes = EventStoreSchema.Table.Indexes;

    // Assert - Verify index count
    await Assert.That(indexes).HasCount().EqualTo(3);

    // Verify unique aggregate index
    var aggregateIndex = indexes[0];
    await Assert.That(aggregateIndex.Name).IsEqualTo("idx_event_store_aggregate");
    await Assert.That(aggregateIndex.Columns).HasCount().EqualTo(2);
    await Assert.That(aggregateIndex.Columns[0]).IsEqualTo("aggregate_id");
    await Assert.That(aggregateIndex.Columns[1]).IsEqualTo("version");
    await Assert.That(aggregateIndex.Unique).IsTrue();

    // Verify aggregate_type index
    var aggregateTypeIndex = indexes[1];
    await Assert.That(aggregateTypeIndex.Name).IsEqualTo("idx_event_store_aggregate_type");
    await Assert.That(aggregateTypeIndex.Columns).HasCount().EqualTo(2);
    await Assert.That(aggregateTypeIndex.Columns[0]).IsEqualTo("aggregate_type");
    await Assert.That(aggregateTypeIndex.Columns[1]).IsEqualTo("created_at");

    // Verify sequence index
    var sequenceIndex = indexes[2];
    await Assert.That(sequenceIndex.Name).IsEqualTo("idx_event_store_sequence");
    await Assert.That(sequenceIndex.Columns).HasCount().EqualTo(1);
    await Assert.That(sequenceIndex.Columns[0]).IsEqualTo("sequence_number");
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
    await Assert.That(primaryKeyColumn.DataType).IsEqualTo(WhizbangDataType.Uuid);
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
    await Assert.That(aggregateIndex.Columns).HasCount().EqualTo(2);
    await Assert.That(aggregateIndex.Columns[0]).IsEqualTo("aggregate_id");
    await Assert.That(aggregateIndex.Columns[1]).IsEqualTo("version");
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_ShouldProvideAllConstantsAsync() {
    // Arrange & Act - Get all column constants
    var eventId = EventStoreSchema.Columns.EventId;
    var aggregateId = EventStoreSchema.Columns.AggregateId;
    var aggregateType = EventStoreSchema.Columns.AggregateType;
    var eventType = EventStoreSchema.Columns.EventType;
    var eventData = EventStoreSchema.Columns.EventData;
    var metadata = EventStoreSchema.Columns.Metadata;
    var sequenceNumber = EventStoreSchema.Columns.SequenceNumber;
    var version = EventStoreSchema.Columns.Version;
    var createdAt = EventStoreSchema.Columns.CreatedAt;

    // Assert - Verify constants match column names
    await Assert.That(eventId).IsEqualTo("event_id");
    await Assert.That(aggregateId).IsEqualTo("aggregate_id");
    await Assert.That(aggregateType).IsEqualTo("aggregate_type");
    await Assert.That(eventType).IsEqualTo("event_type");
    await Assert.That(eventData).IsEqualTo("event_data");
    await Assert.That(metadata).IsEqualTo("metadata");
    await Assert.That(sequenceNumber).IsEqualTo("sequence_number");
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
    await Assert.That(((FunctionDefault)createdAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DateTime_Now);
  }
}
