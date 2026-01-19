using TUnit.Assertions.Extensions;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for ModelTypeInfo - validates metadata container for perspective model types.
/// This type is used by EFCoreServiceRegistrationGenerator to track discovered models.
/// </summary>
public class ModelTypeInfoTests {
  /// <summary>
  /// Test that ModelTypeInfo can be constructed with valid parameters.
  /// Record should store Type and TableName correctly.
  /// </summary>
  [Test]
  public async Task ModelTypeInfo_Constructor_StoresTypeAndTableNameAsync() {
    // Arrange
    var modelType = typeof(TestModel);
    var tableName = "test_model";

    // Act
    var info = new ModelTypeInfo(modelType, tableName);

    // Assert
    await Assert.That(info.Type).IsEqualTo(modelType);
    await Assert.That(info.TableName).IsEqualTo(tableName);
  }

  /// <summary>
  /// Test that two ModelTypeInfo instances with same values are equal (value semantics).
  /// Records should provide structural equality by default.
  /// </summary>
  [Test]
  public async Task ModelTypeInfo_Equality_TwoInstancesWithSameValues_AreEqualAsync() {
    // Arrange
    var modelType = typeof(TestModel);
    var tableName = "test_model";

    var info1 = new ModelTypeInfo(modelType, tableName);
    var info2 = new ModelTypeInfo(modelType, tableName);

    // Act & Assert
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }

  /// <summary>
  /// Test that two ModelTypeInfo instances with different values are not equal.
  /// Records should correctly distinguish different instances.
  /// </summary>
  [Test]
  public async Task ModelTypeInfo_Equality_TwoInstancesWithDifferentValues_AreNotEqualAsync() {
    // Arrange
    var info1 = new ModelTypeInfo(typeof(TestModel), "test_model");
    var info2 = new ModelTypeInfo(typeof(TestModel), "different_table");

    // Act & Assert
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  /// <summary>
  /// Test that ModelTypeInfo can be deconstructed using record syntax.
  /// Should support positional deconstruction.
  /// </summary>
  [Test]
  public async Task ModelTypeInfo_Deconstruction_ExtractsTypeAndTableNameAsync() {
    // Arrange
    var modelType = typeof(TestModel);
    var tableName = "test_model";
    var info = new ModelTypeInfo(modelType, tableName);

    // Act
    var (type, table) = info;

    // Assert
    await Assert.That(type).IsEqualTo(modelType);
    await Assert.That(table).IsEqualTo(tableName);
  }

  /// <summary>
  /// Test model class used for testing ModelTypeInfo.
  /// </summary>
  private sealed record TestModel {
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
  }
}
