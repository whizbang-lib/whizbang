using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="VectorDistanceMetric"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Perspectives/VectorDistanceMetric.cs</tests>
public class VectorDistanceMetricTests {
  [Test]
  public async Task VectorDistanceMetric_L2_IsDefinedAsync() {
    var value = VectorDistanceMetric.L2;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task VectorDistanceMetric_InnerProduct_IsDefinedAsync() {
    var value = VectorDistanceMetric.InnerProduct;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task VectorDistanceMetric_Cosine_IsDefinedAsync() {
    var value = VectorDistanceMetric.Cosine;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task VectorDistanceMetric_HasThreeValuesAsync() {
    var values = Enum.GetValues<VectorDistanceMetric>();
    await Assert.That(values.Length).IsEqualTo(3);
  }

  [Test]
  public async Task VectorDistanceMetric_L2_HasCorrectIntValueAsync() {
    var value = (int)VectorDistanceMetric.L2;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task VectorDistanceMetric_InnerProduct_HasCorrectIntValueAsync() {
    var value = (int)VectorDistanceMetric.InnerProduct;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task VectorDistanceMetric_Cosine_HasCorrectIntValueAsync() {
    var value = (int)VectorDistanceMetric.Cosine;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task VectorDistanceMetric_L2_IsDefaultAsync() {
    var value = default(VectorDistanceMetric);
    await Assert.That(value).IsEqualTo(VectorDistanceMetric.L2);
  }
}
