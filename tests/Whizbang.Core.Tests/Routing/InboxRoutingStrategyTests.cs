using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for IInboxRoutingStrategy implementations.
/// Inbox strategies determine where a service receives commands.
/// </summary>
public class InboxRoutingStrategyTests {
  #region InboxSubscription Record

  [Test]
  public async Task InboxSubscription_WithTopicOnly_CreatesValidRecordAsync() {
    // Arrange & Act
    var subscription = new InboxSubscription("whizbang.inbox");

    // Assert
    await Assert.That(subscription.Topic).IsEqualTo("whizbang.inbox");
    await Assert.That(subscription.FilterExpression).IsNull();
    await Assert.That(subscription.Metadata is null).IsTrue();
  }

  [Test]
  public async Task InboxSubscription_WithFilter_CreatesValidRecordAsync() {
    // Arrange & Act
    var subscription = new InboxSubscription("whizbang.inbox", "orders,inventory");

    // Assert
    await Assert.That(subscription.Topic).IsEqualTo("whizbang.inbox");
    await Assert.That(subscription.FilterExpression).IsEqualTo("orders,inventory");
  }

  [Test]
  public async Task InboxSubscription_WithMetadata_CreatesValidRecordAsync() {
    // Arrange
    var metadata = new Dictionary<string, object> {
      ["DestinationFilter"] = "orders,inventory",
      ["RoutingPattern"] = "orders.#"
    };

    // Act
    var subscription = new InboxSubscription("whizbang.inbox", "orders,inventory", metadata);

    // Assert
    await Assert.That(subscription.Metadata is not null).IsTrue();
    await Assert.That(subscription.Metadata!["DestinationFilter"]).IsEqualTo("orders,inventory");
    await Assert.That(subscription.Metadata["RoutingPattern"]).IsEqualTo("orders.#");
  }

  #endregion

  #region SharedTopicInboxStrategy

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_ReturnsDefaultInboxTopicAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.commands", "myapp.inventory.commands" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - Default inbox topic is "inbox"
    await Assert.That(subscription.Topic).IsEqualTo("inbox");
  }

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_WithCustomTopic_ReturnsCustomTopicAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy("my.custom.inbox");
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.commands" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert
    await Assert.That(subscription.Topic).IsEqualTo("my.custom.inbox");
  }

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_IncludesSystemCommandsInFilterAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.commands" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - Filter should include system commands namespace
    await Assert.That(subscription.FilterExpression).IsNotNull();
    await Assert.That(subscription.FilterExpression).Contains("whizbang.core.commands.system.#");
  }

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_IncludesOwnedNamespacesInFilterAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.commands", "myapp.inventory.commands" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - Filter should include owned command namespaces
    await Assert.That(subscription.FilterExpression).IsNotNull();
    await Assert.That(subscription.FilterExpression).Contains("myapp.orders.commands.#");
    await Assert.That(subscription.FilterExpression).Contains("myapp.inventory.commands.#");
  }

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_ReturnsRoutingPatternsMetadataAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.commands" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - Metadata should contain routing patterns list
    await Assert.That(subscription.Metadata is not null).IsTrue();
    await Assert.That(subscription.Metadata!.ContainsKey("RoutingPatterns")).IsTrue();
  }

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_RoutingPatternsIncludesSystemAndOwnedAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.commands" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - Routing patterns should include both system commands and owned namespaces
    await Assert.That(subscription.Metadata is not null).IsTrue();
    var routingPatterns = (List<string>)subscription.Metadata!["RoutingPatterns"];
    await Assert.That(routingPatterns).Contains("whizbang.core.commands.system.#");
    await Assert.That(routingPatterns).Contains("myapp.orders.commands.#");
  }

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_WildcardNamespace_ConvertsToHashPatternAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.*" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - Wildcard ".*" should be converted to ".#"
    await Assert.That(subscription.FilterExpression).IsNotNull();
    await Assert.That(subscription.FilterExpression).Contains("myapp.orders.#");
  }

  [Test]
  public async Task SharedTopicInboxStrategy_SystemCommandNamespace_ReturnsCorrectValueAsync() {
    // Assert - Static property returns system command namespace
    await Assert.That(SharedTopicInboxStrategy.SystemCommandNamespace).IsEqualTo("whizbang.core.commands.system");
  }

  #endregion

  #region DomainTopicInboxStrategy

  [Test]
  public async Task DomainTopicInboxStrategy_GetSubscription_ReturnsDomainSpecificTopicAsync() {
    // Arrange
    var strategy = new DomainTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - Topic should be domain + suffix
    await Assert.That(subscription.Topic).IsEqualTo("orders.inbox");
  }

  [Test]
  public async Task DomainTopicInboxStrategy_GetSubscription_WithCustomSuffix_ReturnsCustomTopicAsync() {
    // Arrange
    var strategy = new DomainTopicInboxStrategy(".in");
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert
    await Assert.That(subscription.Topic).IsEqualTo("orders.in");
  }

  [Test]
  public async Task DomainTopicInboxStrategy_GetSubscription_NoFilterExpression_WhenDomainSpecificAsync() {
    // Arrange
    var strategy = new DomainTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - No filter needed when topic IS the filter
    await Assert.That(subscription.FilterExpression).IsNull();
  }

  [Test]
  public async Task DomainTopicInboxStrategy_GetSubscription_MultipleDomains_UsesFirstDomainAsync() {
    // Arrange
    var strategy = new DomainTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders", "inventory" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - Returns primary domain (first in set)
    // Note: Caller should handle multiple subscriptions for multiple domains
    await Assert.That(subscription.Topic).Contains(".inbox");
  }

  [Test]
  public async Task DomainTopicInboxStrategy_GetSubscription_EmptyDomains_FallsBackToServiceNameAsync() {
    // Arrange
    var strategy = new DomainTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - Falls back to service name when no domains specified
    await Assert.That(subscription.Topic).IsEqualTo("OrderService.inbox");
  }

  #endregion

  #region Edge Cases

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_EmptyDomains_StillIncludesSystemCommandsAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - All services receive system commands even with no owned domains
    await Assert.That(subscription.FilterExpression).IsNotEmpty();
    await Assert.That(subscription.FilterExpression).Contains("whizbang.core.commands.system.#");
  }

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_NullDomains_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();

    // Act & Assert
    await Assert.That(() => strategy.GetSubscription(null!, "OrderService", MessageKind.Command))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task DomainTopicInboxStrategy_GetSubscription_NullDomains_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var strategy = new DomainTopicInboxStrategy();

    // Act & Assert
    await Assert.That(() => strategy.GetSubscription(null!, "OrderService", MessageKind.Command))
      .Throws<ArgumentNullException>();
  }

  #endregion
}
