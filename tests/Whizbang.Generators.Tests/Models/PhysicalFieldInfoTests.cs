extern alias shared;
using GeneratorVectorDistanceMetric = shared::Whizbang.Generators.Shared.Models.GeneratorVectorDistanceMetric;
using GeneratorVectorIndexType = shared::Whizbang.Generators.Shared.Models.GeneratorVectorIndexType;
using PhysicalFieldInfo = shared::Whizbang.Generators.Shared.Models.PhysicalFieldInfo;

namespace Whizbang.Generators.Tests.Models;

/// <summary>
/// Tests for <see cref="PhysicalFieldInfo"/> record.
/// Validates value type equality and property storage for incremental generator caching.
/// </summary>
public class PhysicalFieldInfoTests {
  [Test]
  public async Task PhysicalFieldInfo_Constructor_SetsAllPropertiesAsync() {
    // Arrange & Act
    var info = new PhysicalFieldInfo(
        PropertyName: "Price",
        ColumnName: "price",
        TypeName: "System.Decimal",
        IsIndexed: true,
        IsUnique: false,
        MaxLength: null,
        IsVector: false,
        VectorDimensions: null,
        VectorDistanceMetric: null,
        VectorIndexType: null,
        VectorIndexLists: null
    );

    // Assert
    await Assert.That(info.PropertyName).IsEqualTo("Price");
    await Assert.That(info.ColumnName).IsEqualTo("price");
    await Assert.That(info.TypeName).IsEqualTo("System.Decimal");
    await Assert.That(info.IsIndexed).IsTrue();
    await Assert.That(info.IsUnique).IsFalse();
    await Assert.That(info.MaxLength).IsNull();
    await Assert.That(info.IsVector).IsFalse();
    await Assert.That(info.VectorDimensions).IsNull();
    await Assert.That(info.VectorDistanceMetric).IsNull();
    await Assert.That(info.VectorIndexType).IsNull();
    await Assert.That(info.VectorIndexLists).IsNull();
  }

  [Test]
  public async Task PhysicalFieldInfo_VectorField_SetsVectorPropertiesAsync() {
    // Arrange & Act
    var info = new PhysicalFieldInfo(
        PropertyName: "Embedding",
        ColumnName: "embedding",
        TypeName: "System.Single[]",
        IsIndexed: true,
        IsUnique: false,
        MaxLength: null,
        IsVector: true,
        VectorDimensions: 1536,
        VectorDistanceMetric: GeneratorVectorDistanceMetric.Cosine,
        VectorIndexType: GeneratorVectorIndexType.HNSW,
        VectorIndexLists: 100
    );

    // Assert
    await Assert.That(info.IsVector).IsTrue();
    await Assert.That(info.VectorDimensions).IsEqualTo(1536);
    await Assert.That(info.VectorDistanceMetric).IsEqualTo(GeneratorVectorDistanceMetric.Cosine);
    await Assert.That(info.VectorIndexType).IsEqualTo(GeneratorVectorIndexType.HNSW);
    await Assert.That(info.VectorIndexLists).IsEqualTo(100);
  }

  [Test]
  public async Task PhysicalFieldInfo_ValueEquality_MatchesWhenFieldsEqualAsync() {
    // Arrange
    var info1 = new PhysicalFieldInfo(
        PropertyName: "Name",
        ColumnName: "name",
        TypeName: "System.String",
        IsIndexed: true,
        IsUnique: false,
        MaxLength: 200,
        IsVector: false,
        VectorDimensions: null,
        VectorDistanceMetric: null,
        VectorIndexType: null,
        VectorIndexLists: null
    );

    var info2 = new PhysicalFieldInfo(
        PropertyName: "Name",
        ColumnName: "name",
        TypeName: "System.String",
        IsIndexed: true,
        IsUnique: false,
        MaxLength: 200,
        IsVector: false,
        VectorDimensions: null,
        VectorDistanceMetric: null,
        VectorIndexType: null,
        VectorIndexLists: null
    );

    // Act & Assert - Value equality for incremental caching
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }

  [Test]
  public async Task PhysicalFieldInfo_ValueEquality_DiffersWhenFieldsDifferAsync() {
    // Arrange
    var info1 = new PhysicalFieldInfo(
        PropertyName: "Name",
        ColumnName: "name",
        TypeName: "System.String",
        IsIndexed: true,
        IsUnique: false,
        MaxLength: 200,
        IsVector: false,
        VectorDimensions: null,
        VectorDistanceMetric: null,
        VectorIndexType: null,
        VectorIndexLists: null
    );

    var info2 = new PhysicalFieldInfo(
        PropertyName: "Name",
        ColumnName: "name",
        TypeName: "System.String",
        IsIndexed: false, // Different!
        IsUnique: false,
        MaxLength: 200,
        IsVector: false,
        VectorDimensions: null,
        VectorDistanceMetric: null,
        VectorIndexType: null,
        VectorIndexLists: null
    );

    // Act & Assert
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  [Test]
  public async Task PhysicalFieldInfo_WithMaxLength_StoresValueAsync() {
    // Arrange & Act
    var info = new PhysicalFieldInfo(
        PropertyName: "Sku",
        ColumnName: "sku",
        TypeName: "System.String",
        IsIndexed: true,
        IsUnique: true,
        MaxLength: 50,
        IsVector: false,
        VectorDimensions: null,
        VectorDistanceMetric: null,
        VectorIndexType: null,
        VectorIndexLists: null
    );

    // Assert
    await Assert.That(info.MaxLength).IsEqualTo(50);
    await Assert.That(info.IsUnique).IsTrue();
  }

  [Test]
  public async Task PhysicalFieldInfo_VectorWithIVFFlat_StoresIndexTypeAsync() {
    // Arrange & Act
    var info = new PhysicalFieldInfo(
        PropertyName: "ContentEmbedding",
        ColumnName: "content_embedding",
        TypeName: "System.Single[]",
        IsIndexed: true,
        IsUnique: false,
        MaxLength: null,
        IsVector: true,
        VectorDimensions: 768,
        VectorDistanceMetric: GeneratorVectorDistanceMetric.L2,
        VectorIndexType: GeneratorVectorIndexType.IVFFlat,
        VectorIndexLists: 50
    );

    // Assert
    await Assert.That(info.VectorIndexType).IsEqualTo(GeneratorVectorIndexType.IVFFlat);
    await Assert.That(info.VectorDistanceMetric).IsEqualTo(GeneratorVectorDistanceMetric.L2);
    await Assert.That(info.VectorIndexLists).IsEqualTo(50);
  }

  [Test]
  public async Task PhysicalFieldInfo_VectorNoIndex_HasNoneIndexTypeAsync() {
    // Arrange & Act
    var info = new PhysicalFieldInfo(
        PropertyName: "TempEmbedding",
        ColumnName: "temp_embedding",
        TypeName: "System.Single[]",
        IsIndexed: false,
        IsUnique: false,
        MaxLength: null,
        IsVector: true,
        VectorDimensions: 256,
        VectorDistanceMetric: GeneratorVectorDistanceMetric.InnerProduct,
        VectorIndexType: GeneratorVectorIndexType.None,
        VectorIndexLists: null
    );

    // Assert
    await Assert.That(info.IsIndexed).IsFalse();
    await Assert.That(info.VectorIndexType).IsEqualTo(GeneratorVectorIndexType.None);
    await Assert.That(info.VectorIndexLists).IsNull();
  }
}
