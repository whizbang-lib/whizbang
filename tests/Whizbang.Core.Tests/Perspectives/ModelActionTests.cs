using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="ModelAction"/> enum.
/// </summary>
/// <docs>core-concepts/model-action</docs>
public class ModelActionTests {
  [Test]
  public async Task ModelAction_None_HasValueZeroAsync() {
    // Arrange
    var action = ModelAction.None;

    // Assert - None should be 0 (default value)
    await Assert.That((int)action).IsEqualTo(0);
  }

  [Test]
  public async Task ModelAction_Delete_HasValueOneAsync() {
    // Arrange
    var action = ModelAction.Delete;

    // Assert - Delete should be 1
    await Assert.That((int)action).IsEqualTo(1);
  }

  [Test]
  public async Task ModelAction_Purge_HasValueTwoAsync() {
    // Arrange
    var action = ModelAction.Purge;

    // Assert - Purge should be 2
    await Assert.That((int)action).IsEqualTo(2);
  }

  [Test]
  public async Task ModelAction_Values_AreDistinctAsync() {
    // Arrange
    var values = Enum.GetValues<ModelAction>();

    // Assert - all values should be distinct
    await Assert.That(values.Distinct().Count()).IsEqualTo(values.Length);
  }

  [Test]
  public async Task ModelAction_DefaultValue_IsNoneAsync() {
    // Arrange
    ModelAction defaultAction = default;

    // Assert - default should be None
    await Assert.That(defaultAction).IsEqualTo(ModelAction.None);
  }

  [Test]
  public async Task ModelAction_HasThreeValuesAsync() {
    // Arrange
    var values = Enum.GetValues<ModelAction>();

    // Assert - should have exactly None, Delete, Purge
    await Assert.That(values.Length).IsEqualTo(3);
  }
}
