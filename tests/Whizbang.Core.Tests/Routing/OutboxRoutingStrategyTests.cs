using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for IOutboxRoutingStrategy implementations.
/// Outbox strategies determine where a service publishes events.
/// </summary>
public class OutboxRoutingStrategyTests {
  #region DomainTopicOutboxStrategy

  [Test]
  public async Task DomainTopicOutboxStrategy_GetDestination_ExtractsDomainFromNamespaceAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act - Event in MyApp.Orders.Events namespace
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Domain extracted from namespace
    await Assert.That(destination.Address).IsEqualTo("orders");
  }

  [Test]
  public async Task DomainTopicOutboxStrategy_GetDestination_SetsRoutingKeyFromTypeNameAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Routing key is lowercase type name
    await Assert.That(destination.RoutingKey).IsEqualTo("ordercreated");
  }

  [Test]
  public async Task DomainTopicOutboxStrategy_GetDestination_WithCustomTopicResolver_UsesResolverAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy(new NamespaceRoutingStrategy(
      type => "custom-domain"
    ));
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert
    await Assert.That(destination.Address).IsEqualTo("custom-domain");
  }

  [Test]
  public async Task DomainTopicOutboxStrategy_GetDestination_NullOwnedDomains_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy();

    // Act & Assert
    await Assert.That(() => strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      null!,
      MessageKind.Event
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task DomainTopicOutboxStrategy_GetDestination_NullMessageType_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act & Assert
    await Assert.That(() => strategy.GetDestination(
      null!,
      ownedDomains,
      MessageKind.Event
    )).Throws<ArgumentNullException>();
  }

  #endregion

  #region SharedTopicOutboxStrategy

  [Test]
  public async Task SharedTopicOutboxStrategy_GetDestination_ReturnsDefaultTopicAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - All events go to shared topic
    await Assert.That(destination.Address).IsEqualTo("whizbang.events");
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_GetDestination_WithCustomTopic_ReturnsCustomTopicAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy("my.events");
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert
    await Assert.That(destination.Address).IsEqualTo("my.events");
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_GetDestination_SetsCompoundRoutingKeyAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Routing key is domain.typename
    await Assert.That(destination.RoutingKey).IsEqualTo("orders.ordercreated");
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_GetDestination_IncludesDomainInMetadataAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Metadata includes domain for filtering
    await Assert.That(destination.Metadata is not null).IsTrue();
    await Assert.That(destination.Metadata!.ContainsKey("Domain")).IsTrue();
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_GetDestination_NullOwnedDomains_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();

    // Act & Assert
    await Assert.That(() => strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      null!,
      MessageKind.Event
    )).Throws<ArgumentNullException>();
  }

  #endregion

  #region Integration with NamespaceRoutingStrategy

  [Test]
  public async Task DomainTopicOutboxStrategy_WithFlatNamespace_ExtractsDomainFromTypeNameAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act - Type in flat namespace (Contracts.Events)
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Contracts.Events.ProductCreatedEvent),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Domain extracted from type name since namespace is generic
    await Assert.That(destination.Address).IsEqualTo("product");
  }

  #endregion
}
