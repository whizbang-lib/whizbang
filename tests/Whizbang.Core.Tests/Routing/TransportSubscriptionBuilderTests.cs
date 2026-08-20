using Microsoft.Extensions.DependencyInjection;
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

  #region Plural seam + DI strategy resolution (topology arc phase 3)

  [Test]
  public async Task BuildInboxDestinations_SharedTopicStrategy_BitIdenticalToSingularPathAsync() {
    // Zero-behavior-change lock: the plural-driven destination list must be EXACTLY the
    // one destination the singular path produced (address, routing key, metadata keys).
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("myapp.orders.commands");

    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), TestEventNamespaceRegistry.Empty);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    // Expected: hand-built from the strategy's singular answer (today's behavior)
    var singular = routingOptions.InboxStrategy!.GetSubscription(
        routingOptions.OwnedDomains, "OrderService", MessageKind.Command);

    var destinations = builder.BuildInboxDestinations();

    await Assert.That(destinations.Count).IsEqualTo(1);
    await Assert.That(destinations[0].Address).IsEqualTo(singular.Topic);
    await Assert.That(destinations[0].RoutingKey).IsEqualTo(singular.FilterExpression);
    await Assert.That(destinations[0].Metadata!.ContainsKey("SubscriberName")).IsTrue();
    await Assert.That(destinations[0].Metadata!["SubscriberName"].GetString()).IsEqualTo("OrderService");
    await Assert.That(destinations[0].Metadata!.ContainsKey("RoutingPatterns")).IsTrue();

    // And the legacy singular builder API returns the same destination (field-wise:
    // TransportDestination record equality compares Metadata by reference, and each
    // build allocates a fresh metadata dictionary)
    var single = builder.BuildInboxDestination();
    await Assert.That(single!.Address).IsEqualTo(destinations[0].Address);
    await Assert.That(single.RoutingKey).IsEqualTo(destinations[0].RoutingKey);
    await Assert.That(single.Metadata!.Keys.OrderBy(k => k, StringComparer.Ordinal)
        .SequenceEqual(destinations[0].Metadata!.Keys.OrderBy(k => k, StringComparer.Ordinal))).IsTrue();
  }

  [Test]
  public async Task BuildInboxDestinations_DomainTopicStrategy_BitIdenticalToSingularPathAsync() {
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("orders");
    routingOptions.Inbox.UseDomainTopics(".in");

    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), TestEventNamespaceRegistry.Empty);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    var singular = routingOptions.InboxStrategy!.GetSubscription(
        routingOptions.OwnedDomains, "OrderService", MessageKind.Command);

    var destinations = builder.BuildInboxDestinations();

    await Assert.That(destinations.Count).IsEqualTo(1);
    await Assert.That(destinations[0].Address).IsEqualTo(singular.Topic);
    await Assert.That(destinations[0].Address).IsEqualTo("orders.in");
    await Assert.That(destinations[0].RoutingKey).IsNull();
    await Assert.That(destinations[0].Metadata!["SubscriberName"].GetString()).IsEqualTo("OrderService");

    var single = builder.BuildInboxDestination();
    await Assert.That(single!.Address).IsEqualTo(destinations[0].Address);
    await Assert.That(single.RoutingKey).IsEqualTo(destinations[0].RoutingKey);
    await Assert.That(single.Metadata!["SubscriberName"].GetString()).IsEqualTo("OrderService");
  }

  [Test]
  public async Task BuildInboxDestination_DiRegisteredStrategy_WinsOverOptionsAsync() {
    // The builder must prefer the DI-resolved IInboxRoutingStrategy over the one hanging
    // off RoutingOptions (plan-flagged fix: options was read directly, bypassing DI).
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("myapp.orders.commands");

    var diStrategy = new FixedTopicStrategy("di-resolved-inbox");
    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), TestEventNamespaceRegistry.Empty);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService",
        inboxStrategy: diStrategy);

    var destination = builder.BuildInboxDestination();

    await Assert.That(destination).IsNotNull();
    await Assert.That(destination!.Address).IsEqualTo("di-resolved-inbox");
  }

  [Test]
  public async Task BuildInboxDestination_NoDiStrategy_FallsBackToOptionsAsync() {
    // Options fallback path: with no DI-resolved strategy the options strategy is used.
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("myapp.orders.commands");
    routingOptions.Inbox.UseSharedTopic("options-inbox");

    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), TestEventNamespaceRegistry.Empty);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService",
        inboxStrategy: null);

    var destination = builder.BuildInboxDestination();

    await Assert.That(destination).IsNotNull();
    await Assert.That(destination!.Address).IsEqualTo("options-inbox");
  }

  [Test]
  public async Task BuildInboxDestinations_RegistryResolvable_PassesHandledMessagesToContextAsync() {
    // When an IReceptorRegistryQuery is resolvable, its GetHandledMessages() feeds the
    // subscription context so kind-aware strategies (phase 5) can enumerate the surface.
    var routingOptions = new RoutingOptions();
    var capturing = new ContextCapturingStrategy();
    routingOptions.Inbox.UseCustom(capturing);

    var handled = new List<Whizbang.Core.Messaging.HandledMessageInfo> {
      new("MyApp.Orders.Commands.CreateOrder", "myapp.orders.commands", MessageKind.Command)
    };
    var registryQuery = new FixedRegistryQuery(handled);

    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), TestEventNamespaceRegistry.Empty);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService",
        receptorRegistry: registryQuery);

    _ = builder.BuildInboxDestinations();

    await Assert.That(capturing.LastContext).IsNotNull();
    await Assert.That(capturing.LastContext!.ServiceName).IsEqualTo("OrderService");
    await Assert.That(capturing.LastContext!.HandledMessages.Count).IsEqualTo(1);
    await Assert.That(capturing.LastContext!.HandledMessages[0].MessageTypeName)
      .IsEqualTo("MyApp.Orders.Commands.CreateOrder");
  }

  [Test]
  public async Task AddTransportSubscriptionBuilder_MakesTopologyManifestResolvableAsync() {
    // Phase 5 wiring: registering the subscription builder TryAdds a TopologyManifest factory
    // (strategies + registry + catalog at first resolve) so TransportConsumerWorker can run
    // manifest-driven DARK provisioning without any consumer opt-in.
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("myapp.orders.commands");
    var services = new ServiceCollection();
    services.AddSingleton(Options.Create(routingOptions));
    services.AddSingleton(new EventSubscriptionDiscovery(
        Options.Create(routingOptions), TestEventNamespaceRegistry.Empty));
    services.AddTransportSubscriptionBuilder("OrderService");
    using var provider = services.BuildServiceProvider();

    var manifest = provider.GetService<TopologyManifest>();

    await Assert.That(manifest).IsNotNull();
    await Assert.That(manifest!.ServiceName).IsEqualTo("OrderService");
    await Assert.That(manifest.Subscriptions.Count).IsEqualTo(1)
      .Because("the default shared strategy names exactly one subscription — zero new entities");
    await Assert.That(manifest.Subscriptions[0].Topic).IsEqualTo("inbox");
  }

  [Test]
  public async Task BuildInboxDestinations_ContextCarriesDiscoveredConsumedEventNamespacesAsync() {
    // Phase 5: the subscription context's ConsumedEventNamespaces must be fed from
    // EventSubscriptionDiscovery (perspective-consumed + manual namespaces) — the
    // composite/raw-carry surface reuses the existing discovery, not a new enumeration.
    var routingOptions = new RoutingOptions();
    routingOptions.SubscribeTo("myapp.manual.events");
    var capturing = new ContextCapturingStrategy();
    routingOptions.Inbox.UseCustom(capturing);

    var discovery = new EventSubscriptionDiscovery(
        Options.Create(routingOptions),
        new TestEventNamespaceRegistry(["myapp.payments.events"]));
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    _ = builder.BuildInboxDestinations();

    await Assert.That(capturing.LastContext).IsNotNull();
    await Assert.That(capturing.LastContext!.ConsumedEventNamespaces).Contains("myapp.payments.events")
      .Because("perspective-consumed event namespaces come from the generated registry via discovery");
    await Assert.That(capturing.LastContext!.ConsumedEventNamespaces).Contains("myapp.manual.events")
      .Because("manual SubscribeTo namespaces are consumed-constituent sources too");
  }

  [Test]
  public async Task BuildInboxDestinations_NoRegistry_ContextHandledMessagesEmptyAsync() {
    var routingOptions = new RoutingOptions();
    var capturing = new ContextCapturingStrategy();
    routingOptions.Inbox.UseCustom(capturing);

    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), TestEventNamespaceRegistry.Empty);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    _ = builder.BuildInboxDestinations();

    await Assert.That(capturing.LastContext).IsNotNull();
    await Assert.That(capturing.LastContext!.HandledMessages.Count).IsEqualTo(0);
  }

  [Test]
  public async Task BuildInboxDestinations_MultiSubscriptionStrategy_BuildsOneDestinationPerSubscriptionAsync() {
    // The widened seam: a strategy returning N subscriptions produces N destinations,
    // each with the SubscriberName metadata required for deterministic queue naming.
    var routingOptions = new RoutingOptions();
    routingOptions.Inbox.UseCustom(new MultiSubscriptionStrategy());

    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), TestEventNamespaceRegistry.Empty);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    var destinations = builder.BuildInboxDestinations();

    await Assert.That(destinations.Count).IsEqualTo(2);
    await Assert.That(destinations[0].Address).IsEqualTo("inbox.first");
    await Assert.That(destinations[1].Address).IsEqualTo("inbox.second");
    foreach (var destination in destinations) {
      await Assert.That(destination.Metadata!["SubscriberName"].GetString()).IsEqualTo("OrderService");
    }

    // BuildDestinations includes every inbox destination too
    var all = builder.BuildDestinations();
    await Assert.That(all.Select(d => d.Address)).Contains("inbox.first");
    await Assert.That(all.Select(d => d.Address)).Contains("inbox.second");
  }

  private sealed class FixedTopicStrategy(string topic) : IInboxRoutingStrategy {
    public InboxSubscription GetSubscription(
        IReadOnlySet<string> ownedDomains, string serviceName, MessageKind kind)
      => new(topic);
  }

  private sealed class ContextCapturingStrategy : IInboxRoutingStrategy {
    public InboxSubscriptionContext? LastContext { get; private set; }

    public InboxSubscription GetSubscription(
        IReadOnlySet<string> ownedDomains, string serviceName, MessageKind kind)
      => new("captured-inbox");

    public IReadOnlyList<InboxSubscription> GetSubscriptions(InboxSubscriptionContext context) {
      LastContext = context;
      return [new InboxSubscription("captured-inbox")];
    }
  }

  private sealed class MultiSubscriptionStrategy : IInboxRoutingStrategy {
    public InboxSubscription GetSubscription(
        IReadOnlySet<string> ownedDomains, string serviceName, MessageKind kind)
      => new("inbox.first");

    public IReadOnlyList<InboxSubscription> GetSubscriptions(InboxSubscriptionContext context)
      => [new InboxSubscription("inbox.first"), new InboxSubscription("inbox.second")];
  }

  private sealed class FixedRegistryQuery(
      IReadOnlyList<Whizbang.Core.Messaging.HandledMessageInfo> handled)
      : Whizbang.Core.Messaging.IReceptorRegistryQuery {
    public bool HasReceptors(Whizbang.Core.Messaging.LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasAnyConsumer(string messageType) => false;
    public IReadOnlyList<Whizbang.Core.Messaging.HandledMessageInfo> GetHandledMessages() => handled;
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
