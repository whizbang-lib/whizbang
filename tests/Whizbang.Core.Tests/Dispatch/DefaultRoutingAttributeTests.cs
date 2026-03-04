using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatch;

/// <summary>
/// Tests for DefaultRoutingAttribute which specifies default dispatch routing for message types or receptors.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/DefaultRoutingAttribute.cs</code-under-test>
public class DefaultRoutingAttributeTests {
  #region Constructor and Properties

  [Test]
  public async Task Constructor_WithLocalMode_SetsModePropertyAsync() {
    // Arrange & Act
    var attr = new DefaultRoutingAttribute(DispatchMode.Local);

    // Assert
    await Assert.That(attr.Mode).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task Constructor_WithOutboxMode_SetsModePropertyAsync() {
    // Arrange & Act
    var attr = new DefaultRoutingAttribute(DispatchMode.Outbox);

    // Assert
    await Assert.That(attr.Mode).IsEqualTo(DispatchMode.Outbox);
  }

  [Test]
  public async Task Constructor_WithBothMode_SetsModePropertyAsync() {
    // Arrange & Act
    var attr = new DefaultRoutingAttribute(DispatchMode.Both);

    // Assert
    await Assert.That(attr.Mode).IsEqualTo(DispatchMode.Both);
  }

  [Test]
  public async Task Constructor_WithNoneMode_SetsModePropertyAsync() {
    // Arrange & Act
    var attr = new DefaultRoutingAttribute(DispatchMode.None);

    // Assert
    await Assert.That(attr.Mode).IsEqualTo(DispatchMode.None);
  }

  #endregion

  #region Attribute Usage

  [Test]
  public async Task Attribute_CanBeAppliedToClass_WithLocalModeAsync() {
    // Arrange
    var type = typeof(LocalRoutedEvent);

    // Act
    var attr = type.GetCustomAttribute<DefaultRoutingAttribute>();

    // Assert
    await Assert.That(attr).IsNotNull();
    await Assert.That(attr!.Mode).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task Attribute_CanBeAppliedToClass_WithOutboxModeAsync() {
    // Arrange
    var type = typeof(OutboxRoutedEvent);

    // Act
    var attr = type.GetCustomAttribute<DefaultRoutingAttribute>();

    // Assert
    await Assert.That(attr).IsNotNull();
    await Assert.That(attr!.Mode).IsEqualTo(DispatchMode.Outbox);
  }

  [Test]
  public async Task Attribute_CanBeAppliedToClass_WithBothModeAsync() {
    // Arrange
    var type = typeof(BothRoutedEvent);

    // Act
    var attr = type.GetCustomAttribute<DefaultRoutingAttribute>();

    // Assert
    await Assert.That(attr).IsNotNull();
    await Assert.That(attr!.Mode).IsEqualTo(DispatchMode.Both);
  }

  [Test]
  public async Task Attribute_CanBeAppliedToStruct_Async() {
    // Arrange
    var type = typeof(LocalRoutedStructEvent);

    // Act
    var attr = type.GetCustomAttribute<DefaultRoutingAttribute>();

    // Assert
    await Assert.That(attr).IsNotNull();
    await Assert.That(attr!.Mode).IsEqualTo(DispatchMode.Local);
  }

  [Test]
  public async Task Attribute_CanBeAppliedToRecord_Async() {
    // Arrange
    var type = typeof(LocalRoutedRecordEvent);

    // Act
    var attr = type.GetCustomAttribute<DefaultRoutingAttribute>();

    // Assert
    await Assert.That(attr).IsNotNull();
    await Assert.That(attr!.Mode).IsEqualTo(DispatchMode.Local);
  }

  #endregion

  #region Attribute Targets

  [Test]
  public async Task Attribute_HasCorrectTargets_ClassAndStructAsync() {
    // Arrange
    var attrType = typeof(DefaultRoutingAttribute);

    // Act
    var usageAttr = attrType.GetCustomAttribute<AttributeUsageAttribute>();

    // Assert
    await Assert.That(usageAttr).IsNotNull();
    await Assert.That(usageAttr!.ValidOn.HasFlag(AttributeTargets.Class)).IsTrue();
    await Assert.That(usageAttr.ValidOn.HasFlag(AttributeTargets.Struct)).IsTrue();
  }

  #endregion

  #region Type Without Attribute

  [Test]
  public async Task Type_WithoutAttribute_ReturnsNullAsync() {
    // Arrange
    var type = typeof(UnroutedEvent);

    // Act
    var attr = type.GetCustomAttribute<DefaultRoutingAttribute>();

    // Assert
    await Assert.That(attr).IsNull();
  }

  #endregion

  #region Test Types

  [DefaultRouting(DispatchMode.Local)]
  private sealed class LocalRoutedEvent : IEvent { }

  [DefaultRouting(DispatchMode.Outbox)]
  private sealed class OutboxRoutedEvent : IEvent { }

  [DefaultRouting(DispatchMode.Both)]
  private sealed class BothRoutedEvent : IEvent { }

  [DefaultRouting(DispatchMode.Local)]
  private struct LocalRoutedStructEvent : IEvent { }

  [DefaultRouting(DispatchMode.Local)]
  private sealed record LocalRoutedRecordEvent : IEvent;

  private sealed class UnroutedEvent : IEvent { }

  #endregion
}
