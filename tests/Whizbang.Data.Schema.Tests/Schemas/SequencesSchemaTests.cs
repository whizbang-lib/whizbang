using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema.Schemas;

namespace Whizbang.Data.Schema.Tests.Schemas;

/// <summary>
/// Tests for SequencesSchema - distributed sequence generation table schema.
/// Tests verify table definition structure, columns, types, constraints, and indexes.
/// </summary>

public class SequencesSchemaTests {
  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectNameAsync() {
    // Arrange & Act
    var tableName = SequencesSchema.Table.Name;

    // Assert
    await Assert.That(tableName).IsEqualTo("sequences");
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasCorrectColumnsAsync() {
    // Arrange & Act
    var columns = SequencesSchema.Table.Columns;

    // Assert - Verify column count
    await Assert.That(columns).Count().IsEqualTo(4);

    // Verify sequence_name column
    var sequenceName = columns[0];
    await Assert.That(sequenceName.Name).IsEqualTo("sequence_name");
    await Assert.That(sequenceName.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(sequenceName.MaxLength).IsEqualTo(200);
    await Assert.That(sequenceName.PrimaryKey).IsTrue();
    await Assert.That(sequenceName.Nullable).IsFalse();

    // Verify current_value column
    var currentValue = columns[1];
    await Assert.That(currentValue.Name).IsEqualTo("current_value");
    await Assert.That(currentValue.DataType).IsEqualTo(WhizbangDataType.BigInt);
    await Assert.That(currentValue.Nullable).IsFalse();

    // Verify increment_by column
    var incrementBy = columns[2];
    await Assert.That(incrementBy.Name).IsEqualTo("increment_by");
    await Assert.That(incrementBy.DataType).IsEqualTo(WhizbangDataType.Integer);
    await Assert.That(incrementBy.Nullable).IsFalse();

    // Verify last_updated_at column
    var lastUpdatedAt = columns[3];
    await Assert.That(lastUpdatedAt.Name).IsEqualTo("last_updated_at");
    await Assert.That(lastUpdatedAt.DataType).IsEqualTo(WhizbangDataType.TimestampTz);
    await Assert.That(lastUpdatedAt.Nullable).IsFalse();
  }

  [Test]
  [Category("Schema")]
  public async Task Table_SequenceName_IsPrimaryKeyAsync() {
    // Arrange & Act
    var columns = SequencesSchema.Table.Columns;
    var primaryKeyColumn = columns.FirstOrDefault(c => c.PrimaryKey);

    // Assert
    await Assert.That(primaryKeyColumn).IsNotNull();
    await Assert.That(primaryKeyColumn!.Name).IsEqualTo("sequence_name");
    await Assert.That(primaryKeyColumn.DataType).IsEqualTo(WhizbangDataType.String);
    await Assert.That(primaryKeyColumn.MaxLength).IsEqualTo(200);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_HasNoAdditionalIndexesAsync() {
    // Arrange & Act
    var indexes = SequencesSchema.Table.Indexes;

    // Assert - Verify no additional indexes (primary key only)
    await Assert.That(indexes).Count().IsEqualTo(0);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_CurrentValue_HasDefaultZeroAsync() {
    // Arrange & Act
    var columns = SequencesSchema.Table.Columns;
    var currentValueColumn = columns.First(c => c.Name == "current_value");

    // Assert
    await Assert.That(currentValueColumn.DefaultValue).IsNotNull();
    await Assert.That(currentValueColumn.DefaultValue).IsTypeOf<IntegerDefault>();
    await Assert.That(((IntegerDefault)currentValueColumn.DefaultValue!).Value).IsEqualTo(0);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_IncrementBy_HasDefaultOneAsync() {
    // Arrange & Act
    var columns = SequencesSchema.Table.Columns;
    var incrementByColumn = columns.First(c => c.Name == "increment_by");

    // Assert
    await Assert.That(incrementByColumn.DefaultValue).IsNotNull();
    await Assert.That(incrementByColumn.DefaultValue).IsTypeOf<IntegerDefault>();
    await Assert.That(((IntegerDefault)incrementByColumn.DefaultValue!).Value).IsEqualTo(1);
  }

  [Test]
  [Category("Schema")]
  public async Task Table_LastUpdatedAt_HasDefaultNowAsync() {
    // Arrange & Act
    var columns = SequencesSchema.Table.Columns;
    var lastUpdatedAtColumn = columns.First(c => c.Name == "last_updated_at");

    // Assert
    await Assert.That(lastUpdatedAtColumn.DefaultValue).IsNotNull();
    await Assert.That(lastUpdatedAtColumn.DefaultValue).IsTypeOf<FunctionDefault>();
    await Assert.That(((FunctionDefault)lastUpdatedAtColumn.DefaultValue!).FunctionType).IsEqualTo(DefaultValueFunction.DateTime_Now);
  }

  [Test]
  [Category("Schema")]
  public async Task Columns_HasAllConstantsAsync() {
    // Arrange & Act - Get all column constants
    var sequenceName = SequencesSchema.Columns.SequenceName;
    var currentValue = SequencesSchema.Columns.CurrentValue;
    var incrementBy = SequencesSchema.Columns.IncrementBy;
    var lastUpdatedAt = SequencesSchema.Columns.LastUpdatedAt;

    // Assert - Verify constants match column names
    await Assert.That(sequenceName).IsEqualTo("sequence_name");
    await Assert.That(currentValue).IsEqualTo("current_value");
    await Assert.That(incrementBy).IsEqualTo("increment_by");
    await Assert.That(lastUpdatedAt).IsEqualTo("last_updated_at");
  }
}
