using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.Attributes;

[Category("Core")]
[Category("Attributes")]
public class TopicAttributeTests {
  [Test]
  public async Task Constructor_WithValidTopicName_SetsTopicNameAsync() {
    // Arrange
    const string topicName = "products";

    // Act
    var attribute = new TopicAttribute(topicName);

    // Assert
    await Assert.That(attribute.TopicName).IsEqualTo(topicName);
  }

  [Test]
  public async Task Constructor_WithNullTopicName_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new TopicAttribute(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithEmptyTopicName_ThrowsArgumentExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new TopicAttribute(string.Empty))
        .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task Constructor_WithWhitespaceTopicName_ThrowsArgumentExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new TopicAttribute("   "))
        .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task Constructor_WithDifferentTopicNames_CreatesDistinctInstancesAsync() {
    // Arrange & Act
    var attribute1 = new TopicAttribute("products");
    var attribute2 = new TopicAttribute("inventory");

    // Assert
    await Assert.That(attribute1.TopicName).IsNotEqualTo(attribute2.TopicName);
  }

  [Test]
  public async Task TopicAttribute_CanBeAppliedToClassAsync() {
    // Arrange & Act
    var type = typeof(TestEventWithTopic);
    var attributes = type.GetCustomAttributes(typeof(TopicAttribute), false);

    // Assert
    await Assert.That(attributes).IsNotEmpty();
    var attr = attributes.First() as TopicAttribute;
    await Assert.That(attr).IsNotNull();
    await Assert.That(attr!.TopicName).IsEqualTo("test-events");
  }

  [Test]
  public async Task TopicAttribute_AttributeUsageAllowsOnlyClassesAsync() {
    // Arrange
    var attributeUsage = typeof(TopicAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .FirstOrDefault() as AttributeUsageAttribute;

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn).IsEqualTo(AttributeTargets.Class);
  }

  [Test]
  public async Task TopicAttribute_AttributeUsageDoesNotAllowMultipleAsync() {
    // Arrange
    var attributeUsage = typeof(TopicAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .FirstOrDefault() as AttributeUsageAttribute;

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task TopicAttribute_AttributeUsageIsNotInheritedAsync() {
    // Arrange
    var attributeUsage = typeof(TopicAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .FirstOrDefault() as AttributeUsageAttribute;

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.Inherited).IsFalse();
  }

  [Topic("test-events")]
  private sealed class TestEventWithTopic { }
}
