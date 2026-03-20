using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for ITopicRoutingStrategy implementations.
/// Validates PassthroughRoutingStrategy, PoolSuffixRoutingStrategy, and CompositeTopicRoutingStrategy.
/// </summary>
public class TopicRoutingStrategyTests {
  private sealed record TestEvent : IEvent;
  private sealed record TestCommand : ICommand;

  [Test]
  public async Task PassthroughRoutingStrategy_ReturnsBaseTopicUnchangedAsync() {
    // Arrange
    var strategy = PassthroughRoutingStrategy.Instance;
    var messageType = typeof(TestEvent);
    var baseTopic = "products";

    // Act
    var result = strategy.ResolveTopic(messageType, baseTopic);

    // Assert
    await Assert.That(result).IsEqualTo("products");
  }

  [Test]
  public async Task PassthroughRoutingStrategy_WithContext_IgnoresContextAsync() {
    // Arrange
    var strategy = PassthroughRoutingStrategy.Instance;
    var messageType = typeof(TestEvent);
    var baseTopic = "inventory";
    var context = new Dictionary<string, object> { ["TenantId"] = "tenant-123" };

    // Act
    var result = strategy.ResolveTopic(messageType, baseTopic, context);

    // Assert - Context is ignored
    await Assert.That(result).IsEqualTo("inventory");
  }

  [Test]
  public async Task PoolSuffixRoutingStrategy_AppendsPoolSuffixAsync() {
    // Arrange
    var strategy = new PoolSuffixRoutingStrategy("01");
    var messageType = typeof(TestEvent);
    var baseTopic = "products";

    // Act
    var result = strategy.ResolveTopic(messageType, baseTopic);

    // Assert
    await Assert.That(result).IsEqualTo("products-01");
  }

  [Test]
  public async Task PoolSuffixRoutingStrategy_WithMultipleDigitSuffix_AppendsCorrectlyAsync() {
    // Arrange
    var strategy = new PoolSuffixRoutingStrategy("09");
    var messageType = typeof(TestEvent);
    var baseTopic = "inventory";

    // Act
    var result = strategy.ResolveTopic(messageType, baseTopic);

    // Assert
    await Assert.That(result).IsEqualTo("inventory-09");
  }

