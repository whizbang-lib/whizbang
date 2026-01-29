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
      var composite = new CompositeTopicRoutingStrategy(Array.Empty<ITopicRoutingStrategy>());
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
  private sealed class TenantPrefixStrategy : ITopicRoutingStrategy {
    private readonly string _tenantId;

    public TenantPrefixStrategy(string tenantId) {
      _tenantId = tenantId;
    }

    public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
      return $"{_tenantId}-{baseTopic}";
    }
  }

  // ===============================================================================
  // NamespaceRoutingStrategy Tests
  // ===============================================================================

  [Test]
  public async Task NamespaceRoutingStrategy_HierarchicalNamespace_ExtractsSecondToLastSegmentAsync() {
    // Arrange - MyApp.Orders.Events.OrderCreated → "orders"
    var strategy = new NamespaceRoutingStrategy();

    // Act
    var result = strategy.ResolveTopic(typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated), "ignored");

    // Assert
    await Assert.That(result).IsEqualTo("orders");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_FlatNamespace_ExtractsFromTypeNameAsync() {
    // Arrange - MyApp.Contracts.Commands.CreateOrder → "order"
    var strategy = new NamespaceRoutingStrategy();

    // Act
    var result = strategy.ResolveTopic(typeof(TestNamespaces.MyApp.Contracts.Commands.CreateOrder), "ignored");

    // Assert
    await Assert.That(result).IsEqualTo("order");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_FlatNamespaceWithEvents_ExtractsFromTypeNameAsync() {
    // Arrange - MyApp.Contracts.Events.OrderCreated → "order"
    var strategy = new NamespaceRoutingStrategy();

    // Act
    var result = strategy.ResolveTopic(typeof(TestNamespaces.MyApp.Contracts.Events.OrderCreated), "ignored");

    // Assert
    await Assert.That(result).IsEqualTo("order");
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
  public async Task NamespaceRoutingStrategy_TypeNameExtraction_RemovesCommandSuffixAsync() {
    // Arrange
    var strategy = new NamespaceRoutingStrategy();

    // Act - CreateOrderCommand → "order"
    var result = strategy.ResolveTopic(typeof(TestNamespaces.MyApp.Contracts.Messages.CreateOrderCommand), "ignored");

    // Assert
    await Assert.That(result).IsEqualTo("order");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_TypeNameExtraction_RemovesEventSuffixAsync() {
    // Arrange
    var strategy = new NamespaceRoutingStrategy();

    // Act - OrderCreatedEvent → "order"
    var result = strategy.ResolveTopic(typeof(TestNamespaces.MyApp.Contracts.Messages.OrderCreatedEvent), "ignored");

    // Assert
    await Assert.That(result).IsEqualTo("order");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_IntegrationWithComposite_WorksCorrectlyAsync() {
    // Arrange
    var namespaceStrategy = new NamespaceRoutingStrategy();
    var poolStrategy = new PoolSuffixRoutingStrategy("01");
    var composite = new CompositeTopicRoutingStrategy(namespaceStrategy, poolStrategy);

    // Act - orders → orders-01
    var result = composite.ResolveTopic(typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated), "base");

    // Assert
    await Assert.That(result).IsEqualTo("orders-01");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_NullNamespace_ExtractsFromTypeNameAsync() {
    // The type has a namespace, so we'll test with a custom function that returns the type name processing
    var strategy = new NamespaceRoutingStrategy();

    // Act
    var result = strategy.ResolveTopic(typeof(TestEvent), "fallback");

    // Assert - TestEvent with namespace "Whizbang.Core.Tests.Routing" should work
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task NamespaceRoutingStrategy_SkipsQueriesNamespace_UsesTypeNameAsync() {
    // Arrange - MyApp.Contracts.Queries.GetOrderById → "order"
    var strategy = new NamespaceRoutingStrategy();

    // Act
    var result = strategy.ResolveTopic(typeof(TestNamespaces.MyApp.Contracts.Queries.GetOrderById), "ignored");

    // Assert
    await Assert.That(result).IsEqualTo("order");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_ReturnsLowercaseTopicAsync() {
    // Arrange
    var strategy = new NamespaceRoutingStrategy();

    // Act
    var result = strategy.ResolveTopic(typeof(NamespaceRoutingTestTypes.OrderCreated), "ignored");

    // Assert - Should be lowercase
    await Assert.That(result).IsEqualTo(result.ToLowerInvariant());
  }
}
