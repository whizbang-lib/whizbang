using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="VectorFieldAttribute"/>.
/// Validates attribute behavior, properties, and targeting rules.
/// </summary>
/// <docs>perspectives/vector-fields</docs>
[Category("Core")]
[Category("Attributes")]
[Category("PhysicalFields")]
[Category("Vectors")]
public class VectorFieldAttributeTests {
  [Test]
  public async Task VectorFieldAttribute_Constructor_SetsDimensionsAsync() {
    // Arrange & Act
    var attribute = new VectorFieldAttribute(1536);

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute.Dimensions).IsEqualTo(1536);
  }

  [Test]
  public async Task VectorFieldAttribute_Constructor_HasDefaultValuesAsync() {
    // Arrange & Act
    var attribute = new VectorFieldAttribute(768);

    // Assert - verify defaults
    await Assert.That(attribute.DistanceMetric).IsEqualTo(VectorDistanceMetric.Cosine);
    await Assert.That(attribute.Indexed).IsTrue();
    await Assert.That(attribute.IndexType).IsEqualTo(VectorIndexType.IVFFlat);
    await Assert.That(attribute.IndexLists).IsEqualTo(100);
    await Assert.That(attribute.ColumnName).IsNull();
  }

  [Test]
  public async Task VectorFieldAttribute_Constructor_ThrowsForZeroDimensionsAsync() {
    // Arrange & Act & Assert
    await Assert.That(() => new VectorFieldAttribute(0))
        .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task VectorFieldAttribute_Constructor_ThrowsForNegativeDimensionsAsync() {
    // Arrange & Act & Assert
    await Assert.That(() => new VectorFieldAttribute(-1))
        .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task VectorFieldAttribute_DistanceMetric_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new VectorFieldAttribute(1536) { DistanceMetric = VectorDistanceMetric.L2 };

    // Assert
    await Assert.That(attribute.DistanceMetric).IsEqualTo(VectorDistanceMetric.L2);
  }

  [Test]
  public async Task VectorFieldAttribute_Indexed_CanBeDisabledAsync() {
    // Arrange & Act
    var attribute = new VectorFieldAttribute(1536) { Indexed = false };

    // Assert
    await Assert.That(attribute.Indexed).IsFalse();
  }

  [Test]
  public async Task VectorFieldAttribute_IndexType_CanBeSetToHNSWAsync() {
    // Arrange & Act
    var attribute = new VectorFieldAttribute(1536) { IndexType = VectorIndexType.HNSW };

    // Assert
    await Assert.That(attribute.IndexType).IsEqualTo(VectorIndexType.HNSW);
  }

  [Test]
  public async Task VectorFieldAttribute_IndexLists_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new VectorFieldAttribute(1536) { IndexLists = 200 };

    // Assert
    await Assert.That(attribute.IndexLists).IsEqualTo(200);
  }

  [Test]
  public async Task VectorFieldAttribute_ColumnName_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new VectorFieldAttribute(1536) { ColumnName = "embedding_vec" };

    // Assert
    await Assert.That(attribute.ColumnName).IsEqualTo("embedding_vec");
  }

  [Test]
  public async Task VectorFieldAttribute_AttributeUsage_AllowsPropertyTargetOnlyAsync() {
    // Arrange & Act
    var attributeUsage = typeof(VectorFieldAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn).IsEqualTo(AttributeTargets.Property);
  }

  [Test]
  public async Task VectorFieldAttribute_AttributeUsage_DoesNotAllowMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(VectorFieldAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task VectorFieldAttribute_AttributeUsage_IsInheritedAsync() {
    // Arrange & Act
    var attributeUsage = typeof(VectorFieldAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.Inherited).IsTrue();
  }

  [Test]
  public async Task VectorFieldAttribute_IsSealedAsync() {
    // Assert - attribute class should be sealed for performance
    await Assert.That(typeof(VectorFieldAttribute).IsSealed).IsTrue();
  }

  [Test]
  [Arguments(1)]
  [Arguments(384)]
  [Arguments(768)]
  [Arguments(1536)]
  [Arguments(4096)]
  public async Task VectorFieldAttribute_Constructor_AcceptsValidDimensionsAsync(int dimensions) {
    // Arrange & Act
    var attribute = new VectorFieldAttribute(dimensions);

    // Assert
    await Assert.That(attribute.Dimensions).IsEqualTo(dimensions);
  }
}
