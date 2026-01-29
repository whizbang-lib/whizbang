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
  public async Task SharedTopicInboxStrategy_GetSubscription_ReturnsDefaultTopicAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders", "inventory" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert
    await Assert.That(subscription.Topic).IsEqualTo("whizbang.inbox");
  }

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_WithCustomTopic_ReturnsCustomTopicAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy("my.custom.inbox");
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert
    await Assert.That(subscription.Topic).IsEqualTo("my.custom.inbox");
  }

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_ReturnsFilterExpressionAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders", "inventory" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - Filter should list owned domains
    await Assert.That(subscription.FilterExpression).IsNotNull();
    await Assert.That(subscription.FilterExpression).Contains("orders");
    await Assert.That(subscription.FilterExpression).Contains("inventory");
  }

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_SingleDomain_ReturnsRoutingPatternAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - Metadata should contain routing pattern
    await Assert.That(subscription.Metadata is not null).IsTrue();
    await Assert.That(subscription.Metadata!["RoutingPattern"]).IsEqualTo("orders.#");
  }

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_MultipleDomains_ReturnsCatchAllPatternAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders", "inventory" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - With multiple domains, use catch-all pattern and rely on filtering
    await Assert.That(subscription.Metadata is not null).IsTrue();
    await Assert.That(subscription.Metadata!["RoutingPattern"]).IsEqualTo("#");
  }

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscription_IncludesDestinationFilterMetadataAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders", "inventory" };

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert - Metadata should include DestinationFilter for ASB CorrelationFilter
    await Assert.That(subscription.Metadata is not null).IsTrue();
    await Assert.That(subscription.Metadata!.ContainsKey("DestinationFilter")).IsTrue();
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
  public async Task SharedTopicInboxStrategy_GetSubscription_EmptyDomains_ReturnsEmptyFilterAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Act
    var subscription = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    // Assert
    await Assert.That(subscription.FilterExpression).IsEmpty();
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
