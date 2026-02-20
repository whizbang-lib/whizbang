using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatch;

/// <summary>
/// Tests for Route static factory class which provides convenient methods to create Routed&lt;T&gt; instances.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/Route.cs</code-under-test>
public class RouteTests {
  #region Route.Local

  [Test]
  public async Task Local_WithValue_ReturnsRoutedWithLocalModeAsync() {
    // Arrange
    var value = new TestEvent("Test");

    // Act
    var routed = Route.Local(value);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(value);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task Local_WithArray_ReturnsRoutedArrayWithLocalModeAsync() {
    // Arrange
    var array = new[] { new TestEvent("A"), new TestEvent("B") };

    // Act
    var routed = Route.Local(array);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(array);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task Local_WithTuple_ReturnsRoutedTupleWithLocalModeAsync() {
    // Arrange
    var tuple = (new TestEvent("A"), new TestEvent("B"));

    // Act
    var routed = Route.Local(tuple);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(tuple);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task Local_WithNull_ReturnsRoutedNullWithLocalModeAsync() {
    // Act
    var routed = Route.Local<TestEvent?>(null);

    // Assert
    await Assert.That(routed.Value).IsNull();
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Local);
  }

  #endregion

  #region Route.Outbox

  [Test]
  public async Task Outbox_WithValue_ReturnsRoutedWithOutboxModeAsync() {
    // Arrange
    var value = new TestEvent("Test");

    // Act
    var routed = Route.Outbox(value);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(value);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Outbox);
  }

  [Test]
  public async Task Outbox_WithArray_ReturnsRoutedArrayWithOutboxModeAsync() {
    // Arrange
    var array = new[] { new TestEvent("A"), new TestEvent("B") };

    // Act
    var routed = Route.Outbox(array);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(array);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Outbox);
  }

