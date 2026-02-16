using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for ITemporalPerspectiveFor interface - append-only (temporal) perspective pattern.
/// Unlike IPerspectiveFor (which uses UPSERT), temporal perspectives INSERT new rows for each event.
/// The Transform method converts events to log entries without needing current state.
/// </summary>
[Category("TemporalPerspectives")]
public class ITemporalPerspectiveForTests {
  [Test]
  public async Task TemporalPerspective_ImplementingITemporalPerspectiveFor_HasTransformMethodAsync() {
    // Arrange - Create a test temporal perspective
    var perspective = new TestActivityPerspective();
    var @event = new OrderCreatedTestEvent { OrderId = Guid.NewGuid(), Amount = 99.99m };

    // Act - Transform should be a pure function (no async, no current state needed)
    var result = perspective.Transform(@event);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.SubjectId).IsEqualTo(@event.OrderId);
    await Assert.That(result.Action).IsEqualTo("created");
  }

  [Test]
  public async Task TemporalPerspective_TransformIsPureFunctionAsync() {
    // Arrange
    var perspective = new TestActivityPerspective();
    var @event = new OrderCreatedTestEvent { OrderId = Guid.NewGuid(), Amount = 50.00m };

    // Act - Call Transform multiple times with same input
    var result1 = perspective.Transform(@event);
    var result2 = perspective.Transform(@event);

    // Assert - Pure function returns same result for same inputs
    await Assert.That(result1).IsNotNull();
    await Assert.That(result2).IsNotNull();
    await Assert.That(result1!.SubjectId).IsEqualTo(result2!.SubjectId);
    await Assert.That(result1.Action).IsEqualTo(result2.Action);
    await Assert.That(result1.Description).IsEqualTo(result2.Description);
  }

  [Test]
  public async Task TemporalPerspective_TransformCanReturnNullToSkipEventAsync() {
    // Arrange - Perspective that filters out certain events
    var perspective = new FilteringActivityPerspective();
    var lowValueEvent = new OrderCreatedTestEvent { OrderId = Guid.NewGuid(), Amount = 5.00m };
    var highValueEvent = new OrderCreatedTestEvent { OrderId = Guid.NewGuid(), Amount = 100.00m };

    // Act
    var lowValueResult = perspective.Transform(lowValueEvent);
    var highValueResult = perspective.Transform(highValueEvent);

    // Assert - Low value events are skipped (null), high value are transformed
    await Assert.That(lowValueResult).IsNull();
    await Assert.That(highValueResult).IsNotNull();
    await Assert.That(highValueResult!.SubjectId).IsEqualTo(highValueEvent.OrderId);
  }

  [Test]
  public async Task TemporalPerspective_ImplementingMultipleEventTypes_HasTransformForEachAsync() {
    // Arrange - Perspective handles two event types
    var perspective = new MultiEventActivityPerspective();
    var createEvent = new OrderCreatedTestEvent { OrderId = Guid.NewGuid(), Amount = 75.00m };
    var updateEvent = new OrderUpdatedTestEvent { OrderId = Guid.NewGuid(), NewStatus = "Shipped" };

    // Act
    var createResult = perspective.Transform(createEvent);
    var updateResult = perspective.Transform(updateEvent);

    // Assert
    await Assert.That(createResult).IsNotNull();
    await Assert.That(createResult!.Action).IsEqualTo("created");
    await Assert.That(updateResult).IsNotNull();
    await Assert.That(updateResult!.Action).IsEqualTo("updated");
  }

  [Test]
  public async Task TemporalPerspective_TransformSignature_ReturnsModelNotTaskAsync() {
    // Arrange
    var perspective = new TestActivityPerspective();
    var @event = new OrderCreatedTestEvent { OrderId = Guid.NewGuid(), Amount = 25.00m };

    // Act - Transform should return TModel? directly, NOT Task<TModel?>
    var result = perspective.Transform(@event);

    // Assert - Verify it's synchronous
    await Assert.That(result).IsTypeOf<ActivityEntryTestModel>();
  }

  [Test]
  public async Task TemporalPerspective_DoesNotRequireCurrentStateAsync() {
    // Arrange
    var perspective = new TestActivityPerspective();
    var @event = new OrderCreatedTestEvent { OrderId = Guid.NewGuid(), Amount = 150.00m };

    // Act - Transform only takes the event, not current model state
    // This is the key difference from IPerspectiveFor.Apply(currentData, eventData)
    var result = perspective.Transform(@event);

    // Assert - Result is a new entry based only on the event
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.SubjectId).IsEqualTo(@event.OrderId);
    await Assert.That(result.Description).Contains("150");
  }

  [Test]
  public async Task TemporalPerspective_MarkerInterfaceIsBaseAsync() {
    // Arrange
    var perspective = new TestActivityPerspective();

    // Assert - Single-event interface inherits from marker interface
    await Assert.That(perspective).IsAssignableTo<ITemporalPerspectiveFor<ActivityEntryTestModel>>();
    await Assert.That(perspective).IsAssignableTo<ITemporalPerspectiveFor<ActivityEntryTestModel, OrderCreatedTestEvent>>();
  }

  [Test]
  public async Task TemporalPerspective_MultiEventInterface_InheritsFromMarkerAsync() {
    // Arrange
    var perspective = new MultiEventActivityPerspective();

    // Assert - Multi-event interface also inherits from marker
    await Assert.That(perspective).IsAssignableTo<ITemporalPerspectiveFor<ActivityEntryTestModel>>();
  }
}

