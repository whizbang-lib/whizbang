using System;
using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="NotificationTagAttribute"/>.
/// Validates real-time notification tagging for SignalR/WebSocket integration.
/// </summary>
/// <tests>Whizbang.Core/Attributes/NotificationTagAttribute.cs</tests>
[Category("Core")]
[Category("Attributes")]
[Category("Tags")]
public class NotificationTagAttributeTests {

  [Test]
  public async Task NotificationTagAttribute_InheritsFromMessageTagAttributeAsync() {
    // Assert
    await Assert.That(typeof(NotificationTagAttribute).BaseType).IsEqualTo(typeof(MessageTagAttribute));
  }

  [Test]
  public async Task NotificationTagAttribute_AttributeUsage_AllowsClassTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(NotificationTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Class)).IsTrue();
  }

  [Test]
  public async Task NotificationTagAttribute_AttributeUsage_AllowsStructTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(NotificationTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Struct)).IsTrue();
  }

  [Test]
  public async Task NotificationTagAttribute_AttributeUsage_AllowsMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(NotificationTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsTrue();
  }

  [Test]
  public async Task NotificationTagAttribute_Tag_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new NotificationTagAttribute { Tag = "order-shipped" };

    // Assert
    await Assert.That(attribute.Tag).IsEqualTo("order-shipped");
  }

  [Test]
  public async Task NotificationTagAttribute_Group_IsNullByDefaultAsync() {
    // Arrange & Act
    var attribute = new NotificationTagAttribute { Tag = "test-tag" };

    // Assert
    await Assert.That(attribute.Group).IsNull();
  }

  [Test]
  public async Task NotificationTagAttribute_Group_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new NotificationTagAttribute {
      Tag = "order-shipped",
      Group = "customer-{CustomerId}"
    };

    // Assert
    await Assert.That(attribute.Group).IsEqualTo("customer-{CustomerId}");
  }

  [Test]
  public async Task NotificationTagAttribute_Priority_DefaultsToNormalAsync() {
    // Arrange & Act
    var attribute = new NotificationTagAttribute { Tag = "test-tag" };

    // Assert
    await Assert.That(attribute.Priority).IsEqualTo(NotificationPriority.Normal);
  }

  [Test]
  public async Task NotificationTagAttribute_Priority_CanBeSetToHighAsync() {
    // Arrange & Act
    var attribute = new NotificationTagAttribute {
      Tag = "urgent-alert",
      Priority = NotificationPriority.High
    };

    // Assert
    await Assert.That(attribute.Priority).IsEqualTo(NotificationPriority.High);
  }

  [Test]
  public async Task NotificationTagAttribute_Priority_CanBeSetToCriticalAsync() {
    // Arrange & Act
    var attribute = new NotificationTagAttribute {
      Tag = "system-failure",
      Priority = NotificationPriority.Critical
    };

    // Assert
    await Assert.That(attribute.Priority).IsEqualTo(NotificationPriority.Critical);
  }

  [Test]
  public async Task NotificationTagAttribute_Priority_CanBeSetToLowAsync() {
    // Arrange & Act
    var attribute = new NotificationTagAttribute {
      Tag = "background-update",
      Priority = NotificationPriority.Low
    };

    // Assert
    await Assert.That(attribute.Priority).IsEqualTo(NotificationPriority.Low);
  }

  [Test]
  public async Task NotificationTagAttribute_InheritsBasePropertiesAsync() {
    // Arrange & Act
    var attribute = new NotificationTagAttribute {
      Tag = "order-updated",
      Properties = ["OrderId", "Status"],
      IncludeEvent = true,
      ExtraJson = """{"source": "api"}""",
      Group = "tenant-{TenantId}",
      Priority = NotificationPriority.High
    };

    // Assert - Base properties work
    await Assert.That(attribute.Tag).IsEqualTo("order-updated");
    await Assert.That(attribute.Properties).IsNotNull();
    await Assert.That(attribute.Properties!.Length).IsEqualTo(2);
    await Assert.That(attribute.IncludeEvent).IsTrue();
    await Assert.That(attribute.ExtraJson).IsEqualTo("""{"source": "api"}""");

    // Assert - NotificationTag-specific properties work
    await Assert.That(attribute.Group).IsEqualTo("tenant-{TenantId}");
    await Assert.That(attribute.Priority).IsEqualTo(NotificationPriority.High);
  }

  [Test]
  public async Task NotificationTagAttribute_CanBeAppliedToEventAsync() {
    // Arrange
    var targetType = typeof(TestOrderShippedEvent);

    // Act
    var attributes = targetType
      .GetCustomAttributes(typeof(NotificationTagAttribute), true)
      .Cast<NotificationTagAttribute>()
      .ToArray();

    // Assert
    await Assert.That(attributes.Length).IsEqualTo(1);
    await Assert.That(attributes[0].Tag).IsEqualTo("order-shipped");
    await Assert.That(attributes[0].Group).IsEqualTo("customer-{CustomerId}");
    await Assert.That(attributes[0].Priority).IsEqualTo(NotificationPriority.High);
  }

  // Test helper type
  [NotificationTag(
    Tag = "order-shipped",
    Properties = ["OrderId", "CustomerId", "TrackingNumber"],
    Group = "customer-{CustomerId}",
    Priority = NotificationPriority.High)]
  private sealed record TestOrderShippedEvent(Guid OrderId, Guid CustomerId, string TrackingNumber);
}
