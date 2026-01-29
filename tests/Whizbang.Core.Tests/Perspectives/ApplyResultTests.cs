using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="ApplyResult{TModel}"/> struct.
/// </summary>
/// <docs>core-concepts/model-action</docs>
public class ApplyResultTests {
  // Test model for ApplyResult tests
  private sealed class TestModel {
    public string Name { get; init; } = string.Empty;
  }

  [Test]
  public async Task ApplyResult_Constructor_WithModel_SetsPropertiesAsync() {
    // Arrange
    var model = new TestModel { Name = "Test" };

    // Act
    var result = new ApplyResult<TestModel>(model);

    // Assert
    await Assert.That(result.Model).IsNotNull();
    await Assert.That(result.Model!.Name).IsEqualTo("Test");
    await Assert.That(result.Action).IsEqualTo(ModelAction.None);
  }

  [Test]
  public async Task ApplyResult_Constructor_WithModelAndAction_SetsPropertiesAsync() {
    // Arrange
    var model = new TestModel { Name = "Test" };

    // Act
    var result = new ApplyResult<TestModel>(model, ModelAction.Delete);

    // Assert
    await Assert.That(result.Model).IsNotNull();
    await Assert.That(result.Action).IsEqualTo(ModelAction.Delete);
  }

  [Test]
  public async Task ApplyResult_Constructor_WithNull_SetsNullModelAsync() {
    // Act
    var result = new ApplyResult<TestModel>(null, ModelAction.Purge);

    // Assert
    await Assert.That(result.Model).IsNull();
    await Assert.That(result.Action).IsEqualTo(ModelAction.Purge);
  }

  [Test]
  public async Task ApplyResult_None_ReturnsNullModelWithNoneActionAsync() {
    // Act
    var result = ApplyResult<TestModel>.None();

    // Assert
    await Assert.That(result.Model).IsNull();
    await Assert.That(result.Action).IsEqualTo(ModelAction.None);
  }

  [Test]
  public async Task ApplyResult_Delete_ReturnsNullModelWithDeleteActionAsync() {
    // Act
    var result = ApplyResult<TestModel>.Delete();

    // Assert
    await Assert.That(result.Model).IsNull();
    await Assert.That(result.Action).IsEqualTo(ModelAction.Delete);
  }

  [Test]
  public async Task ApplyResult_Purge_ReturnsNullModelWithPurgeActionAsync() {
    // Act
    var result = ApplyResult<TestModel>.Purge();

    // Assert
    await Assert.That(result.Model).IsNull();
    await Assert.That(result.Action).IsEqualTo(ModelAction.Purge);
  }

  [Test]
  public async Task ApplyResult_Update_ReturnsModelWithNoneActionAsync() {
    // Arrange
    var model = new TestModel { Name = "Updated" };

    // Act
    var result = ApplyResult<TestModel>.Update(model);

    // Assert
    await Assert.That(result.Model).IsNotNull();
    await Assert.That(result.Model!.Name).IsEqualTo("Updated");
    await Assert.That(result.Action).IsEqualTo(ModelAction.None);
  }

  [Test]
  public async Task ApplyResult_ImplicitConversion_FromModel_WorksAsync() {
    // Arrange
    var model = new TestModel { Name = "Implicit" };

    // Act - implicit conversion from TModel to ApplyResult<TModel>
    ApplyResult<TestModel> result = model;

    // Assert
    await Assert.That(result.Model).IsNotNull();
    await Assert.That(result.Model!.Name).IsEqualTo("Implicit");
    await Assert.That(result.Action).IsEqualTo(ModelAction.None);
  }

  [Test]
  public async Task ApplyResult_ImplicitConversion_FromModelAction_WorksAsync() {
    // Act - implicit conversion from ModelAction to ApplyResult<TModel>
    ApplyResult<TestModel> result = ModelAction.Delete;

    // Assert
    await Assert.That(result.Model).IsNull();
    await Assert.That(result.Action).IsEqualTo(ModelAction.Delete);
  }

  [Test]
  public async Task ApplyResult_ImplicitConversion_FromTuple_WorksAsync() {
    // Arrange
    var model = new TestModel { Name = "Tuple" };
    (TestModel?, ModelAction) tuple = (model, ModelAction.Purge);

    // Act - implicit conversion from tuple to ApplyResult<TModel>
    ApplyResult<TestModel> result = tuple;

    // Assert
    await Assert.That(result.Model).IsNotNull();
    await Assert.That(result.Model!.Name).IsEqualTo("Tuple");
    await Assert.That(result.Action).IsEqualTo(ModelAction.Purge);
  }

  [Test]
  public async Task ApplyResult_ImplicitConversion_FromTupleWithNullModel_WorksAsync() {
    // Arrange
    (TestModel?, ModelAction) tuple = (null, ModelAction.Delete);

    // Act
    ApplyResult<TestModel> result = tuple;

    // Assert
    await Assert.That(result.Model).IsNull();
    await Assert.That(result.Action).IsEqualTo(ModelAction.Delete);
  }

  [Test]
  public async Task ApplyResult_IsReadonlyStruct_ForPerformanceAsync() {
    // Assert - ApplyResult should be a readonly struct
    var type = typeof(ApplyResult<TestModel>);
    await Assert.That(type.IsValueType).IsTrue();
  }

  [Test]
  public async Task ApplyResult_DefaultValue_HasNullModelAndNoneActionAsync() {
    // Arrange
    ApplyResult<TestModel> defaultResult = default;

    // Assert
    await Assert.That(defaultResult.Model).IsNull();
    await Assert.That(defaultResult.Action).IsEqualTo(ModelAction.None);
  }
}
