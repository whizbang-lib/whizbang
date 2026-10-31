// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

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
  public async Task ModelAction_Resurrect_HasValueThreeAsync() {
    // Arrange
    var action = ModelAction.Resurrect;

    // Assert - Resurrect is appended after Purge so stored and compiled values never shift
    await Assert.That((int)action).IsEqualTo(3);
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
  public async Task ModelAction_HasFourValuesAsync() {
    // Arrange
    var values = Enum.GetValues<ModelAction>();

    // Assert - should have exactly None, Delete, Purge, Resurrect
    await Assert.That(values.Length).IsEqualTo(4);
  }

  // The documented example (ModelAction's <example>): an action is returned through the interface-declared
  // ApplyResult<TModel>, converted implicitly, and Delete sets nothing on the model (#1936).

  internal sealed record OrderView {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset? DeletedAt { get; init; }
  }

  internal sealed record OrderPlaced : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  internal sealed record OrderCanceled : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  internal sealed class OrderPerspective :
      IPerspectiveFor<OrderView, OrderPlaced>,
      IPerspectiveWithActionsFor<OrderView, OrderCanceled> {
    public OrderView Apply(OrderView current, OrderPlaced @event) => current with { Status = "Placed" };

    public ApplyResult<OrderView> Apply(OrderView current, OrderCanceled @event) {
      return ModelAction.Delete;
    }
  }

  [Test]
  public async Task TheDocumentedExample_ReturnsDeleteThroughTheInterfaceDeclaredShape_AndSetsNothingAsync() {
    var perspective = new OrderPerspective();
    var current = new OrderView { OrderId = TrackedGuid.New().Value, Status = "Placed" };

    var result = perspective.Apply(current, new OrderCanceled { OrderId = current.OrderId });

    await Assert.That(result.Action).IsEqualTo(ModelAction.Delete);
    await Assert.That(result.Model).IsNull()
      .Because("Delete returns no model, so the runner keeps the current one; nothing stamps DeletedAt on it");
    await Assert.That(current.DeletedAt).IsNull();
  }
}
