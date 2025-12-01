using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;

namespace Whizbang.Data.Schema.Tests;

/// <summary>
/// Tests for IndexDefinition record - defines table indexes.
/// Tests verify property initialization, defaults, and value equality.
/// </summary>
public class IndexDefinitionTests {
  [Test]
  public async Task IndexDefinition_WithRequiredProperties_CreatesInstanceAsync() {
    // Arrange & Act
    var index = new IndexDefinition(
      Name: "idx_created_at",
      Columns: ImmutableArray.Create("created_at")
    );

    // Assert
    await Assert.That(index).IsNotNull();
    await Assert.That(index.Name).IsEqualTo("idx_created_at");
    await Assert.That(index.Columns).HasCount().EqualTo(1);
    await Assert.That(index.Columns[0]).IsEqualTo("created_at");
  }

  [Test]
  public async Task IndexDefinition_WithoutOptionalProperties_UsesDefaultsAsync() {
    // Arrange & Act
    var index = new IndexDefinition(
      Name: "test_idx",
      Columns: ImmutableArray.Create("col1")
    );

    // Assert - Verify default values
    await Assert.That(index.Unique).IsFalse(); // Default: not unique
  }

  [Test]
  public async Task IndexDefinition_WithMultipleColumns_StoresAllAsync() {
    // Arrange & Act
    var index = new IndexDefinition(
      Name: "idx_composite",
      Columns: ImmutableArray.Create("tenant_id", "user_id", "created_at")
    );

    // Assert
    await Assert.That(index.Columns).HasCount().EqualTo(3);
    await Assert.That(index.Columns[0]).IsEqualTo("tenant_id");
    await Assert.That(index.Columns[1]).IsEqualTo("user_id");
    await Assert.That(index.Columns[2]).IsEqualTo("created_at");
  }

  [Test]
  public async Task IndexDefinition_WithUnique_SetsPropertyAsync() {
    // Arrange & Act
    var index = new IndexDefinition(
      Name: "idx_unique_email",
      Columns: ImmutableArray.Create("email"),
      Unique: true
    );

    // Assert
    await Assert.That(index.Unique).IsTrue();
  }

  [Test]
  public async Task IndexDefinition_SameValues_HasMatchingPropertiesAsync() {
    // Arrange
    var index1 = new IndexDefinition(
      Name: "idx_test",
      Columns: ImmutableArray.Create("col1", "col2"),
      Unique: true
    );

    var index2 = new IndexDefinition(
      Name: "idx_test",
      Columns: ImmutableArray.Create("col1", "col2"),
      Unique: true
    );

    // Act & Assert - Check individual properties for value equality
    await Assert.That(index1.Name).IsEqualTo(index2.Name);
    await Assert.That(index1.Unique).IsEqualTo(index2.Unique);
    await Assert.That(index1.Columns.Length).IsEqualTo(index2.Columns.Length);
    await Assert.That(index1.Columns[0]).IsEqualTo(index2.Columns[0]);
    await Assert.That(index1.Columns[1]).IsEqualTo(index2.Columns[1]);
  }

  [Test]
  public async Task IndexDefinition_DifferentName_AreNotEqualAsync() {
    // Arrange
    var index1 = new IndexDefinition("idx1", ImmutableArray.Create("col1"));
    var index2 = new IndexDefinition("idx2", ImmutableArray.Create("col1"));

    // Act & Assert
    await Assert.That(index1).IsNotEqualTo(index2);
  }

  [Test]
  public async Task IndexDefinition_DifferentColumns_AreNotEqualAsync() {
    // Arrange
    var index1 = new IndexDefinition("idx", ImmutableArray.Create("col1"));
    var index2 = new IndexDefinition("idx", ImmutableArray.Create("col2"));

    // Act & Assert
    await Assert.That(index1).IsNotEqualTo(index2);
  }

  [Test]
  public async Task IndexDefinition_DifferentUnique_AreNotEqualAsync() {
    // Arrange
    var index1 = new IndexDefinition("idx", ImmutableArray.Create("col1"), Unique: true);
    var index2 = new IndexDefinition("idx", ImmutableArray.Create("col1"), Unique: false);

    // Act & Assert
    await Assert.That(index1).IsNotEqualTo(index2);
  }

  [Test]
  public async Task IndexDefinition_IsRecordAsync() {
    // Arrange & Act - Records have compiler-generated EqualityContract property
    var hasEqualityContract = typeof(IndexDefinition).GetProperty("EqualityContract",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) != null;

    // Assert
    await Assert.That(hasEqualityContract).IsTrue();
  }
}