// Test model for temporal entries (activity log)
internal sealed record ActivityEntryTestModel {
  public Guid SubjectId { get; init; }
  public required string Action { get; init; }
  public required string Description { get; init; }
}

// Test events
internal sealed record OrderCreatedTestEvent : IEvent {
  [StreamKey]
  public Guid OrderId { get; init; }
  public decimal Amount { get; init; }
}

internal sealed record OrderUpdatedTestEvent : IEvent {
  [StreamKey]
  public Guid OrderId { get; init; }
  public required string NewStatus { get; init; }
}

// Test perspective implementing ITemporalPerspectiveFor with one event type
internal sealed class TestActivityPerspective : ITemporalPerspectiveFor<ActivityEntryTestModel, OrderCreatedTestEvent> {
  public ActivityEntryTestModel? Transform(OrderCreatedTestEvent @event) {
    return new ActivityEntryTestModel {
      SubjectId = @event.OrderId,
      Action = "created",
      Description = $"Order created for ${@event.Amount}"
    };
  }
}

// Filtering perspective that skips low-value orders
internal sealed class FilteringActivityPerspective : ITemporalPerspectiveFor<ActivityEntryTestModel, OrderCreatedTestEvent> {
  public ActivityEntryTestModel? Transform(OrderCreatedTestEvent @event) {
    // Skip low-value orders (below $10)
    if (@event.Amount < 10.00m) {
      return null;
    }

    return new ActivityEntryTestModel {
      SubjectId = @event.OrderId,
      Action = "created",
      Description = $"High-value order created for ${@event.Amount}"
    };
  }
}

// Multi-event perspective implementing ITemporalPerspectiveFor with two event types
internal sealed class MultiEventActivityPerspective :
    ITemporalPerspectiveFor<ActivityEntryTestModel, OrderCreatedTestEvent, OrderUpdatedTestEvent> {

  public ActivityEntryTestModel? Transform(OrderCreatedTestEvent @event) {
    return new ActivityEntryTestModel {
      SubjectId = @event.OrderId,
      Action = "created",
      Description = $"Order created for ${@event.Amount}"
    };
  }

  public ActivityEntryTestModel? Transform(OrderUpdatedTestEvent @event) {
    return new ActivityEntryTestModel {
      SubjectId = @event.OrderId,
      Action = "updated",
      Description = $"Order status changed to {@event.NewStatus}"
    };
  }
}
