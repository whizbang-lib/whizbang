using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;

namespace Whizbang.Data.Schema.Tests;

/// <summary>
/// Tests for ColumnDefinition record - building block for table schemas.
/// Tests verify property initialization, defaults, and value equality.
/// </summary>
public class ColumnDefinitionTests {
  [Test]
  public async Task ColumnDefinition_WithRequiredProperties_CreatesInstanceAsync() {
    // Arrange & Act
    var column = new ColumnDefinition(
      Name: "message_id",
      DataType: WhizbangDataType.UUID
    );

    // Assert
    await Assert.That(column).IsNotNull();
    await Assert.That(column.Name).IsEqualTo("message_id");
    await Assert.That(column.DataType).IsEqualTo(WhizbangDataType.UUID);
  }

  [Test]
  public async Task ColumnDefinition_WithoutOptionalProperties_UsesDefaultsAsync() {
    // Arrange & Act
    var column = new ColumnDefinition(
      Name: "test_column",
      DataType: WhizbangDataType.STRING
    );

    // Assert - Verify default values
    await Assert.That(column.Nullable).IsFalse(); // Default: not nullable
    await Assert.That(column.PrimaryKey).IsFalse(); // Default: not primary key
    await Assert.That(column.Unique).IsFalse(); // Default: not unique
    await Assert.That(column.MaxLength).IsNull(); // Default: no max length
    await Assert.That(column.DefaultValue).IsNull(); // Default: no default
  }

  [Test]
  public async Task ColumnDefinition_WithPrimaryKey_SetsPropertyAsync() {
    // Arrange & Act
    var column = new ColumnDefinition(
      Name: "id",
      DataType: WhizbangDataType.UUID,
      PrimaryKey: true
    );

    // Assert
    await Assert.That(column.PrimaryKey).IsTrue();
  }

  [Test]
  public async Task ColumnDefinition_WithNullable_SetsPropertyAsync() {
    // Arrange & Act
    var column = new ColumnDefinition(
      Name: "optional_field",
      DataType: WhizbangDataType.STRING,
      Nullable: true
    );

    // Assert
    await Assert.That(column.Nullable).IsTrue();
  }

  [Test]
  public async Task ColumnDefinition_WithUnique_SetsPropertyAsync() {
    // Arrange & Act
    var column = new ColumnDefinition(
      Name: "email",
      DataType: WhizbangDataType.STRING,
      Unique: true
    );

    // Assert
    await Assert.That(column.Unique).IsTrue();
  }

  [Test]
  public async Task ColumnDefinition_WithMaxLength_SetsPropertyAsync() {
    // Arrange & Act
    var column = new ColumnDefinition(
      Name: "status",
      DataType: WhizbangDataType.STRING,
      MaxLength: 50
    );

    // Assert
    await Assert.That(column.MaxLength).IsEqualTo(50);
  }

  [Test]
  public async Task ColumnDefinition_WithDefaultValue_SetsPropertyAsync() {
    // Arrange
    var defaultValue = DefaultValue.Integer(0);

    // Act
    var column = new ColumnDefinition(
      Name: "attempts",
      DataType: WhizbangDataType.INTEGER,
      DefaultValue: defaultValue
    );

    // Assert
    await Assert.That(column.DefaultValue).IsEqualTo(defaultValue);
  }

  [Test]
  public async Task ColumnDefinition_WithAllProperties_SetsAllAsync() {
    // Arrange
    var defaultValue = DefaultValue.String("Pending");

    // Act
    var column = new ColumnDefinition(
      Name: "status",
      DataType: WhizbangDataType.STRING,
      Nullable: false,
      PrimaryKey: false,
      Unique: true,
      MaxLength: 100,
      DefaultValue: defaultValue
    );

    // Assert
    await Assert.That(column.Name).IsEqualTo("status");
    await Assert.That(column.DataType).IsEqualTo(WhizbangDataType.STRING);
    await Assert.That(column.Nullable).IsFalse();
    await Assert.That(column.PrimaryKey).IsFalse();
    await Assert.That(column.Unique).IsTrue();
    await Assert.That(column.MaxLength).IsEqualTo(100);
    await Assert.That(column.DefaultValue).IsEqualTo(defaultValue);
  }

  [Test]
  public async Task ColumnDefinition_SameValues_AreEqualAsync() {
    // Arrange
    var column1 = new ColumnDefinition(
      Name: "test",
      DataType: WhizbangDataType.STRING,
      MaxLength: 50
    );

    var column2 = new ColumnDefinition(
      Name: "test",
      DataType: WhizbangDataType.STRING,
      MaxLength: 50
    );

    // Act & Assert
    await Assert.That(column1).IsEqualTo(column2);
  }

  [Test]
  public async Task ColumnDefinition_DifferentName_AreNotEqualAsync() {
    // Arrange
    var column1 = new ColumnDefinition("test1", WhizbangDataType.STRING);
    var column2 = new ColumnDefinition("test2", WhizbangDataType.STRING);

    // Act & Assert
    await Assert.That(column1).IsNotEqualTo(column2);
  }

  [Test]
  public async Task ColumnDefinition_DifferentDataType_AreNotEqualAsync() {
    // Arrange
    var column1 = new ColumnDefinition("test", WhizbangDataType.STRING);
    var column2 = new ColumnDefinition("test", WhizbangDataType.INTEGER);

    // Act & Assert
    await Assert.That(column1).IsNotEqualTo(column2);
  }

  [Test]
  public async Task ColumnDefinition_IsRecordAsync() {
    // Arrange & Act - Records have compiler-generated EqualityContract property
    var hasEqualityContract = typeof(ColumnDefinition).GetProperty("EqualityContract",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) != null;

    // Assert
    await Assert.That(hasEqualityContract).IsTrue();
  }
}
