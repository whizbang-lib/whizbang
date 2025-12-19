using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for IPerspectiveFor interface - pure function perspective pattern.
/// This tests the new interface that replaces IPerspectiveOf.
/// </summary>
public class IPerspectiveForTests {
  [Test]
  public async Task Perspective_ImplementingIPerspectiveFor_HasApplyMethodAsync() {
    // Arrange - Create a test perspective
    var perspective = new TestPerspective();
    var model = new TestModel { Value = 0 };
    var @event = new TestEvent { Delta = 5 };

    // Act - Apply should be a pure function (no async)
    var result = perspective.Apply(model, @event);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Value).IsEqualTo(5);
    await Assert.That(model.Value).IsEqualTo(0); // Original model unchanged (pure function!)
  }

  [Test]
  public async Task Perspective_ImplementingIPerspectiveFor_ApplyIsPureFunctionAsync() {
    // Arrange
    var perspective = new TestPerspective();
    var model = new TestModel { Value = 10 };
    var @event = new TestEvent { Delta = 3 };

    // Act - Call Apply multiple times with same inputs
    var result1 = perspective.Apply(model, @event);
    var result2 = perspective.Apply(model, @event);

    // Assert - Pure function returns same result for same inputs
    await Assert.That(result1.Value).IsEqualTo(result2.Value);
    await Assert.That(result1.Value).IsEqualTo(13);
  }

  [Test]
  public async Task Perspective_ImplementingMultipleEventTypes_HasApplyForEachAsync() {
    // Arrange - Perspective handles two event types
    var perspective = new MultiEventPerspective();
    var model = new TestModel { Value = 0 };
    var event1 = new TestEvent { Delta = 5 };
    var event2 = new AnotherTestEvent { Multiplier = 2 };

    // Act
    var afterEvent1 = perspective.Apply(model, event1);
    var afterEvent2 = perspective.Apply(afterEvent1, event2);

    // Assert
    await Assert.That(afterEvent1.Value).IsEqualTo(5);
    await Assert.That(afterEvent2.Value).IsEqualTo(10);
  }

  [Test]
  public async Task Perspective_ApplySignature_ReturnsModelNotTaskAsync() {
    // Arrange
    var perspective = new TestPerspective();
    var model = new TestModel { Value = 0 };
    var @event = new TestEvent { Delta = 1 };

    // Act - Apply should return TModel directly, NOT Task<TModel>
    var result = perspective.Apply(model, @event);

    // Assert - Verify it's synchronous
    await Assert.That(result).IsTypeOf<TestModel>();
  }
}

// Test implementations
internal record TestModel {
  public int Value { get; init; }
}

internal record TestEvent : IEvent {
  public int Delta { get; init; }
}

internal record AnotherTestEvent : IEvent {
  public int Multiplier { get; init; }
}

// Test perspective implementing IPerspectiveFor with one event type
internal class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
  public TestModel Apply(TestModel currentData, TestEvent @event) {
    return currentData with { Value = currentData.Value + @event.Delta };
  }
}

// Test perspective implementing IPerspectiveFor with two event types
internal class MultiEventPerspective :
    IPerspectiveFor<TestModel, TestEvent>,
    IPerspectiveFor<TestModel, AnotherTestEvent> {

  public TestModel Apply(TestModel currentData, TestEvent @event) {
    return currentData with { Value = currentData.Value + @event.Delta };
  }

  public TestModel Apply(TestModel currentData, AnotherTestEvent @event) {
    return currentData with { Value = currentData.Value * @event.Multiplier };
  }
}
