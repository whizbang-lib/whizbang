using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for <see cref="FilterMode"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Lenses/FilterMode.cs</tests>
public class FilterModeTests {
  [Test]
  public async Task FilterMode_Equals_IsDefinedAsync() {
    var value = FilterMode.Equals;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task FilterMode_In_IsDefinedAsync() {
    var value = FilterMode.In;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task FilterMode_HasTwoValuesAsync() {
    var values = Enum.GetValues<FilterMode>();
    await Assert.That(values.Length).IsEqualTo(2);
  }

  [Test]
  public async Task FilterMode_Equals_HasCorrectIntValueAsync() {
    var value = (int)FilterMode.Equals;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task FilterMode_In_HasCorrectIntValueAsync() {
    var value = (int)FilterMode.In;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task FilterMode_Equals_IsDefaultAsync() {
    var value = default(FilterMode);
    await Assert.That(value).IsEqualTo(FilterMode.Equals);
  }
}
