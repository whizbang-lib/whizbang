using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;

namespace Whizbang.Data.Schema.Tests;

/// <summary>
/// Tests for TableDefinition record - complete table schema.
/// Tests verify property initialization and value equality.
/// </summary>
public class TableDefinitionTests {
  [Test]
  public async Task TableDefinition_WithRequiredProperties_CreatesInstanceAsync() {
    // Arrange
    var columns = ImmutableArray.Create(
      new ColumnDefinition("id", WhizbangDataType.Uuid, PrimaryKey: true),
      new ColumnDefinition("name", WhizbangDataType.String, MaxLength: 100)
    );

    // Act
    var table = new TableDefinition(
      Name: "users",
      Columns: columns
    );

    // Assert
    await Assert.That(table).IsNotNull();
    await Assert.That(table.Name).IsEqualTo("users");
    await Assert.That(table.Columns).HasCount().EqualTo(2);
  }

  [Test]
  public async Task TableDefinition_WithoutIndexes_UsesDefaultAsync() {
    // Arrange
    var columns = ImmutableArray.Create(
      new ColumnDefinition("id", WhizbangDataType.Uuid)
    );

    // Act
    var table = new TableDefinition("test", columns);

    // Assert - Default empty indexes array
    await Assert.That(table.Indexes).IsNotNull();
    await Assert.That(table.Indexes).HasCount().EqualTo(0);
  }

  [Test]
  public async Task TableDefinition_WithIndexes_StoresAllAsync() {
    // Arrange
    var columns = ImmutableArray.Create(
      new ColumnDefinition("id", WhizbangDataType.Uuid, PrimaryKey: true),
      new ColumnDefinition("email", WhizbangDataType.String, MaxLength: 255),
      new ColumnDefinition("created_at", WhizbangDataType.TimestampTz)
    );

    var indexes = ImmutableArray.Create(
      new IndexDefinition("idx_email", ImmutableArray.Create("email"), Unique: true),
      new IndexDefinition("idx_created_at", ImmutableArray.Create("created_at"))
    );

    // Act
    var table = new TableDefinition(
      Name: "users",
      Columns: columns,
      Indexes: indexes
    );

    // Assert
    await Assert.That(table.Indexes).HasCount().EqualTo(2);
    await Assert.That(table.Indexes[0].Name).IsEqualTo("idx_email");
    await Assert.That(table.Indexes[1].Name).IsEqualTo("idx_created_at");
  }

  [Test]
  public async Task TableDefinition_SameValues_AreEqualAsync() {
    // Arrange
    var columns = ImmutableArray.Create(
      new ColumnDefinition("id", WhizbangDataType.Uuid)
    );

    var table1 = new TableDefinition("test", columns);
    var table2 = new TableDefinition("test", columns);

    // Act & Assert
    await Assert.That(table1).IsEqualTo(table2);
  }

  [Test]
  public async Task TableDefinition_DifferentName_AreNotEqualAsync() {
    // Arrange
    var columns = ImmutableArray.Create(
      new ColumnDefinition("id", WhizbangDataType.Uuid)
    );

    var table1 = new TableDefinition("table1", columns);
    var table2 = new TableDefinition("table2", columns);

    // Act & Assert
    await Assert.That(table1).IsNotEqualTo(table2);
  }

  [Test]
  public async Task TableDefinition_IsRecordAsync() {
    // Arrange & Act - Records have compiler-generated EqualityContract property
    var hasEqualityContract = typeof(TableDefinition).GetProperty("EqualityContract",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) != null;

    // Assert
    await Assert.That(hasEqualityContract).IsTrue();
  }
}
