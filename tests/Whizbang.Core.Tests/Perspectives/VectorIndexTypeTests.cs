using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="VectorIndexType"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Perspectives/VectorIndexType.cs</tests>
public class VectorIndexTypeTests {
  [Test]
  public async Task VectorIndexType_None_IsDefinedAsync() {
    var value = VectorIndexType.None;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task VectorIndexType_IVFFlat_IsDefinedAsync() {
    var value = VectorIndexType.IVFFlat;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task VectorIndexType_HNSW_IsDefinedAsync() {
    var value = VectorIndexType.HNSW;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task VectorIndexType_HasThreeValuesAsync() {
    var values = Enum.GetValues<VectorIndexType>();
    await Assert.That(values.Length).IsEqualTo(3);
  }

  [Test]
  public async Task VectorIndexType_None_HasCorrectIntValueAsync() {
    var value = (int)VectorIndexType.None;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task VectorIndexType_IVFFlat_HasCorrectIntValueAsync() {
    var value = (int)VectorIndexType.IVFFlat;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task VectorIndexType_HNSW_HasCorrectIntValueAsync() {
    var value = (int)VectorIndexType.HNSW;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task VectorIndexType_None_IsDefaultAsync() {
    var value = default(VectorIndexType);
    await Assert.That(value).IsEqualTo(VectorIndexType.None);
  }
}
