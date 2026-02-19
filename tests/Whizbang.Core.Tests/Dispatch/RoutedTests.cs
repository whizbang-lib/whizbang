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
    var mode = DispatchMode.Local;

    // Act
    var routed = new Routed<TestEvent>(value, mode);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(value);
    await Assert.That(routed.Mode).IsEqualTo(mode);
  }

  [Test]
  public async Task Constructor_WithNullValue_AllowsNullAsync() {
    // Arrange & Act
    var routed = new Routed<TestEvent?>(null, DispatchMode.Outbox);

    // Assert
    await Assert.That(routed.Value).IsNull();
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Outbox);
  }

  [Test]
  public async Task Constructor_WithDifferentModes_SetsCorrectModeAsync() {
    // Test all modes
    var value = new TestEvent("Test");

    var routedNone = new Routed<TestEvent>(value, DispatchMode.None);
    var routedLocal = new Routed<TestEvent>(value, DispatchMode.Local);
    var routedOutbox = new Routed<TestEvent>(value, DispatchMode.Outbox);
    var routedBoth = new Routed<TestEvent>(value, DispatchMode.Both);

    await Assert.That(routedNone.Mode).IsEqualTo(DispatchMode.None);
    await Assert.That(routedLocal.Mode).IsEqualTo(DispatchMode.Local);
    await Assert.That(routedOutbox.Mode).IsEqualTo(DispatchMode.Outbox);
    await Assert.That(routedBoth.Mode).IsEqualTo(DispatchMode.Both);
  }

  #endregion

  #region IRouted Interface

  [Test]
  public async Task IRouted_Value_ReturnsValueAsObjectAsync() {
    // Arrange
    var value = new TestEvent("Test");
    var routed = new Routed<TestEvent>(value, DispatchMode.Local);

    // Act
    IRouted iRouted = routed;
    var objectValue = iRouted.Value;

    // Assert
    await Assert.That(objectValue).IsEqualTo(value);
  }

  [Test]
  public async Task IRouted_Mode_ReturnsSameModeAsync() {
    // Arrange
    var routed = new Routed<TestEvent>(new TestEvent("Test"), DispatchMode.Both);

    // Act
    IRouted iRouted = routed;

    // Assert
    await Assert.That(iRouted.Mode).IsEqualTo(DispatchMode.Both);
  }

  [Test]
  public async Task IRouted_CanPatternMatch_OnRoutedTypeAsync() {
    // Arrange
    object obj = new Routed<TestEvent>(new TestEvent("Test"), DispatchMode.Local);

    // Act
    var isRouted = obj is IRouted;

    // Assert
    await Assert.That(isRouted).IsTrue();
  }

  [Test]
  public async Task IRouted_PatternMatch_ExtractsValueAndModeAsync() {
    // Arrange
    var originalValue = new TestEvent("Test");
    object obj = new Routed<TestEvent>(originalValue, DispatchMode.Outbox);

    // Act
    var routed = obj as IRouted;

    // Assert
    await Assert.That(routed).IsNotNull();
    await Assert.That(routed!.Value).IsEqualTo(originalValue);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Outbox);
  }

  #endregion

  #region Value Types

  [Test]
  public async Task Routed_WithValueType_WorksCorrectlyAsync() {
    // Arrange
    var routed = new Routed<int>(42, DispatchMode.Local);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(42);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task Routed_WithArray_WorksCorrectlyAsync() {
    // Arrange
    var array = new[] { new TestEvent("A"), new TestEvent("B") };
    var routed = new Routed<TestEvent[]>(array, DispatchMode.Both);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(array);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Both);
  }

  [Test]
  public async Task Routed_WithTuple_WorksCorrectlyAsync() {
    // Arrange
    var tuple = (new TestEvent("A"), new TestEvent("B"));
    var routed = new Routed<(TestEvent, TestEvent)>(tuple, DispatchMode.Outbox);

    // Assert
    await Assert.That(routed.Value).IsEqualTo(tuple);
    await Assert.That(routed.Mode).IsEqualTo(DispatchMode.Outbox);
  }

  #endregion

  #region Struct Behavior

  [Test]
  public async Task Routed_IsValueType_NoHeapAllocationAsync() {
    // Arrange
    var routed = new Routed<TestEvent>(new TestEvent("Test"), DispatchMode.Local);

    // Assert - Routed<T> should be a value type (struct)
    await Assert.That(routed.GetType().IsValueType).IsTrue();
  }

  [Test]
  public async Task Routed_DefaultValue_HasNoneMode_AndDefaultValueAsync() {
    // Arrange
    var defaultRouted = default(Routed<TestEvent>);

    // Assert
    await Assert.That(defaultRouted.Value).IsNull();
    await Assert.That(defaultRouted.Mode).IsEqualTo(DispatchMode.None);
  }

  #endregion

  #region Test Types

  private sealed record TestEvent(string Name) : IEvent;

  #endregion
}
