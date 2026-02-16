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
  public async Task DomainTopicOutboxStrategy_GetDestination_ReturnsFullNamespaceAsTopicAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act - Event in OutboxTestTypes.Orders.Events namespace
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Topic is full namespace in lowercase
    await Assert.That(destination.Address).IsEqualTo("outboxtesttypes.orders.events");
  }

  [Test]
  public async Task DomainTopicOutboxStrategy_GetDestination_SetsRoutingKeyFromTypeNameAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

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
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

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
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act & Assert
    await Assert.That(() => strategy.GetDestination(
      null!,
      ownedDomains,
      MessageKind.Event
    )).Throws<ArgumentNullException>();
  }

  #endregion

  #region SharedTopicOutboxStrategy - Command Routing

  [Test]
  public async Task SharedTopicOutboxStrategy_Command_RoutesToInboxTopicAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.commands" };

    // Act - Command goes to shared inbox
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Command
    );

    // Assert - Commands go to shared inbox topic
    await Assert.That(destination.Address).IsEqualTo("inbox");
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_Command_SetsNamespaceBasedRoutingKeyAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.commands" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Command
    );

    // Assert - Routing key is namespace.typename for command filtering
    await Assert.That(destination.RoutingKey).IsEqualTo("outboxtesttypes.orders.events.ordercreated");
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_Command_WithCustomInboxTopic_UsesCustomTopicAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy("my-inbox");
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.commands" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Command
    );

    // Assert
    await Assert.That(destination.Address).IsEqualTo("my-inbox");
  }

  #endregion

  #region SharedTopicOutboxStrategy - Event Routing

  [Test]
  public async Task SharedTopicOutboxStrategy_Event_RoutesToNamespaceTopicAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act - Event goes to namespace topic
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Events go to namespace-specific topic
    await Assert.That(destination.Address).IsEqualTo("outboxtesttypes.orders.events");
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_Event_SetsTypeNameRoutingKeyAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Routing key is just the type name for events
    await Assert.That(destination.RoutingKey).IsEqualTo("ordercreated");
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_Event_IncludesNamespaceInMetadataAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Metadata includes namespace for filtering
    await Assert.That(destination.Metadata is not null).IsTrue();
    await Assert.That(destination.Metadata!.ContainsKey("Namespace")).IsTrue();
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_Event_IncludesKindInMetadataAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Metadata includes kind
    await Assert.That(destination.Metadata is not null).IsTrue();
    await Assert.That(destination.Metadata!.ContainsKey("Kind")).IsTrue();
  }

  #endregion

  #region SharedTopicOutboxStrategy - Validation

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

  [Test]
  public async Task SharedTopicOutboxStrategy_GetDestination_NullMessageType_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act & Assert
    await Assert.That(() => strategy.GetDestination(
      null!,
      ownedDomains,
      MessageKind.Event
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_DefaultInboxTopic_ReturnsInboxAsync() {
    // Assert - Static property returns default inbox topic
    await Assert.That(SharedTopicOutboxStrategy.DefaultInboxTopic).IsEqualTo("inbox");
  }

  #endregion

  #region Integration with NamespaceRoutingStrategy

  [Test]
  public async Task DomainTopicOutboxStrategy_ContractsNamespace_ReturnsFullNamespaceAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.contracts.events" };

    // Act - Type in flat namespace (Contracts.Events)
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Contracts.Events.ProductCreatedEvent),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Returns full namespace as topic
    await Assert.That(destination.Address).IsEqualTo("outboxtesttypes.contracts.events");
  }

  #endregion
}