  [Test]
  public async Task PoolSuffixRoutingStrategy_WithNullOrWhitespaceSuffix_ThrowsAsync() {
    // Assert - null suffix
    await Assert.ThrowsAsync<ArgumentException>(async () => {
      var strategy = new PoolSuffixRoutingStrategy(null!);
      await Task.CompletedTask;
    });

    // Assert - empty suffix
    await Assert.ThrowsAsync<ArgumentException>(async () => {
      var strategy = new PoolSuffixRoutingStrategy("");
      await Task.CompletedTask;
    });

    // Assert - whitespace suffix
    await Assert.ThrowsAsync<ArgumentException>(async () => {
      var strategy = new PoolSuffixRoutingStrategy("   ");
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task CompositeTopicRoutingStrategy_AppliesStrategiesInOrderAsync() {
    // Arrange
    var tenantStrategy = new TenantPrefixStrategy("tenant-A");
    var poolStrategy = new PoolSuffixRoutingStrategy("02");
    var composite = new CompositeTopicRoutingStrategy(tenantStrategy, poolStrategy);

    var messageType = typeof(TestEvent);
    var baseTopic = "products";

    // Act
    var result = composite.ResolveTopic(messageType, baseTopic);

    // Assert - Should apply tenant prefix first, then pool suffix
    await Assert.That(result).IsEqualTo("tenant-A-products-02");
  }

  [Test]
  public async Task CompositeTopicRoutingStrategy_WithSingleStrategy_WorksAsync() {
    // Arrange
    var poolStrategy = new PoolSuffixRoutingStrategy("03");
    var composite = new CompositeTopicRoutingStrategy(poolStrategy);

    var messageType = typeof(TestEvent);
    var baseTopic = "inventory";

    // Act
    var result = composite.ResolveTopic(messageType, baseTopic);

    // Assert
    await Assert.That(result).IsEqualTo("inventory-03");
  }

  [Test]
  public async Task CompositeTopicRoutingStrategy_WithEmptyStrategies_ThrowsAsync() {
    // Assert
    await Assert.ThrowsAsync<ArgumentException>(async () => {
      var composite = new CompositeTopicRoutingStrategy([]);
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task CompositeTopicRoutingStrategy_WithNullStrategies_ThrowsAsync() {
    // Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      var composite = new CompositeTopicRoutingStrategy(null!);
      await Task.CompletedTask;
    });
  }

  /// <summary>
  /// Test helper strategy that adds tenant prefix.
  /// Example: "products" → "tenant-A-products"
  /// </summary>
  private sealed class TenantPrefixStrategy(string tenantId) : ITopicRoutingStrategy {
    private readonly string _tenantId = tenantId;

    public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
      return $"{_tenantId}-{baseTopic}";
    }
  }

  // ===============================================================================
  // NamespaceRoutingStrategy Tests
  // ===============================================================================

  [Test]
  public async Task NamespaceRoutingStrategy_ReturnsFullNamespaceAsync() {
    // Arrange - MyApp.Orders.Events.OrderCreated → "testnamespaces.myapp.orders.events"
    var strategy = new NamespaceRoutingStrategy();

    // Act
    var result = strategy.ResolveTopic(typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated), "ignored");

    // Assert - Returns full namespace in lowercase
    await Assert.That(result).IsEqualTo("testnamespaces.myapp.orders.events");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_CommandNamespace_ReturnsFullNamespaceAsync() {
    // Arrange - MyApp.Contracts.Commands.CreateOrder → "testnamespaces.myapp.contracts.commands"
    var strategy = new NamespaceRoutingStrategy();

    // Act
    var result = strategy.ResolveTopic(typeof(TestNamespaces.MyApp.Contracts.Commands.CreateOrder), "ignored");

    // Assert - Returns full namespace in lowercase
    await Assert.That(result).IsEqualTo("testnamespaces.myapp.contracts.commands");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_EventNamespace_ReturnsFullNamespaceAsync() {
    // Arrange - MyApp.Contracts.Events.OrderCreated → "testnamespaces.myapp.contracts.events"
    var strategy = new NamespaceRoutingStrategy();

    // Act
    var result = strategy.ResolveTopic(typeof(TestNamespaces.MyApp.Contracts.Events.OrderCreated), "ignored");

    // Assert - Returns full namespace in lowercase
    await Assert.That(result).IsEqualTo("testnamespaces.myapp.contracts.events");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_CustomMapping_OverridesDefaultAsync() {
    // Arrange
    var strategy = new NamespaceRoutingStrategy(type => "custom-topic");

    // Act
    var result = strategy.ResolveTopic(typeof(TestEvent), "ignored");

    // Assert
    await Assert.That(result).IsEqualTo("custom-topic");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_MessageNamespace_ReturnsFullNamespaceAsync() {
    // Arrange - Messages namespace
    var strategy = new NamespaceRoutingStrategy();

    // Act
    var result = strategy.ResolveTopic(typeof(TestNamespaces.MyApp.Contracts.Messages.CreateOrderCommand), "ignored");

    // Assert - Returns full namespace in lowercase
    await Assert.That(result).IsEqualTo("testnamespaces.myapp.contracts.messages");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_SameNamespaceForDifferentTypes_ReturnsSameNamespaceAsync() {
    // Arrange
    var strategy = new NamespaceRoutingStrategy();

    // Act - Both types are in the same namespace
    var result1 = strategy.ResolveTopic(typeof(TestNamespaces.MyApp.Contracts.Messages.CreateOrderCommand), "ignored");
    var result2 = strategy.ResolveTopic(typeof(TestNamespaces.MyApp.Contracts.Messages.OrderCreatedEvent), "ignored");

    // Assert - Both should return the same namespace
    await Assert.That(result1).IsEqualTo(result2);
    await Assert.That(result1).IsEqualTo("testnamespaces.myapp.contracts.messages");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_IntegrationWithComposite_WorksCorrectlyAsync() {
    // Arrange
    var namespaceStrategy = new NamespaceRoutingStrategy();
    var poolStrategy = new PoolSuffixRoutingStrategy("01");
    var composite = new CompositeTopicRoutingStrategy(namespaceStrategy, poolStrategy);

    // Act - Full namespace with pool suffix
    var result = composite.ResolveTopic(typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated), "base");

    // Assert
    await Assert.That(result).IsEqualTo("testnamespaces.myapp.orders.events-01");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_WithValidNamespace_ReturnsNamespaceAsync() {
    // Arrange
    var strategy = new NamespaceRoutingStrategy();

    // Act
    var result = strategy.ResolveTopic(typeof(TestEvent), "fallback");

    // Assert - TestEvent with namespace "Whizbang.Core.Tests.Routing" should return it
    await Assert.That(result).IsEqualTo("whizbang.core.tests.routing");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_QueriesNamespace_ReturnsFullNamespaceAsync() {
    // Arrange - MyApp.Contracts.Queries.GetOrderById → full namespace
    var strategy = new NamespaceRoutingStrategy();

    // Act
    var result = strategy.ResolveTopic(typeof(TestNamespaces.MyApp.Contracts.Queries.GetOrderById), "ignored");

    // Assert - Returns full namespace in lowercase
    await Assert.That(result).IsEqualTo("testnamespaces.myapp.contracts.queries");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_ReturnsLowercaseNamespaceAsync() {
    // Arrange
    var strategy = new NamespaceRoutingStrategy();

    // Act
    var result = strategy.ResolveTopic(typeof(NamespaceRoutingTestTypes.OrderCreated), "ignored");

    // Assert - Should be lowercase
    await Assert.That(result).IsEqualTo("namespaceroutingtesttypes");
    await Assert.That(result).IsEqualTo(result.ToLowerInvariant());
  }
}
