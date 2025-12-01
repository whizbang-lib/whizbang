using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports.AzureServiceBus;

namespace Whizbang.Core.Tests.Transports.AzureServiceBus;

/// <summary>
/// Tests for TopicRequirement value object.
/// Ensures value equality semantics and immutability.
/// </summary>
public class TopicRequirementTests
{
  [Test]
  public async Task TopicRequirement_Constructor_SetsPropertiesAsync()
  {
    // Arrange & Act
    var requirement = new TopicRequirement("orders", "bff-orders");

    // Assert
    await Assert.That(requirement.TopicName).IsEqualTo("orders");
    await Assert.That(requirement.SubscriptionName).IsEqualTo("bff-orders");
  }

  [Test]
  public async Task TopicRequirement_WithSameValues_AreEqualAsync()
  {
    // Arrange
    var req1 = new TopicRequirement("orders", "bff-orders");
    var req2 = new TopicRequirement("orders", "bff-orders");

    // Act & Assert - Value equality (critical for record types)
    await Assert.That(req1).IsEqualTo(req2);
    await Assert.That(req1.GetHashCode()).IsEqualTo(req2.GetHashCode());
  }

  [Test]
  public async Task TopicRequirement_WithDifferentTopicNames_AreNotEqualAsync()
  {
    // Arrange
    var req1 = new TopicRequirement("orders", "bff-orders");
    var req2 = new TopicRequirement("products", "bff-orders");

    // Act & Assert
    await Assert.That(req1).IsNotEqualTo(req2);
  }

  [Test]
  public async Task TopicRequirement_WithDifferentSubscriptionNames_AreNotEqualAsync()
  {
    // Arrange
    var req1 = new TopicRequirement("orders", "bff-orders");
    var req2 = new TopicRequirement("orders", "payment-orders");

    // Act & Assert
    await Assert.That(req1).IsNotEqualTo(req2);
  }

  [Test]
  public async Task TopicRequirement_ToString_ReturnsFormattedStringAsync()
  {
    // Arrange
    var requirement = new TopicRequirement("orders", "bff-orders");

    // Act
    var result = requirement.ToString();

    // Assert - Should include both topic and subscription names
    await Assert.That(result).Contains("orders");
    await Assert.That(result).Contains("bff-orders");
  }

  [Test]
  public async Task TopicRequirement_WithEmptyTopicName_AllowedAsync()
  {
    // Arrange & Act - Records allow any string values
    var requirement = new TopicRequirement("", "bff-orders");

    // Assert
    await Assert.That(requirement.TopicName).IsEqualTo("");
  }

  [Test]
  public async Task TopicRequirement_WithEmptySubscriptionName_AllowedAsync()
  {
    // Arrange & Act
    var requirement = new TopicRequirement("orders", "");

    // Assert
    await Assert.That(requirement.SubscriptionName).IsEqualTo("");
  }

  [Test]
  public async Task TopicRequirement_Deconstruct_ExtractsValuesAsync()
  {
    // Arrange
    var requirement = new TopicRequirement("orders", "bff-orders");

    // Act - Records support deconstruction
    var (topicName, subscriptionName) = requirement;

    // Assert
    await Assert.That(topicName).IsEqualTo("orders");
    await Assert.That(subscriptionName).IsEqualTo("bff-orders");
  }
}
