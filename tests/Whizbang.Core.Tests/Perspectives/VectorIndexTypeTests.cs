using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="VectorIndexType"/> enum.
/// Validates enum values for pgvector index types.
/// </summary>
/// <docs>perspectives/vector-fields</docs>
[Category("Core")]
[Category("Enums")]
[Category("Vectors")]
public class VectorIndexTypeTests {
  [Test]
  public async Task VectorIndexType_None_HasValueZeroAsync() {
    // Arrange
    var noneValue = (int)VectorIndexType.None;

    // Assert - No index (exact search)
    await Assert.That(noneValue).IsEqualTo(0);
  }

  [Test]
  public async Task VectorIndexType_IVFFlat_HasValueOneAsync() {
    // Arrange
    var ivfflatValue = (int)VectorIndexType.IVFFlat;

    // Assert - IVFFlat index
    await Assert.That(ivfflatValue).IsEqualTo(1);
  }

  [Test]
  public async Task VectorIndexType_HNSW_HasValueTwoAsync() {
    // Arrange
    var hnswValue = (int)VectorIndexType.HNSW;

    // Assert - HNSW (Hierarchical Navigable Small World) index
    await Assert.That(hnswValue).IsEqualTo(2);
  }

  [Test]
  public async Task VectorIndexType_Default_IsNoneAsync() {
    // Arrange
    VectorIndexType defaultValue = default;

    // Assert - default value should be None (0)
    await Assert.That(defaultValue).IsEqualTo(VectorIndexType.None);
  }

  [Test]
  public async Task VectorIndexType_HasExactlyThreeValuesAsync() {
    // Arrange
    var values = Enum.GetValues<VectorIndexType>();

    // Assert
    await Assert.That(values).Count().IsEqualTo(3);
  }

  [Test]
  public async Task VectorIndexType_AllValuesAreParsableAsync() {
    // Arrange & Act
    var noneParsed = Enum.TryParse<VectorIndexType>("None", out var none);
    var ivfflatParsed = Enum.TryParse<VectorIndexType>("IVFFlat", out var ivfflat);
    var hnswParsed = Enum.TryParse<VectorIndexType>("HNSW", out var hnsw);

    // Assert
    await Assert.That(noneParsed).IsTrue();
    await Assert.That(ivfflatParsed).IsTrue();
    await Assert.That(hnswParsed).IsTrue();
    await Assert.That(none).IsEqualTo(VectorIndexType.None);
    await Assert.That(ivfflat).IsEqualTo(VectorIndexType.IVFFlat);
    await Assert.That(hnsw).IsEqualTo(VectorIndexType.HNSW);
  }
}
