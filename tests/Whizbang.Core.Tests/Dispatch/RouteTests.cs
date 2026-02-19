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
  public async Task Both_HasFlag_LocalAsync() {
    // Arrange
    var routed = Route.Both(new TestEvent("Test"));

    // Assert
    await Assert.That(routed.Mode.HasFlag(DispatchMode.Local)).IsTrue();
  }

  [Test]
  public async Task Both_HasFlag_OutboxAsync() {
    // Arrange
    var routed = Route.Both(new TestEvent("Test"));

    // Assert
    await Assert.That(routed.Mode.HasFlag(DispatchMode.Outbox)).IsTrue();
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

  #region Test Types

  private sealed record TestEvent(string Name) : IEvent;

  #endregion
}
