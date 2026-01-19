using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for ConfigurationTopicRegistry.
/// Validates configuration-based topic mappings for types without [Topic] attributes.
/// </summary>
public class ConfigurationTopicRegistryTests {
  private sealed record TestEvent : IEvent;
  private sealed record TestCommand : ICommand;
  private sealed record AnotherEvent : IEvent;

  [Test]
  public async Task GetBaseTopic_WithConfiguredType_ReturnsTopicAsync() {
    // Arrange
    var mappings = new Dictionary<Type, string> {
      [typeof(TestEvent)] = "products",
      [typeof(TestCommand)] = "inventory"
    };
    var registry = new ConfigurationTopicRegistry(mappings);

    // Act
    var eventTopic = registry.GetBaseTopic(typeof(TestEvent));
    var commandTopic = registry.GetBaseTopic(typeof(TestCommand));

    // Assert
    await Assert.That(eventTopic).IsEqualTo("products");
    await Assert.That(commandTopic).IsEqualTo("inventory");
  }

  [Test]
  public async Task GetBaseTopic_WithUnconfiguredType_ReturnsNullAsync() {
    // Arrange
    var mappings = new Dictionary<Type, string> {
      [typeof(TestEvent)] = "products"
    };
    var registry = new ConfigurationTopicRegistry(mappings);

    // Act
    var result = registry.GetBaseTopic(typeof(AnotherEvent));

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetBaseTopic_WithEmptyConfiguration_ReturnsNullAsync() {
    // Arrange
    var mappings = new Dictionary<Type, string>();
    var registry = new ConfigurationTopicRegistry(mappings);

    // Act
    var result = registry.GetBaseTopic(typeof(TestEvent));

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetBaseTopic_CalledMultipleTimes_ReturnsSameTopicAsync() {
    // Arrange
    var mappings = new Dictionary<Type, string> {
      [typeof(TestEvent)] = "products"
    };
    var registry = new ConfigurationTopicRegistry(mappings);

    // Act
    var result1 = registry.GetBaseTopic(typeof(TestEvent));
    var result2 = registry.GetBaseTopic(typeof(TestEvent));
    var result3 = registry.GetBaseTopic(typeof(TestEvent));

    // Assert - Should return consistent results (idempotent)
    await Assert.That(result1).IsEqualTo("products");
    await Assert.That(result2).IsEqualTo("products");
    await Assert.That(result3).IsEqualTo("products");
  }

  [Test]
  public async Task GetBaseTopic_WithMultipleTypes_ReturnsCorrectTopicForEachAsync() {
    // Arrange
    var mappings = new Dictionary<Type, string> {
      [typeof(TestEvent)] = "products",
      [typeof(TestCommand)] = "inventory",
      [typeof(AnotherEvent)] = "orders"
    };
    var registry = new ConfigurationTopicRegistry(mappings);

    // Act & Assert
    await Assert.That(registry.GetBaseTopic(typeof(TestEvent))).IsEqualTo("products");
    await Assert.That(registry.GetBaseTopic(typeof(TestCommand))).IsEqualTo("inventory");
    await Assert.That(registry.GetBaseTopic(typeof(AnotherEvent))).IsEqualTo("orders");
  }
}
