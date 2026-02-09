using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Consolidated tests for perspective-related enums.
/// Validates enum values, defaults, and parsing.
/// </summary>
/// <docs>perspectives/physical-fields</docs>
[Category("Core")]
[Category("Enums")]
public class EnumValueTests {
  #region FieldStorageMode Tests

  [Test]
  [Arguments(FieldStorageMode.JsonOnly, 0)]
  [Arguments(FieldStorageMode.Extracted, 1)]
  [Arguments(FieldStorageMode.Split, 2)]
  public async Task FieldStorageMode_HasExpectedValueAsync(FieldStorageMode mode, int expected) {
    await Assert.That((int)mode).IsEqualTo(expected);
  }

  [Test]
  public async Task FieldStorageMode_Default_IsJsonOnlyAsync() {
    FieldStorageMode defaultValue = default;
    await Assert.That(defaultValue).IsEqualTo(FieldStorageMode.JsonOnly);
  }

  [Test]
  public async Task FieldStorageMode_HasExactlyThreeValuesAsync() {
    await Assert.That(Enum.GetValues<FieldStorageMode>()).Count().IsEqualTo(3);
  }

  #endregion

  #region VectorDistanceMetric Tests

  [Test]
  [Arguments(VectorDistanceMetric.L2, 0)]
  [Arguments(VectorDistanceMetric.InnerProduct, 1)]
  [Arguments(VectorDistanceMetric.Cosine, 2)]
  public async Task VectorDistanceMetric_HasExpectedValueAsync(VectorDistanceMetric metric, int expected) {
    await Assert.That((int)metric).IsEqualTo(expected);
  }

  [Test]
  public async Task VectorDistanceMetric_Default_IsL2Async() {
    VectorDistanceMetric defaultValue = default;
    await Assert.That(defaultValue).IsEqualTo(VectorDistanceMetric.L2);
  }

  [Test]
  public async Task VectorDistanceMetric_HasExactlyThreeValuesAsync() {
    await Assert.That(Enum.GetValues<VectorDistanceMetric>()).Count().IsEqualTo(3);
  }

  #endregion

  #region VectorIndexType Tests

  [Test]
  [Arguments(VectorIndexType.None, 0)]
  [Arguments(VectorIndexType.IVFFlat, 1)]
  [Arguments(VectorIndexType.HNSW, 2)]
  public async Task VectorIndexType_HasExpectedValueAsync(VectorIndexType indexType, int expected) {
    await Assert.That((int)indexType).IsEqualTo(expected);
  }

  [Test]
  public async Task VectorIndexType_Default_IsNoneAsync() {
    VectorIndexType defaultValue = default;
    await Assert.That(defaultValue).IsEqualTo(VectorIndexType.None);
  }

  [Test]
  public async Task VectorIndexType_HasExactlyThreeValuesAsync() {
    await Assert.That(Enum.GetValues<VectorIndexType>()).Count().IsEqualTo(3);
  }

  #endregion
}