  [Test]
  public async Task Outbox_WithNull_ReturnsRoutedNullWithOutboxModeAsync() {
    // Act
    var routed = Route.Outbox<TestEvent?>(null);

    // Assert
    await Assert.That(routed.Value).IsNull();
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Outbox);
  }

  #endregion

  #region Route.Both

  [Test]
  public async Task Both_WithValue_ReturnsRoutedWithBothModeAsync() {
    // Arrange
    var value = new TestEvent("Test");

    // Act
    var routed = Route.Both(value);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(value);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Both);
  }

  [Test]
  public async Task Both_WithArray_ReturnsRoutedArrayWithBothModeAsync() {
    // Arrange
    var array = new[] { new TestEvent("A"), new TestEvent("B") };

    // Act
    var routed = Route.Both(array);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(array);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Both);
  }

  [Test]
  public async Task Both_HasFlag_LocalDispatchAsync() {
    // Arrange
    var routed = Route.Both(new TestEvent("Test"));

    // Assert - Both includes LocalDispatch for local receptor invocation
    await Assert.That(routed.Mode.HasFlag(DispatchMode.LocalDispatch)).IsTrue();
  }

  [Test]
  public async Task Both_HasFlag_OutboxAsync() {
    // Arrange
    var routed = Route.Both(new TestEvent("Test"));

    // Assert
    await Assert.That(routed.Mode.HasFlag(DispatchMode.Outbox)).IsTrue();
  }

  [Test]
  public async Task Both_DoesNotHaveFlag_EventStoreAsync() {
    // Arrange
    var routed = Route.Both(new TestEvent("Test"));

    // Assert - Both uses outbox for event storage, not direct EventStore flag
    await Assert.That(routed.Mode.HasFlag(DispatchMode.EventStore)).IsFalse();
  }

  #endregion

  #region Route.LocalNoPersist

  [Test]
  public async Task LocalNoPersist_WithValue_ReturnsRoutedWithLocalNoPersistModeAsync() {
    // Arrange
    var value = new TestEvent("Test");

    // Act
    var routed = Route.LocalNoPersist(value);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(value);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.LocalNoPersist);
  }

  [Test]
  public async Task LocalNoPersist_WithArray_ReturnsRoutedArrayWithLocalNoPersistModeAsync() {
    // Arrange
    var array = new[] { new TestEvent("A"), new TestEvent("B") };

    // Act
    var routed = Route.LocalNoPersist(array);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(array);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.LocalNoPersist);
  }

  [Test]
  public async Task LocalNoPersist_WithNull_ReturnsRoutedNullWithLocalNoPersistModeAsync() {
    // Act
    TestEvent? nullValue = null;
    var routed = Route.LocalNoPersist(nullValue);

    // Assert
    await Assert.That(routed.Value).IsNull();
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.LocalNoPersist);
  }

  [Test]
  public async Task LocalNoPersist_HasFlag_LocalDispatchAsync() {
    // Arrange
    var routed = Route.LocalNoPersist(new TestEvent("Test"));

    // Assert - LocalNoPersist invokes local receptors
    await Assert.That(routed.Mode.HasFlag(DispatchMode.LocalDispatch)).IsTrue();
  }

  [Test]
  public async Task LocalNoPersist_DoesNotHaveFlag_EventStoreAsync() {
    // Arrange
    var routed = Route.LocalNoPersist(new TestEvent("Test"));

    // Assert - LocalNoPersist does NOT persist to event store
    await Assert.That(routed.Mode.HasFlag(DispatchMode.EventStore)).IsFalse();
  }

  [Test]
  public async Task LocalNoPersist_DoesNotHaveFlag_OutboxAsync() {
    // Arrange
    var routed = Route.LocalNoPersist(new TestEvent("Test"));

    // Assert - LocalNoPersist does NOT use outbox
    await Assert.That(routed.Mode.HasFlag(DispatchMode.Outbox)).IsFalse();
  }

  [Test]
  public async Task LocalNoPersist_WithCollection_ReturnsEnumerableOfRoutedAsync() {
    // Arrange
    IEnumerable<TestEvent> events = new List<TestEvent> { new("A"), new("B"), new("C") };

    // Act
    var routedCollection = Route.LocalNoPersist(events).ToList();

    // Assert
    await Assert.That(routedCollection).Count().IsEqualTo(3);
    await Assert.That(routedCollection[0].Value.Name).IsEqualTo("A");
    await Assert.That(routedCollection[0].Mode).IsEqualTo(DispatchMode.LocalNoPersist);
    await Assert.That(routedCollection[1].Value.Name).IsEqualTo("B");
    await Assert.That(routedCollection[2].Value.Name).IsEqualTo("C");
  }

  #endregion

  #region Route.EventStoreOnly

  [Test]
  public async Task EventStoreOnly_WithValue_ReturnsRoutedWithEventStoreOnlyModeAsync() {
    // Arrange
    var value = new TestEvent("Test");

    // Act
    var routed = Route.EventStoreOnly(value);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(value);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.EventStoreOnly);
  }

  [Test]
  public async Task EventStoreOnly_WithArray_ReturnsRoutedArrayWithEventStoreOnlyModeAsync() {
    // Arrange
    var array = new[] { new TestEvent("A"), new TestEvent("B") };

    // Act
    var routed = Route.EventStoreOnly(array);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(array);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.EventStoreOnly);
  }

  [Test]
  public async Task EventStoreOnly_WithNull_ReturnsRoutedNullWithEventStoreOnlyModeAsync() {
    // Act
    TestEvent? nullValue = null;
    var routed = Route.EventStoreOnly(nullValue);

    // Assert
    await Assert.That(routed.Value).IsNull();
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.EventStoreOnly);
  }

  [Test]
  public async Task EventStoreOnly_HasFlag_EventStoreAsync() {
    // Arrange
    var routed = Route.EventStoreOnly(new TestEvent("Test"));

    // Assert - EventStoreOnly persists to event store
    await Assert.That(routed.Mode.HasFlag(DispatchMode.EventStore)).IsTrue();
  }

  [Test]
  public async Task EventStoreOnly_DoesNotHaveFlag_LocalDispatchAsync() {
    // Arrange
    var routed = Route.EventStoreOnly(new TestEvent("Test"));

    // Assert - EventStoreOnly does NOT invoke local receptors
    await Assert.That(routed.Mode.HasFlag(DispatchMode.LocalDispatch)).IsFalse();
  }

  [Test]
  public async Task EventStoreOnly_DoesNotHaveFlag_OutboxAsync() {
    // Arrange
    var routed = Route.EventStoreOnly(new TestEvent("Test"));

    // Assert - EventStoreOnly does NOT use outbox transport
    await Assert.That(routed.Mode.HasFlag(DispatchMode.Outbox)).IsFalse();
  }

  [Test]
  public async Task EventStoreOnly_WithCollection_ReturnsEnumerableOfRoutedAsync() {
    // Arrange
    IEnumerable<TestEvent> events = new List<TestEvent> { new("A"), new("B"), new("C") };

    // Act
    var routedCollection = Route.EventStoreOnly(events).ToList();

    // Assert
    await Assert.That(routedCollection).Count().IsEqualTo(3);
    await Assert.That(routedCollection[0].Value.Name).IsEqualTo("A");
    await Assert.That(routedCollection[0].Mode).IsEqualTo(DispatchMode.EventStoreOnly);
    await Assert.That(routedCollection[1].Value.Name).IsEqualTo("B");
    await Assert.That(routedCollection[2].Value.Name).IsEqualTo("C");
  }

  #endregion

  #region Route.Local HasFlag Tests (updated for new behavior)

  [Test]
  public async Task Local_HasFlag_LocalDispatchAsync() {
    // Arrange
    var routed = Route.Local(new TestEvent("Test"));

    // Assert - Local invokes local receptors
    await Assert.That(routed.Mode.HasFlag(DispatchMode.LocalDispatch)).IsTrue();
  }

  [Test]
  public async Task Local_HasFlag_EventStoreAsync() {
    // Arrange
    var routed = Route.Local(new TestEvent("Test"));

    // Assert - Local now persists to event store
    await Assert.That(routed.Mode.HasFlag(DispatchMode.EventStore)).IsTrue();
  }

  [Test]
  public async Task Local_DoesNotHaveFlag_OutboxAsync() {
    // Arrange
    var routed = Route.Local(new TestEvent("Test"));

    // Assert - Local does NOT use outbox transport
    await Assert.That(routed.Mode.HasFlag(DispatchMode.Outbox)).IsFalse();
  }

  #endregion

  #region Type Inference

  [Test]
  public async Task Local_InfersType_FromValueAsync() {
    // Arrange
    var value = new TestEvent("Test");

    // Act - type should be inferred from value
    Routed<TestEvent> routed = Route.Local(value);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(value);
  }

  [Test]
  public async Task Outbox_InfersType_FromValueAsync() {
    // Arrange
    var value = new TestEvent("Test");

    // Act - type should be inferred from value
    Routed<TestEvent> routed = Route.Outbox(value);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(value);
  }

  [Test]
  public async Task Both_InfersType_FromValueAsync() {
    // Arrange
    var value = new TestEvent("Test");

    // Act - type should be inferred from value
    Routed<TestEvent> routed = Route.Both(value);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(value);
  }

  #endregion

  #region IRouted Interface from Factory Methods

  [Test]
  public async Task Local_Result_ImplementsIRoutedAsync() {
    // Arrange
    var routed = Route.Local(new TestEvent("Test"));

    // Act
    IRouted iRouted = routed;

    // Assert
    await Assert.That(iRouted.Mode).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task Outbox_Result_ImplementsIRoutedAsync() {
    // Arrange
    var routed = Route.Outbox(new TestEvent("Test"));

    // Act
    IRouted iRouted = routed;

    // Assert
    await Assert.That(iRouted.Mode).IsEqualTo(DispatchMode.Outbox);
  }

  [Test]
  public async Task Both_Result_ImplementsIRoutedAsync() {
    // Arrange
    var routed = Route.Both(new TestEvent("Test"));

    // Act
    IRouted iRouted = routed;

    // Assert
    await Assert.That(iRouted.Mode).IsEqualTo(DispatchMode.Both);
  }

  #endregion

  #region Route.None

  [Test]
  public async Task None_ReturnsRoutedNoneAsync() {
    // Act
    var result = Route.None();

    // Assert - RoutedNone is a struct, check type
    await Assert.That(result).IsTypeOf<RoutedNone>();
  }

  [Test]
  public async Task None_ImplementsIRoutedAsync() {
    // Act
    var result = Route.None();

    // Assert - Access via interface
    await Assert.That(result.Mode).IsEqualTo(DispatchMode.None);
    await Assert.That(result.Value).IsNull();
  }

  [Test]
  public async Task None_InTuple_CanBeMixedWithEventsAsync() {
    // Arrange - Discriminated union tuple: success or failure
    var successEvent = new TestEvent("Success");
    var tuple = (success: successEvent, failure: Route.None());

    // Assert - Both elements exist in tuple
    await Assert.That(tuple.success).IsEqualTo(successEvent);
    await Assert.That(tuple.failure).IsTypeOf<RoutedNone>();
  }

  [Test]
  public async Task None_InTuple_AlternativePathAsync() {
    // Arrange - Discriminated union: failure path
    var failureEvent = new TestEvent("Failure");
    var tuple = (success: Route.None(), failure: failureEvent);

    // Assert
    await Assert.That(tuple.success).IsTypeOf<RoutedNone>();
    await Assert.That(tuple.failure).IsEqualTo(failureEvent);
  }

  #endregion

  #region Test Types

  private sealed record TestEvent(string Name) : IEvent;

  #endregion
}
