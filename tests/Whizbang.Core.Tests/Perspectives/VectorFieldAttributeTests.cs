using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Helpers;

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
  [Arguments(1)]
  [Arguments(384)]
  [Arguments(768)]
  [Arguments(1536)]
  [Arguments(4096)]
  public async Task VectorFieldAttribute_Constructor_AcceptsValidDimensionsAsync(int dimensions) {
    var attribute = new VectorFieldAttribute(dimensions);
    await Assert.That(attribute.Dimensions).IsEqualTo(dimensions);
  }

  [Test]
  public async Task VectorFieldAttribute_Constructor_HasDefaultValuesAsync() {
    var attribute = new VectorFieldAttribute(768);
    await Assert.That(attribute.DistanceMetric).IsEqualTo(VectorDistanceMetric.Cosine);
    await Assert.That(attribute.Indexed).IsTrue();
    await Assert.That(attribute.IndexType).IsEqualTo(VectorIndexType.IVFFlat);
    await Assert.That(attribute.IndexLists).IsEqualTo(100);
    await Assert.That(attribute.ColumnName).IsNull();
  }

  [Test]
  public async Task VectorFieldAttribute_Constructor_ThrowsForInvalidDimensionsAsync() {
    await Assert.That(() => new VectorFieldAttribute(0)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new VectorFieldAttribute(-1)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task VectorFieldAttribute_Properties_CanBeSetAsync() {
    var attribute = new VectorFieldAttribute(1536) {
      DistanceMetric = VectorDistanceMetric.L2,
      Indexed = false,
      IndexType = VectorIndexType.HNSW,
      IndexLists = 200,
      ColumnName = "embedding_vec"
    };

    await Assert.That(attribute.DistanceMetric).IsEqualTo(VectorDistanceMetric.L2);
    await Assert.That(attribute.Indexed).IsFalse();
    await Assert.That(attribute.IndexType).IsEqualTo(VectorIndexType.HNSW);
    await Assert.That(attribute.IndexLists).IsEqualTo(200);
    await Assert.That(attribute.ColumnName).IsEqualTo("embedding_vec");
  }

  [Test]
  public async Task VectorFieldAttribute_AttributeUsage_PropertyOnly_NotMultiple_IsInheritedAsync() {
    var attributeUsage = AttributeTestHelpers.GetAttributeUsage<VectorFieldAttribute>();
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn).IsEqualTo(AttributeTargets.Property);
    await Assert.That(attributeUsage.AllowMultiple).IsFalse();
    await Assert.That(attributeUsage.Inherited).IsTrue();
  }

  [Test]
  public async Task VectorFieldAttribute_IsSealedAsync() {
    await Assert.That(typeof(VectorFieldAttribute).IsSealed).IsTrue();
  }
}
