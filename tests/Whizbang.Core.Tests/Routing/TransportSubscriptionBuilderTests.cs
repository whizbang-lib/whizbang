using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for TransportSubscriptionBuilder.
/// Verifies that the builder correctly combines inbox and event subscriptions.
/// </summary>
public class TransportSubscriptionBuilderTests {
  #region BuildDestinations

  [Test]
  public async Task BuildDestinations_WithInboxAndEvents_ReturnsAllDestinationsAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("myapp.orders.commands");
    routingOptions.SubscribeTo("myapp.payments.events");

    var registry = new TestEventNamespaceRegistry(["myapp.users.events", "myapp.orders.events"]);
    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), registry);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    // Act
    var destinations = builder.BuildDestinations();

    // Assert - Should have inbox + 3 event namespaces (users, orders, payments)
    await Assert.That(destinations.Count).IsGreaterThanOrEqualTo(4);
  }

  [Test]
  public async Task BuildDestinations_WithNoEvents_ReturnsOnlyInboxAsync() {
    // Arrange - use empty registry to isolate from static EventNamespaceRegistry
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("myapp.orders.commands");

    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), TestEventNamespaceRegistry.Empty);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    // Act
    var destinations = builder.BuildDestinations();

    // Assert - Should have only inbox
    await Assert.That(destinations.Count).IsEqualTo(1);
    await Assert.That(destinations[0].Address).IsEqualTo("inbox");
  }

  #endregion

  #region BuildInboxDestination

  [Test]
  public async Task BuildInboxDestination_WithSharedTopicStrategy_ReturnsInboxTopicAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("myapp.orders.commands");
    routingOptions.Inbox.UseSharedTopic("commands.inbox");

    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), null);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    // Act
    var destination = builder.BuildInboxDestination();

    // Assert
    await Assert.That(destination).IsNotNull();
    await Assert.That(destination!.Address).IsEqualTo("commands.inbox");
  }

  [Test]
  public async Task BuildInboxDestination_WithDomainTopicStrategy_ReturnsDomainInboxTopicAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("orders");
    routingOptions.Inbox.UseDomainTopics(".in");

    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), null);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    // Act
    var destination = builder.BuildInboxDestination();

    // Assert
    await Assert.That(destination).IsNotNull();
    await Assert.That(destination!.Address).IsEqualTo("orders.in");
  }

  [Test]
  public async Task BuildInboxDestination_IncludesRoutingKeyFilterAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("myapp.orders.commands");

    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), null);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    // Act
    var destination = builder.BuildInboxDestination();

    // Assert - Should have routing key with filter patterns
    await Assert.That(destination).IsNotNull();
    await Assert.That(destination!.RoutingKey).IsNotNull();
    await Assert.That(destination!.RoutingKey).Contains("myapp.orders.commands.#");
    await Assert.That(destination!.RoutingKey).Contains("whizbang.core.commands.system.#");
  }

  #endregion

  #region BuildEventDestinations

  [Test]
  public async Task BuildEventDestinations_WithAutoDiscoveredNamespaces_ReturnsAllAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    var registry = new TestEventNamespaceRegistry(["myapp.users.events", "myapp.orders.events"]);
    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), registry);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    // Act
    var destinations = builder.BuildEventDestinations();

    // Assert
    await Assert.That(destinations.Count).IsEqualTo(2);
    await Assert.That(destinations.Select(d => d.Address)).Contains("myapp.users.events");
    await Assert.That(destinations.Select(d => d.Address)).Contains("myapp.orders.events");
  }

  [Test]
  public async Task BuildEventDestinations_WithManualSubscriptions_IncludesThemAsync() {
    // Arrange - use empty registry to isolate from static EventNamespaceRegistry
    var routingOptions = new RoutingOptions();
    routingOptions.SubscribeTo("myapp.payments.events");

    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), TestEventNamespaceRegistry.Empty);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    // Act
    var destinations = builder.BuildEventDestinations();

    // Assert
    await Assert.That(destinations.Count).IsEqualTo(1);
    await Assert.That(destinations[0].Address).IsEqualTo("myapp.payments.events");
  }

  [Test]
  public async Task BuildEventDestinations_CombinesAutoAndManual_DeduplicatesAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    routingOptions.SubscribeTo("myapp.orders.events"); // Also auto-discovered

    var registry = new TestEventNamespaceRegistry(["myapp.orders.events"]);
    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), registry);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    // Act
    var destinations = builder.BuildEventDestinations();

    // Assert - Should deduplicate
    await Assert.That(destinations.Count).IsEqualTo(1);
    await Assert.That(destinations[0].Address).IsEqualTo("myapp.orders.events");
  }

  [Test]
  public async Task BuildEventDestinations_AllHaveCatchAllRoutingKeyAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    var registry = new TestEventNamespaceRegistry(["myapp.users.events"]);
    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), registry);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    // Act
    var destinations = builder.BuildEventDestinations();

    // Assert - All event subscriptions use "#" to receive all events in namespace
    foreach (var dest in destinations) {
      await Assert.That(dest.RoutingKey).IsEqualTo("#");
    }
  }

  #endregion

  #region ConfigureOptions

  [Test]
  public async Task ConfigureOptions_AddsAllDestinationsAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("myapp.orders.commands");
    routingOptions.SubscribeTo("myapp.payments.events");

    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), null);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    var options = new TransportConsumerOptions();

    // Act
    builder.ConfigureOptions(options);

    // Assert
    await Assert.That(options.Destinations.Count).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  public async Task ConfigureOptions_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), null);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    // Act & Assert
    await Assert.That(() => builder.ConfigureOptions(null!))
      .Throws<ArgumentNullException>();
  }

  #endregion

  #region Constructor Validation

  [Test]
  public async Task Constructor_WithNullRoutingOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), null);

    // Act & Assert
    await Assert.That(() => new TransportSubscriptionBuilder(null!, discovery, "OrderService"))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullDiscovery_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();

    // Act & Assert
    await Assert.That(() => new TransportSubscriptionBuilder(Options.Create(routingOptions), null!, "OrderService"))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullServiceName_ThrowsArgumentExceptionAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), null);

    // Act & Assert
    await Assert.That(() => new TransportSubscriptionBuilder(Options.Create(routingOptions), discovery, null!))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Constructor_WithEmptyServiceName_ThrowsArgumentExceptionAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), null);

    // Act & Assert
    await Assert.That(() => new TransportSubscriptionBuilder(Options.Create(routingOptions), discovery, ""))
      .Throws<ArgumentException>();
  }

  #endregion

  #region Test Helpers

  private sealed class TestEventNamespaceRegistry(IEnumerable<string> namespaces) : IEventNamespaceRegistry {
    private readonly HashSet<string> _namespaces = new(namespaces, StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an empty registry (no auto-discovered namespaces).</summary>
    public static TestEventNamespaceRegistry Empty => new([]);

    public IReadOnlySet<string> GetPerspectiveEventNamespaces() => _namespaces;
    public IReadOnlySet<string> GetReceptorEventNamespaces() => new HashSet<string>();
    public IReadOnlySet<string> GetAllEventNamespaces() => _namespaces;
  }

  #endregion
}
