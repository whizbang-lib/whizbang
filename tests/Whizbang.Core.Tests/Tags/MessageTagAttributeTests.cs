using System;
using System.Linq;
using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="MessageTagAttribute"/> base class.
/// Validates attribute behavior, inheritance support, and property extraction configuration.
/// </summary>
/// <tests>Whizbang.Core/Attributes/MessageTagAttribute.cs</tests>
[Category("Core")]
[Category("Attributes")]
[Category("Tags")]
public class MessageTagAttributeTests {

  // Test concrete implementation for testing the abstract base class
  [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = true)]
  private sealed class TestTagAttribute : MessageTagAttribute { }

  [Test]
  public async Task MessageTagAttribute_IsAbstract_CannotBeInstantiatedDirectlyAsync() {
    // Arrange & Act
    var isAbstract = typeof(MessageTagAttribute).IsAbstract;

    // Assert
    await Assert.That(isAbstract).IsTrue();
  }

  [Test]
  public async Task MessageTagAttribute_AttributeUsage_AllowsClassTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(MessageTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Class)).IsTrue();
  }

  [Test]
  public async Task MessageTagAttribute_AttributeUsage_AllowsStructTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(MessageTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Struct)).IsTrue();
  }

  [Test]
  public async Task MessageTagAttribute_AttributeUsage_AllowsMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(MessageTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsTrue();
  }

  [Test]
  public async Task MessageTagAttribute_AttributeUsage_IsInheritedAsync() {
    // Arrange & Act
    var attributeUsage = typeof(MessageTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.Inherited).IsTrue();
  }

  [Test]
  public async Task MessageTagAttribute_Tag_IsRequiredPropertyAsync() {
    // Arrange
    var tagProperty = typeof(MessageTagAttribute).GetProperty(nameof(MessageTagAttribute.Tag));

    // Assert - Tag should be a required init property
    await Assert.That(tagProperty).IsNotNull();
    await Assert.That(tagProperty!.PropertyType).IsEqualTo(typeof(string));
    await Assert.That(tagProperty.CanRead).IsTrue();
  }

  [Test]
  public async Task MessageTagAttribute_Tag_CanBeSetViaInitAsync() {
    // Arrange & Act
    var attribute = new TestTagAttribute { Tag = "test-tag" };

    // Assert
    await Assert.That(attribute.Tag).IsEqualTo("test-tag");
  }

  [Test]
  public async Task MessageTagAttribute_Properties_IsNullByDefaultAsync() {
    // Arrange & Act
    var attribute = new TestTagAttribute { Tag = "test-tag" };

    // Assert
    await Assert.That(attribute.Properties).IsNull();
  }

  [Test]
  public async Task MessageTagAttribute_Properties_CanBeSetToArrayAsync() {
    // Arrange & Act
    var attribute = new TestTagAttribute {
      Tag = "test-tag",
      Properties = ["OrderId", "Status", "TenantId"]
    };

    // Assert
    await Assert.That(attribute.Properties).IsNotNull();
    await Assert.That(attribute.Properties!.Length).IsEqualTo(3);
    await Assert.That(attribute.Properties).Contains("OrderId");
    await Assert.That(attribute.Properties).Contains("Status");
    await Assert.That(attribute.Properties).Contains("TenantId");
  }

  [Test]
  public async Task MessageTagAttribute_IncludeEvent_IsFalseByDefaultAsync() {
    // Arrange & Act
    var attribute = new TestTagAttribute { Tag = "test-tag" };

    // Assert
    await Assert.That(attribute.IncludeEvent).IsFalse();
  }

  [Test]
  public async Task MessageTagAttribute_IncludeEvent_CanBeSetToTrueAsync() {
    // Arrange & Act
    var attribute = new TestTagAttribute {
      Tag = "test-tag",
      IncludeEvent = true
    };

    // Assert
    await Assert.That(attribute.IncludeEvent).IsTrue();
  }

  [Test]
  public async Task MessageTagAttribute_ExtraJson_IsNullByDefaultAsync() {
    // Arrange & Act
    var attribute = new TestTagAttribute { Tag = "test-tag" };

    // Assert
    await Assert.That(attribute.ExtraJson).IsNull();
  }

  [Test]
  public async Task MessageTagAttribute_ExtraJson_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new TestTagAttribute {
      Tag = "test-tag",
      ExtraJson = """{"source": "api", "version": 2}"""
    };

    // Assert
    await Assert.That(attribute.ExtraJson).IsEqualTo("""{"source": "api", "version": 2}""");
  }

  [Test]
  public async Task MessageTagAttribute_AllProperties_CanBeCombinedAsync() {
    // Arrange & Act
    var attribute = new TestTagAttribute {
      Tag = "order-updated",
      Properties = ["OrderId", "Status"],
      IncludeEvent = true,
      ExtraJson = """{"source": "api"}"""
    };

    // Assert
    await Assert.That(attribute.Tag).IsEqualTo("order-updated");
    await Assert.That(attribute.Properties).IsNotNull();
    await Assert.That(attribute.Properties!.Length).IsEqualTo(2);
    await Assert.That(attribute.IncludeEvent).IsTrue();
    await Assert.That(attribute.ExtraJson).IsEqualTo("""{"source": "api"}""");
  }

  [Test]
  public async Task MessageTagAttribute_InheritedAttribute_CanBeDiscoveredAsync() {
    // Arrange
    var targetType = typeof(TestEventWithTags);

    // Act - Get attributes including inherited
    var attributes = targetType
      .GetCustomAttributes(typeof(MessageTagAttribute), true)
      .Cast<MessageTagAttribute>()
      .ToArray();

    // Assert
    await Assert.That(attributes).IsNotEmpty();
    await Assert.That(attributes.Length).IsEqualTo(1);
    await Assert.That(attributes[0].Tag).IsEqualTo("test-event");
  }

  [Test]
  public async Task MessageTagAttribute_MultipleAttributes_CanBeAppliedAsync() {
    // Arrange
    var targetType = typeof(TestEventWithMultipleTags);

    // Act
    var attributes = targetType
      .GetCustomAttributes(typeof(MessageTagAttribute), true)
      .Cast<MessageTagAttribute>()
      .ToArray();

    // Assert
    await Assert.That(attributes.Length).IsEqualTo(2);
    var tags = attributes.Select(a => a.Tag).ToArray();
    await Assert.That(tags).Contains("notification");
    await Assert.That(tags).Contains("audit");
  }

  // Test helper types
  [TestTag(Tag = "test-event")]
  private sealed record TestEventWithTags(Guid Id, string Name);

  [TestTag(Tag = "notification")]
  [TestTag(Tag = "audit")]
  private sealed record TestEventWithMultipleTags(Guid Id);
}
