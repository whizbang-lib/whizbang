using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatch;

/// <summary>
/// Tests for Routed&lt;T&gt; struct and IRouted interface which wrap values with dispatch routing information.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/Routed.cs</code-under-test>
public class RoutedTests {
  #region Constructor and Properties

  [Test]
  public async Task Constructor_WithValueAndMode_SetsPropertiesAsync() {
    // Arrange
    var value = new TestEvent("Test");
    var mode = DispatchModes.Local;

    // Act
    var routed = new Routed<TestEvent>(value, mode);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(value);
    await Assert.That(routed.Mode).IsEqualTo(mode);
  }

  [Test]
  public async Task Constructor_WithNullValue_AllowsNullAsync() {
    // Arrange & Act
    var routed = new Routed<TestEvent?>(null, DispatchModes.Outbox);

    // Assert
    await Assert.That(routed.Value).IsNull();
    await Assert.That(routed.Mode).IsEqualTo(DispatchModes.Outbox);
  }

  [Test]
  public async Task Constructor_WithDifferentModes_SetsCorrectModeAsync() {
    // Test all modes
    var value = new TestEvent("Test");

    var routedNone = new Routed<TestEvent>(value, DispatchModes.None);
    var routedLocal = new Routed<TestEvent>(value, DispatchModes.Local);
    var routedOutbox = new Routed<TestEvent>(value, DispatchModes.Outbox);
    var routedBoth = new Routed<TestEvent>(value, DispatchModes.Both);

    await Assert.That(routedNone.Mode).IsEqualTo(DispatchModes.None);
    await Assert.That(routedLocal.Mode).IsEqualTo(DispatchModes.Local);
    await Assert.That(routedOutbox.Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(routedBoth.Mode).IsEqualTo(DispatchModes.Both);
  }

  #endregion

  #region IRouted Interface

  [Test]
  public async Task IRouted_Value_ReturnsValueAsObjectAsync() {
    // Arrange
    var value = new TestEvent("Test");
    var routed = new Routed<TestEvent>(value, DispatchModes.Local);

    // Act
    IRouted iRouted = routed;
    var objectValue = iRouted.Value;

    // Assert
    await Assert.That(objectValue).IsEqualTo(value);
  }

  [Test]
  public async Task IRouted_Mode_ReturnsSameModeAsync() {
    // Arrange
    var routed = new Routed<TestEvent>(new TestEvent("Test"), DispatchModes.Both);

    // Act
    IRouted iRouted = routed;

    // Assert
    await Assert.That(iRouted.Mode).IsEqualTo(DispatchModes.Both);
  }

  [Test]
  public async Task IRouted_CanPatternMatch_OnRoutedTypeAsync() {
    // Arrange
    object obj = new Routed<TestEvent>(new TestEvent("Test"), DispatchModes.Local);

    // Act
    var isRouted = obj is IRouted;

    // Assert
    await Assert.That(isRouted).IsTrue();
  }

  [Test]
  public async Task IRouted_PatternMatch_ExtractsValueAndModeAsync() {
    // Arrange
    var originalValue = new TestEvent("Test");
    object obj = new Routed<TestEvent>(originalValue, DispatchModes.Outbox);

    // Act
    var routed = obj as IRouted;

    // Assert
    await Assert.That(routed).IsNotNull();
    await Assert.That(routed!.Value).IsEqualTo(originalValue);
    await Assert.That(routed.Mode).IsEqualTo(DispatchModes.Outbox);
  }

  #endregion

  #region Value Types

  [Test]
  public async Task Routed_WithValueType_WorksCorrectlyAsync() {
    // Arrange
    var routed = new Routed<int>(42, DispatchModes.Local);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(42);
    await Assert.That(routed.Mode).IsEqualTo(DispatchModes.Local);
  }

  [Test]
  public async Task Routed_WithArray_WorksCorrectlyAsync() {
    // Arrange
    var array = new[] { new TestEvent("A"), new TestEvent("B") };
    var routed = new Routed<TestEvent[]>(array, DispatchModes.Both);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(array);
    await Assert.That(routed.Mode).IsEqualTo(DispatchModes.Both);
  }

  [Test]
  public async Task Routed_WithTuple_WorksCorrectlyAsync() {
    // Arrange
    var tuple = (new TestEvent("A"), new TestEvent("B"));
    var routed = new Routed<(TestEvent, TestEvent)>(tuple, DispatchModes.Outbox);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(tuple);
    await Assert.That(routed.Mode).IsEqualTo(DispatchModes.Outbox);
  }

  #endregion

  #region Struct Behavior

  [Test]
  public async Task Routed_IsValueType_NoHeapAllocationAsync() {
    // Arrange
    var routed = new Routed<TestEvent>(new TestEvent("Test"), DispatchModes.Local);

    // Assert - Routed<T> should be a value type (struct)
    await Assert.That(routed.GetType().IsValueType).IsTrue();
  }

  [Test]
  public async Task Routed_DefaultValue_HasNoneMode_AndDefaultValueAsync() {
    // Arrange
    var defaultRouted = default(Routed<TestEvent>);

    // Assert
    await Assert.That(defaultRouted.Value).IsNull();
    await Assert.That(defaultRouted.Mode).IsEqualTo(DispatchModes.None);
  }

  #endregion

  #region AsValueTask

  [Test]
  public async Task AsValueTask_ReturnsCompletedValueTask_WithSameRoutedValueAsync() {
    // Arrange
    var value = new TestEvent("Test");
    var routed = new Routed<TestEvent>(value, DispatchModes.Local);

    // Act
    var valueTask = routed.AsValueTask();

    // Assert
    await Assert.That(valueTask.IsCompleted).IsTrue();
    var result = await valueTask;
    await Assert.That(result.Value).IsEqualTo(value);
    await Assert.That(result.Mode).IsEqualTo(DispatchModes.Local);
  }

  [Test]
  public async Task AsValueTask_WithRouteLocal_EnablesFluentChainingAsync() {
    // Arrange & Act - Simulates receptor return pattern
    var valueTask = Route.Local(new TestEvent("Fluent")).AsValueTask();

    // Assert
    await Assert.That(valueTask.IsCompleted).IsTrue();
    var result = await valueTask;
    await Assert.That(result.Value.Name).IsEqualTo("Fluent");
    await Assert.That(result.Mode).IsEqualTo(DispatchModes.Local);
  }

  [Test]
  public async Task AsValueTask_WithRouteOutbox_EnablesFluentChainingAsync() {
    // Arrange & Act
    var valueTask = Route.Outbox(new TestEvent("Outbox")).AsValueTask();

    // Assert
    var result = await valueTask;
    await Assert.That(result.Mode).IsEqualTo(DispatchModes.Outbox);
  }

  [Test]
  public async Task AsValueTask_WithRouteEventStoreOnly_EnablesFluentChainingAsync() {
    // Arrange & Act
    var valueTask = Route.EventStoreOnly(new TestEvent("EventStore")).AsValueTask();

    // Assert
    var result = await valueTask;
    await Assert.That(result.Mode).IsEqualTo(DispatchModes.EventStoreOnly);
  }

  [Test]
  public async Task AsValueTask_WithRouteLocalNoPersist_EnablesFluentChainingAsync() {
    // Arrange & Act
    var valueTask = Route.LocalNoPersist(new TestEvent("NoPersist")).AsValueTask();

    // Assert
    var result = await valueTask;
    await Assert.That(result.Mode).IsEqualTo(DispatchModes.LocalNoPersist);
  }

  [Test]
  public async Task AsValueTask_WithRouteBoth_EnablesFluentChainingAsync() {
    // Arrange & Act
    var valueTask = Route.Both(new TestEvent("Both")).AsValueTask();

    // Assert
    var result = await valueTask;
    await Assert.That(result.Mode).IsEqualTo(DispatchModes.Both);
  }

  [Test]
  public async Task RoutedNone_AsValueTask_ReturnsCompletedValueTaskAsync() {
    // Arrange
    var routedNone = Route.None();

    // Act
    var valueTask = routedNone.AsValueTask();

    // Assert
    await Assert.That(valueTask.IsCompleted).IsTrue();
    var result = await valueTask;
    await Assert.That(result.Mode).IsEqualTo(DispatchModes.None);
    await Assert.That(result.Value).IsNull();
  }

  #endregion

  #region Test Types

  private sealed record TestEvent(string Name) : IEvent;

  #endregion
}
