using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="TemporalActionType"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Perspectives/TemporalActionType.cs</tests>
public class TemporalActionTypeTests {
  [Test]
  public async Task TemporalActionType_Insert_IsDefinedAsync() {
    var value = TemporalActionType.Insert;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task TemporalActionType_Update_IsDefinedAsync() {
    var value = TemporalActionType.Update;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task TemporalActionType_Delete_IsDefinedAsync() {
    var value = TemporalActionType.Delete;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task TemporalActionType_HasThreeValuesAsync() {
    var values = Enum.GetValues<TemporalActionType>();
    await Assert.That(values.Length).IsEqualTo(3);
  }

  [Test]
  public async Task TemporalActionType_Insert_HasCorrectIntValueAsync() {
    var value = (int)TemporalActionType.Insert;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task TemporalActionType_Update_HasCorrectIntValueAsync() {
    var value = (int)TemporalActionType.Update;
    await Assert.That(value).IsEqualTo(1);
  }

  [Test]
  public async Task TemporalActionType_Delete_HasCorrectIntValueAsync() {
    var value = (int)TemporalActionType.Delete;
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task TemporalActionType_Insert_IsDefaultAsync() {
    var value = default(TemporalActionType);
    await Assert.That(value).IsEqualTo(TemporalActionType.Insert);
  }
}
