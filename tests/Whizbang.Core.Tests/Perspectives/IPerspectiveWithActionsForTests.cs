using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for IPerspectiveWithActionsFor interface - strongly-typed perspective deletion.
/// This interface returns ApplyResult&lt;TModel&gt; to support Delete/Purge operations.
/// </summary>
/// <tests>src/Whizbang.Core/Perspectives/IPerspectiveWithActionsFor.cs</tests>
public class IPerspectiveWithActionsForTests {
  // ==========================================================================
  // Test types
  // ==========================================================================

  internal sealed record TestModel {
    [StreamId]
    public Guid StreamId { get; init; } = Guid.NewGuid();
    public int Value { get; init; }
  }

  internal sealed record TestCreatedEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; } = Guid.NewGuid();
    public int InitialValue { get; init; }
  }

  internal sealed record TestUpdatedEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; } = Guid.NewGuid();
    public int Delta { get; init; }
  }

  internal sealed record TestDeletedEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; } = Guid.NewGuid();
  }

  internal sealed record TestPurgedEvent : IEvent {
    [StreamId]
    public Guid StreamId { get; init; } = Guid.NewGuid();
  }

  // ==========================================================================
  // Test perspective implementing IPerspectiveWithActionsFor
  // ==========================================================================

  /// <summary>
  /// Perspective demonstrating all ApplyResult patterns: update, delete, purge.
  /// </summary>
  internal sealed class ActionsPerspective :
      IPerspectiveWithActionsFor<TestModel, TestCreatedEvent>,
      IPerspectiveWithActionsFor<TestModel, TestUpdatedEvent>,
      IPerspectiveWithActionsFor<TestModel, TestDeletedEvent>,
      IPerspectiveWithActionsFor<TestModel, TestPurgedEvent> {

    public ApplyResult<TestModel> Apply(TestModel currentData, TestCreatedEvent @event) {
      // Implicit conversion from TModel to ApplyResult<TModel>
      return currentData with { Value = @event.InitialValue };
    }

    public ApplyResult<TestModel> Apply(TestModel currentData, TestUpdatedEvent @event) {
      // Explicit factory method
      return ApplyResult<TestModel>.Update(currentData with { Value = currentData.Value + @event.Delta });
    }

    public ApplyResult<TestModel> Apply(TestModel currentData, TestDeletedEvent @event) {
      // Soft delete
      return ApplyResult<TestModel>.Delete();
    }

    public ApplyResult<TestModel> Apply(TestModel currentData, TestPurgedEvent @event) {
      // Hard delete
      return ApplyResult<TestModel>.Purge();
    }
  }

  // ==========================================================================
  // Test perspective mixing both interface types
  // ==========================================================================

  /// <summary>
  /// Perspective demonstrating mixing IPerspectiveFor and IPerspectiveWithActionsFor.
  /// This is the primary use case - use IPerspectiveFor for normal updates,
  /// IPerspectiveWithActionsFor for deletions.
  /// </summary>
  internal sealed class MixedPerspective :
      IPerspectiveFor<TestModel, TestCreatedEvent>,           // Returns TModel
      IPerspectiveFor<TestModel, TestUpdatedEvent>,           // Returns TModel
      IPerspectiveWithActionsFor<TestModel, TestDeletedEvent>, // Returns ApplyResult<TModel>
      IPerspectiveWithActionsFor<TestModel, TestPurgedEvent> { // Returns ApplyResult<TModel>

    // IPerspectiveFor methods - return TModel directly
    public TestModel Apply(TestModel currentData, TestCreatedEvent @event) {
      return currentData with { Value = @event.InitialValue };
    }

    public TestModel Apply(TestModel currentData, TestUpdatedEvent @event) {
      return currentData with { Value = currentData.Value + @event.Delta };
    }

    // IPerspectiveWithActionsFor methods - return ApplyResult<TModel>
    public ApplyResult<TestModel> Apply(TestModel currentData, TestDeletedEvent @event) {
      return ApplyResult<TestModel>.Delete();
    }

    public ApplyResult<TestModel> Apply(TestModel currentData, TestPurgedEvent @event) {
      return ApplyResult<TestModel>.Purge();
    }
  }

  // ==========================================================================
  // ApplyResult return type tests
  // ==========================================================================

  [Test]
  public async Task Apply_ReturningModel_HasImplicitConversionToApplyResultAsync() {
    // Arrange
    var perspective = new ActionsPerspective();
    var model = new TestModel { Value = 0 };
    var @event = new TestCreatedEvent { InitialValue = 42 };

    // Act
    ApplyResult<TestModel> result = perspective.Apply(model, @event);

    // Assert
    await Assert.That(result.Model).IsNotNull();
    await Assert.That(result.Model!.Value).IsEqualTo(42);
    await Assert.That(result.Action).IsEqualTo(ModelAction.None);
  }

  [Test]
  public async Task Apply_ReturningUpdate_HasCorrectModelAndActionAsync() {
    // Arrange
    var perspective = new ActionsPerspective();
    var model = new TestModel { Value = 10 };
    var @event = new TestUpdatedEvent { Delta = 5 };

    // Act
    ApplyResult<TestModel> result = perspective.Apply(model, @event);

    // Assert
    await Assert.That(result.Model).IsNotNull();
    await Assert.That(result.Model!.Value).IsEqualTo(15);
    await Assert.That(result.Action).IsEqualTo(ModelAction.None);
  }

  [Test]
  public async Task Apply_ReturningDelete_HasCorrectActionAsync() {
    // Arrange
    var perspective = new ActionsPerspective();
    var model = new TestModel { Value = 10 };
    var @event = new TestDeletedEvent();

    // Act
    ApplyResult<TestModel> result = perspective.Apply(model, @event);

    // Assert - Soft delete returns null model with Delete action
    await Assert.That(result.Model).IsNull();
    await Assert.That(result.Action).IsEqualTo(ModelAction.Delete);
  }

  [Test]
  public async Task Apply_ReturningPurge_HasCorrectActionAsync() {
    // Arrange
    var perspective = new ActionsPerspective();
    var model = new TestModel { Value = 10 };
    var @event = new TestPurgedEvent();

    // Act
    ApplyResult<TestModel> result = perspective.Apply(model, @event);

    // Assert - Hard delete returns null model with Purge action
    await Assert.That(result.Model).IsNull();
    await Assert.That(result.Action).IsEqualTo(ModelAction.Purge);
  }

  // ==========================================================================
  // Interface type safety tests
  // ==========================================================================

  [Test]
  public async Task Interface_EnforcesApplyResultReturnTypeAsync() {
    // Arrange - Use interface reference to verify compile-time contract
    // We intentionally use the interface type here to verify the contract
#pragma warning disable CA1859 // Use concrete types when possible for improved performance
    IPerspectiveWithActionsFor<TestModel, TestCreatedEvent> perspective = new ActionsPerspective();
#pragma warning restore CA1859
    var model = new TestModel { Value = 0 };
    var @event = new TestCreatedEvent { InitialValue = 100 };

    // Act - Interface signature returns ApplyResult<TModel>
    ApplyResult<TestModel> result = perspective.Apply(model, @event);

    // Assert
    await Assert.That(result.Model).IsNotNull();
    await Assert.That(result.Model!.Value).IsEqualTo(100);
  }

  // ==========================================================================
  // Mixed interface tests
  // ==========================================================================

  [Test]
  public async Task MixedPerspective_IPerspectiveFor_ReturnsTModelAsync() {
    // Arrange
    var perspective = new MixedPerspective();
    var model = new TestModel { Value = 0 };
    var @event = new TestCreatedEvent { InitialValue = 50 };

    // Act - IPerspectiveFor methods return TModel directly
    TestModel result = perspective.Apply(model, @event);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Value).IsEqualTo(50);
  }

  [Test]
  public async Task MixedPerspective_IPerspectiveWithActionsFor_ReturnsApplyResultAsync() {
    // Arrange
    var perspective = new MixedPerspective();
    var model = new TestModel { Value = 10 };
    var @event = new TestDeletedEvent();

    // Act - IPerspectiveWithActionsFor methods return ApplyResult<TModel>
    ApplyResult<TestModel> result = perspective.Apply(model, @event);

    // Assert
    await Assert.That(result.Model).IsNull();
    await Assert.That(result.Action).IsEqualTo(ModelAction.Delete);
  }

  [Test]
  public async Task MixedPerspective_BothInterfaceTypes_WorkTogetherAsync() {
    // Arrange
    var perspective = new MixedPerspective();
    var model = new TestModel { Value = 0 };

    // Act - Chain operations from both interface types
    var afterCreate = perspective.Apply(model, new TestCreatedEvent { InitialValue = 10 });
    var afterUpdate = perspective.Apply(afterCreate, new TestUpdatedEvent { Delta = 5 });
    var afterDelete = perspective.Apply(afterUpdate, new TestDeletedEvent());

    // Assert
    await Assert.That(afterCreate.Value).IsEqualTo(10);
    await Assert.That(afterUpdate.Value).IsEqualTo(15);
    await Assert.That(afterDelete.Model).IsNull();
    await Assert.That(afterDelete.Action).IsEqualTo(ModelAction.Delete);
  }

  // ==========================================================================
  // Implicit conversion tests
  // ==========================================================================

  [Test]
  public async Task Apply_ModelActionImplicitConversion_WorksAsync() {
    // This tests that ModelAction can be returned directly from Apply
    // due to implicit conversion to ApplyResult<TModel>
    var result = _getDeleteViaImplicitConversion();

    await Assert.That(result.Model).IsNull();
    await Assert.That(result.Action).IsEqualTo(ModelAction.Delete);
  }

  private static ApplyResult<TestModel> _getDeleteViaImplicitConversion() {
    // Implicit conversion from ModelAction to ApplyResult<TModel>
    return ModelAction.Delete;
  }

  [Test]
  public async Task Apply_TupleImplicitConversion_WorksAsync() {
    // This tests that (TModel?, ModelAction) can be returned directly
    // due to implicit conversion to ApplyResult<TModel>
    var model = new TestModel { Value = 42 };
    var result = _getUpdateViaTupleConversion(model);

    await Assert.That(result.Model).IsNotNull();
    await Assert.That(result.Model!.Value).IsEqualTo(42);
    await Assert.That(result.Action).IsEqualTo(ModelAction.None);
  }

  private static ApplyResult<TestModel> _getUpdateViaTupleConversion(TestModel model) {
    // Implicit conversion from tuple to ApplyResult<TModel>
    return (model, ModelAction.None);
  }

  // ==========================================================================
  // Pure function tests
  // ==========================================================================

  [Test]
  public async Task Apply_IsPureFunction_OriginalModelUnchangedAsync() {
    // Arrange
    var perspective = new ActionsPerspective();
    var original = new TestModel { Value = 10 };
    var @event = new TestUpdatedEvent { Delta = 5 };

    // Act
    var result = perspective.Apply(original, @event);

    // Assert - Original model unchanged (pure function)
    await Assert.That(original.Value).IsEqualTo(10);
    await Assert.That(result.Model!.Value).IsEqualTo(15);
  }

  [Test]
  public async Task Apply_IsPureFunction_SameInputsSameOutputAsync() {
    // Arrange
    var perspective = new ActionsPerspective();
    var model = new TestModel { Value = 10 };
    var @event = new TestUpdatedEvent { Delta = 5 };

    // Act
    var result1 = perspective.Apply(model, @event);
    var result2 = perspective.Apply(model, @event);

    // Assert - Same inputs produce same outputs (deterministic)
    await Assert.That(result1.Model!.Value).IsEqualTo(result2.Model!.Value);
  }
}
