using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="VectorDistanceMetric"/> enum.
/// Validates enum values for pgvector distance operators.
/// </summary>
/// <docs>perspectives/vector-fields</docs>
[Category("Core")]
[Category("Enums")]
[Category("Vectors")]
public class VectorDistanceMetricTests {
  [Test]
  public async Task VectorDistanceMetric_L2_HasValueZeroAsync() {
    // Arrange
    var l2Value = (int)VectorDistanceMetric.L2;

    // Assert - L2 (Euclidean) distance
    await Assert.That(l2Value).IsEqualTo(0);
  }

  [Test]
  public async Task VectorDistanceMetric_InnerProduct_HasValueOneAsync() {
    // Arrange
    var innerProductValue = (int)VectorDistanceMetric.InnerProduct;

    // Assert - Inner product (negative for ordering)
    await Assert.That(innerProductValue).IsEqualTo(1);
  }

  [Test]
  public async Task VectorDistanceMetric_Cosine_HasValueTwoAsync() {
    // Arrange
    var cosineValue = (int)VectorDistanceMetric.Cosine;

    // Assert - Cosine distance (1 - cosine_similarity)
    await Assert.That(cosineValue).IsEqualTo(2);
  }

  [Test]
  public async Task VectorDistanceMetric_Default_IsL2Async() {
    // Arrange
    VectorDistanceMetric defaultValue = default;

    // Assert - default value should be L2 (0)
    await Assert.That(defaultValue).IsEqualTo(VectorDistanceMetric.L2);
  }

  [Test]
  public async Task VectorDistanceMetric_HasExactlyThreeValuesAsync() {
    // Arrange
    var values = Enum.GetValues<VectorDistanceMetric>();

    // Assert
    await Assert.That(values).Count().IsEqualTo(3);
  }

  [Test]
  public async Task VectorDistanceMetric_AllValuesAreParsableAsync() {
    // Arrange & Act
    var l2Parsed = Enum.TryParse<VectorDistanceMetric>("L2", out var l2);
    var innerProductParsed = Enum.TryParse<VectorDistanceMetric>("InnerProduct", out var innerProduct);
    var cosineParsed = Enum.TryParse<VectorDistanceMetric>("Cosine", out var cosine);

    // Assert
    await Assert.That(l2Parsed).IsTrue();
    await Assert.That(innerProductParsed).IsTrue();
    await Assert.That(cosineParsed).IsTrue();
    await Assert.That(l2).IsEqualTo(VectorDistanceMetric.L2);
    await Assert.That(innerProduct).IsEqualTo(VectorDistanceMetric.InnerProduct);
    await Assert.That(cosine).IsEqualTo(VectorDistanceMetric.Cosine);
  }
}
