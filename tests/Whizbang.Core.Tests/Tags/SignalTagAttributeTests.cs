using System;
using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="SignalTagAttribute"/>.
/// Validates real-time notification tagging for SignalR/WebSocket integration.
/// </summary>
/// <tests>Whizbang.Core/Attributes/SignalTagAttribute.cs</tests>
[Category("Core")]
[Category("Attributes")]
[Category("Tags")]
public class SignalTagAttributeTests {

  [Test]
  public async Task SignalTagAttribute_InheritsFromMessageTagAttributeAsync() {
    // Assert
    await Assert.That(typeof(SignalTagAttribute).BaseType).IsEqualTo(typeof(MessageTagAttribute));
  }

  [Test]
  public async Task SignalTagAttribute_AttributeUsage_AllowsClassTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(SignalTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Class)).IsTrue();
  }

  [Test]
  public async Task SignalTagAttribute_AttributeUsage_AllowsStructTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(SignalTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Struct)).IsTrue();
  }

  [Test]
  public async Task SignalTagAttribute_AttributeUsage_AllowsMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(SignalTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsTrue();
  }

  [Test]
  public async Task SignalTagAttribute_Tag_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new SignalTagAttribute { Tag = "order-shipped" };

    // Assert
    await Assert.That(attribute.Tag).IsEqualTo("order-shipped");
  }

  [Test]
  public async Task SignalTagAttribute_Group_IsNullByDefaultAsync() {
    // Arrange & Act
    var attribute = new SignalTagAttribute { Tag = "test-tag" };

    // Assert
    await Assert.That(attribute.Group).IsNull();
  }

  [Test]
  public async Task SignalTagAttribute_Group_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new SignalTagAttribute {
      Tag = "order-shipped",
      Group = "customer-{CustomerId}"
    };

    // Assert
    await Assert.That(attribute.Group).IsEqualTo("customer-{CustomerId}");
  }

  [Test]
  public async Task SignalTagAttribute_Priority_DefaultsToNormalAsync() {
    // Arrange & Act
    var attribute = new SignalTagAttribute { Tag = "test-tag" };

    // Assert
    await Assert.That(attribute.Priority).IsEqualTo(SignalPriority.Normal);
  }

  [Test]
  public async Task SignalTagAttribute_Priority_CanBeSetToHighAsync() {
    // Arrange & Act
    var attribute = new SignalTagAttribute {
      Tag = "urgent-alert",
      Priority = SignalPriority.High
    };

    // Assert
    await Assert.That(attribute.Priority).IsEqualTo(SignalPriority.High);
  }

  [Test]
  public async Task SignalTagAttribute_Priority_CanBeSetToCriticalAsync() {
    // Arrange & Act
    var attribute = new SignalTagAttribute {
      Tag = "system-failure",
      Priority = SignalPriority.Critical
    };

    // Assert
    await Assert.That(attribute.Priority).IsEqualTo(SignalPriority.Critical);
  }

  [Test]
  public async Task SignalTagAttribute_Priority_CanBeSetToLowAsync() {
    // Arrange & Act
    var attribute = new SignalTagAttribute {
      Tag = "background-update",
      Priority = SignalPriority.Low
    };

    // Assert
    await Assert.That(attribute.Priority).IsEqualTo(SignalPriority.Low);
  }

  [Test]
  public async Task SignalTagAttribute_InheritsBasePropertiesAsync() {
    // Arrange & Act
    var attribute = new SignalTagAttribute {
      Tag = "order-updated",
      Properties = ["OrderId", "Status"],
      IncludeEvent = true,
      ExtraJson = """{"source": "api"}""",
      Group = "tenant-{TenantId}",
      Priority = SignalPriority.High
    };

    // Assert - Base properties work
    await Assert.That(attribute.Tag).IsEqualTo("order-updated");
    await Assert.That(attribute.Properties).IsNotNull();
    await Assert.That(attribute.Properties!.Length).IsEqualTo(2);
    await Assert.That(attribute.IncludeEvent).IsTrue();
    await Assert.That(attribute.ExtraJson).IsEqualTo("""{"source": "api"}""");

    // Assert - SignalTag-specific properties work
    await Assert.That(attribute.Group).IsEqualTo("tenant-{TenantId}");
    await Assert.That(attribute.Priority).IsEqualTo(SignalPriority.High);
  }

  [Test]
  public async Task SignalTagAttribute_CanBeAppliedToEventAsync() {
    // Arrange
    var targetType = typeof(TestOrderShippedEvent);

    // Act
    var attributes = targetType
      .GetCustomAttributes(typeof(SignalTagAttribute), true)
      .Cast<SignalTagAttribute>()
      .ToArray();

    // Assert
    await Assert.That(attributes.Length).IsEqualTo(1);
    await Assert.That(attributes[0].Tag).IsEqualTo("order-shipped");
    await Assert.That(attributes[0].Group).IsEqualTo("customer-{CustomerId}");
    await Assert.That(attributes[0].Priority).IsEqualTo(SignalPriority.High);
  }

  // Test helper type
  [SignalTag(
    Tag = "order-shipped",
    Properties = ["OrderId", "CustomerId", "TrackingNumber"],
    Group = "customer-{CustomerId}",
    Priority = SignalPriority.High)]
  private sealed record TestOrderShippedEvent(Guid OrderId, Guid CustomerId, string TrackingNumber);
}
