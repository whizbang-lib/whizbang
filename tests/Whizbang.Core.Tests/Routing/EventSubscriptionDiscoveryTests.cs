using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for EventSubscriptionDiscovery service.
/// Ensures proper discovery and combination of auto-discovered and manual event namespaces.
/// </summary>
public class EventSubscriptionDiscoveryTests {
  #region Constructor Tests

  [Test]
  public async Task Constructor_WithNullRoutingOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange & Act
    var action = () => new EventSubscriptionDiscovery(null!);

    // Assert
    await Assert.That(action).Throws<ArgumentNullException>()
        .WithMessageContaining("routingOptions");
  }

  [Test]
  public async Task Constructor_WithValidOptions_CreatesInstanceAsync() {
    // Arrange
    var options = Options.Create(new RoutingOptions());

    // Act
    var discovery = new EventSubscriptionDiscovery(options);

    // Assert
    await Assert.That(discovery).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithNullRegistry_CreatesInstanceAsync() {
    // Arrange
    var options = Options.Create(new RoutingOptions());

    // Act
    var discovery = new EventSubscriptionDiscovery(options, registry: null);

    // Assert
    await Assert.That(discovery).IsNotNull();
  }

  #endregion

  #region DiscoverEventNamespaces Tests

  [Test]
  public async Task DiscoverEventNamespaces_WithEmptyRegistry_ReturnsManualSubscriptionsOnlyAsync() {
    // Arrange - use empty registry to isolate from static EventNamespaceRegistry
    var routingOptions = new RoutingOptions();
    routingOptions.SubscribeTo("myapp.orders.events", "myapp.payments.events");
    var options = Options.Create(routingOptions);
    var emptyRegistry = TestEventNamespaceRegistry.Create();
    var discovery = new EventSubscriptionDiscovery(options, emptyRegistry);

    // Act
    var namespaces = discovery.DiscoverEventNamespaces();

    // Assert
    await Assert.That(namespaces.Count).IsEqualTo(2);
    await Assert.That(namespaces.Contains("myapp.orders.events")).IsTrue();
    await Assert.That(namespaces.Contains("myapp.payments.events")).IsTrue();
  }

  [Test]
  public async Task DiscoverEventNamespaces_WithRegistry_CombinesAutoAndManualAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    routingOptions.SubscribeTo("myapp.manual.events");
    var options = Options.Create(routingOptions);

    var registry = TestEventNamespaceRegistry.Create(
        perspectiveNamespaces: "myapp.perspective.events",
        receptorNamespaces: "myapp.receptor.events"
    );

    var discovery = new EventSubscriptionDiscovery(options, registry);

    // Act
    var namespaces = discovery.DiscoverEventNamespaces();

    // Assert
    await Assert.That(namespaces.Count).IsEqualTo(3);
    await Assert.That(namespaces.Contains("myapp.manual.events")).IsTrue();
    await Assert.That(namespaces.Contains("myapp.perspective.events")).IsTrue();
    await Assert.That(namespaces.Contains("myapp.receptor.events")).IsTrue();
  }

  [Test]
  public async Task DiscoverEventNamespaces_WithDuplicates_DeduplicatesNamespacesAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    routingOptions.SubscribeTo("myapp.shared.events");
    var options = Options.Create(routingOptions);

    var registry = TestEventNamespaceRegistry.Create(
        perspectiveNamespaces: "myapp.shared.events", // Duplicate
        receptorNamespaces: "myapp.shared.events"      // Duplicate
    );

    var discovery = new EventSubscriptionDiscovery(options, registry);

    // Act
    var namespaces = discovery.DiscoverEventNamespaces();

    // Assert
    await Assert.That(namespaces.Count).IsEqualTo(1);
    await Assert.That(namespaces.Contains("myapp.shared.events")).IsTrue();
  }

  [Test]
  public async Task DiscoverEventNamespaces_CaseInsensitive_DeduplicatesAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    routingOptions.SubscribeTo("MyApp.Orders.Events");
    var options = Options.Create(routingOptions);

    var registry = TestEventNamespaceRegistry.Create(
        perspectiveNamespaces: "myapp.orders.events" // Different case
    );

    var discovery = new EventSubscriptionDiscovery(options, registry);

    // Act
    var namespaces = discovery.DiscoverEventNamespaces();

    // Assert
    await Assert.That(namespaces.Count).IsEqualTo(1);
  }

  [Test]
  public async Task DiscoverEventNamespaces_WithEmptyOptionsAndRegistry_ReturnsEmptySetAsync() {
    // Arrange - use empty registry to isolate from static EventNamespaceRegistry
    var options = Options.Create(new RoutingOptions());
    var emptyRegistry = TestEventNamespaceRegistry.Create();
    var discovery = new EventSubscriptionDiscovery(options, emptyRegistry);

    // Act
    var namespaces = discovery.DiscoverEventNamespaces();

    // Assert
    await Assert.That(namespaces.Count).IsEqualTo(0);
  }

  [Test]
  public async Task DiscoverEventNamespaces_ExcludesOwnedDomainsExactMatchAsync() {
    // Arrange - BFF service owns "jdx.contracts.bff", shouldn't subscribe to its own events
    // Use empty registry to isolate from static EventNamespaceRegistry
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("jdx.contracts.bff");
    routingOptions.SubscribeTo("jdx.contracts.bff", "jdx.contracts.auth");
    var options = Options.Create(routingOptions);
    var emptyRegistry = TestEventNamespaceRegistry.Create();
    var discovery = new EventSubscriptionDiscovery(options, emptyRegistry);

    // Act
    var namespaces = discovery.DiscoverEventNamespaces();

    // Assert - should only contain auth, not bff (owned)
    await Assert.That(namespaces.Count).IsEqualTo(1);
    await Assert.That(namespaces.Contains("jdx.contracts.auth")).IsTrue();
    await Assert.That(namespaces.Contains("jdx.contracts.bff")).IsFalse();
  }

  [Test]
  public async Task DiscoverEventNamespaces_ExcludesOwnedDomainChildNamespacesAsync() {
    // Arrange - BFF owns "jdx.contracts.bff", shouldn't subscribe to child namespaces
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("jdx.contracts.bff");
    var options = Options.Create(routingOptions);

    var registry = TestEventNamespaceRegistry.Create(
        perspectiveNamespaces: "jdx.contracts.bff.events",  // Child of owned domain
        receptorNamespaces: "jdx.contracts.auth.events"     // Not owned
    );

    var discovery = new EventSubscriptionDiscovery(options, registry);

    // Act
    var namespaces = discovery.DiscoverEventNamespaces();

    // Assert - should only contain auth events, not bff.events (child of owned)
    await Assert.That(namespaces.Count).IsEqualTo(1);
    await Assert.That(namespaces.Contains("jdx.contracts.auth.events")).IsTrue();
    await Assert.That(namespaces.Contains("jdx.contracts.bff.events")).IsFalse();
  }

  [Test]
  public async Task DiscoverEventNamespaces_OwnedDomainWithTrailingDotAsync() {
    // Arrange - owned domain already has trailing dot
    // Use empty registry to isolate from static EventNamespaceRegistry
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("jdx.contracts.bff.");
    routingOptions.SubscribeTo("jdx.contracts.bff.events", "jdx.contracts.user.events");
    var options = Options.Create(routingOptions);
    var emptyRegistry = TestEventNamespaceRegistry.Create();
    var discovery = new EventSubscriptionDiscovery(options, emptyRegistry);

    // Act
    var namespaces = discovery.DiscoverEventNamespaces();

    // Assert - should exclude bff.events even with trailing dot
    await Assert.That(namespaces.Count).IsEqualTo(1);
    await Assert.That(namespaces.Contains("jdx.contracts.user.events")).IsTrue();
    await Assert.That(namespaces.Contains("jdx.contracts.bff.events")).IsFalse();
  }

  [Test]
  public async Task DiscoverEventNamespaces_MultipleOwnedDomainsAsync() {
    // Arrange - service owns multiple domains
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("jdx.contracts.bff", "jdx.contracts.admin");
    var options = Options.Create(routingOptions);

    var registry = TestEventNamespaceRegistry.Create(
        perspectiveNamespaces: "jdx.contracts.bff.events",
        receptorNamespaces: "jdx.contracts.admin.events"
    );
    routingOptions.SubscribeTo("jdx.contracts.user.events");

    var discovery = new EventSubscriptionDiscovery(options, registry);

    // Act
    var namespaces = discovery.DiscoverEventNamespaces();

    // Assert - both owned domain children should be excluded
    await Assert.That(namespaces.Count).IsEqualTo(1);
    await Assert.That(namespaces.Contains("jdx.contracts.user.events")).IsTrue();
    await Assert.That(namespaces.Contains("jdx.contracts.bff.events")).IsFalse();
    await Assert.That(namespaces.Contains("jdx.contracts.admin.events")).IsFalse();
  }

  [Test]
  public async Task DiscoverEventNamespaces_OwnedDomainCaseInsensitiveAsync() {
    // Arrange - owned domain matching should be case-insensitive
    // Use empty registry to isolate from static EventNamespaceRegistry
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("JDX.Contracts.BFF");
    routingOptions.SubscribeTo("jdx.contracts.bff.events", "jdx.contracts.auth.events");
    var options = Options.Create(routingOptions);
    var emptyRegistry = TestEventNamespaceRegistry.Create();
    var discovery = new EventSubscriptionDiscovery(options, emptyRegistry);

    // Act
    var namespaces = discovery.DiscoverEventNamespaces();

    // Assert - should exclude bff.events despite case difference
    await Assert.That(namespaces.Count).IsEqualTo(1);
    await Assert.That(namespaces.Contains("jdx.contracts.auth.events")).IsTrue();
  }

  #endregion

  #region GetAutoDiscoveredNamespaces Tests

  [Test]
  public async Task GetAutoDiscoveredNamespaces_WithEmptyRegistry_ReturnsEmptySetAsync() {
    // Arrange - use empty registry to isolate from static EventNamespaceRegistry
    var options = Options.Create(new RoutingOptions());
    var emptyRegistry = TestEventNamespaceRegistry.Create();
    var discovery = new EventSubscriptionDiscovery(options, emptyRegistry);

    // Act
    var namespaces = discovery.GetAutoDiscoveredNamespaces();

    // Assert
    await Assert.That(namespaces.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetAutoDiscoveredNamespaces_WithRegistry_ReturnsAllEventNamespacesAsync() {
    // Arrange
    var options = Options.Create(new RoutingOptions());
    var registry = TestEventNamespaceRegistry.Create(
        perspectiveNamespaces: "myapp.perspective.events",
        receptorNamespaces: "myapp.receptor.events"
    );
    var discovery = new EventSubscriptionDiscovery(options, registry);

    // Act
    var namespaces = discovery.GetAutoDiscoveredNamespaces();

    // Assert
    await Assert.That(namespaces.Count).IsEqualTo(2);
    await Assert.That(namespaces.Contains("myapp.perspective.events")).IsTrue();
    await Assert.That(namespaces.Contains("myapp.receptor.events")).IsTrue();
  }

  [Test]
  public async Task GetAutoDiscoveredNamespaces_DoesNotIncludeManualSubscriptionsAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    routingOptions.SubscribeTo("myapp.manual.events");
    var options = Options.Create(routingOptions);

    var registry = TestEventNamespaceRegistry.Create(perspectiveNamespaces: "myapp.auto.events");

    var discovery = new EventSubscriptionDiscovery(options, registry);

    // Act
    var namespaces = discovery.GetAutoDiscoveredNamespaces();

    // Assert
    await Assert.That(namespaces.Count).IsEqualTo(1);
    await Assert.That(namespaces.Contains("myapp.auto.events")).IsTrue();
    await Assert.That(namespaces.Contains("myapp.manual.events")).IsFalse();
  }

  #endregion

  #region GetManualSubscriptions Tests

  [Test]
  public async Task GetManualSubscriptions_WithNoSubscriptions_ReturnsEmptySetAsync() {
    // Arrange
    var options = Options.Create(new RoutingOptions());
    var discovery = new EventSubscriptionDiscovery(options);

    // Act
    var namespaces = discovery.GetManualSubscriptions();

    // Assert
    await Assert.That(namespaces.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetManualSubscriptions_ReturnsConfiguredSubscriptionsAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    routingOptions.SubscribeTo("myapp.orders.events", "myapp.payments.events");
    var options = Options.Create(routingOptions);
    var discovery = new EventSubscriptionDiscovery(options);

    // Act
    var namespaces = discovery.GetManualSubscriptions();

    // Assert
    await Assert.That(namespaces.Count).IsEqualTo(2);
    await Assert.That(namespaces.Contains("myapp.orders.events")).IsTrue();
    await Assert.That(namespaces.Contains("myapp.payments.events")).IsTrue();
  }

  [Test]
  public async Task GetManualSubscriptions_DoesNotIncludeAutoDiscoveredAsync() {
    // Arrange
    var routingOptions = new RoutingOptions();
    routingOptions.SubscribeTo("myapp.manual.events");
    var options = Options.Create(routingOptions);

    var registry = TestEventNamespaceRegistry.Create(perspectiveNamespaces: "myapp.auto.events");

    var discovery = new EventSubscriptionDiscovery(options, registry);

    // Act
    var namespaces = discovery.GetManualSubscriptions();

    // Assert
    await Assert.That(namespaces.Count).IsEqualTo(1);
    await Assert.That(namespaces.Contains("myapp.manual.events")).IsTrue();
    await Assert.That(namespaces.Contains("myapp.auto.events")).IsFalse();
  }

  #endregion

  #region Test Helpers

  /// <summary>
  /// Test implementation of IEventNamespaceRegistry for unit testing.
  /// </summary>
  private sealed class TestEventNamespaceRegistry : IEventNamespaceRegistry {
    private readonly HashSet<string> _perspectiveNamespaces;
    private readonly HashSet<string> _receptorNamespaces;
    private readonly HashSet<string> _allNamespaces;

    private TestEventNamespaceRegistry(HashSet<string> perspectiveNamespaces, HashSet<string> receptorNamespaces) {
      _perspectiveNamespaces = perspectiveNamespaces;
      _receptorNamespaces = receptorNamespaces;

      _allNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var ns in _perspectiveNamespaces) {
        _allNamespaces.Add(ns);
      }
      foreach (var ns in _receptorNamespaces) {
        _allNamespaces.Add(ns);
      }
    }

    public static TestEventNamespaceRegistry Create(
        string? perspectiveNamespaces = null,
        string? receptorNamespaces = null) {
      var perspectives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var receptors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      if (perspectiveNamespaces is not null) {
        perspectives.Add(perspectiveNamespaces);
      }

      if (receptorNamespaces is not null) {
        receptors.Add(receptorNamespaces);
      }

      return new TestEventNamespaceRegistry(perspectives, receptors);
    }

    public IReadOnlySet<string> GetPerspectiveEventNamespaces() => _perspectiveNamespaces;
    public IReadOnlySet<string> GetReceptorEventNamespaces() => _receptorNamespaces;
    public IReadOnlySet<string> GetAllEventNamespaces() => _allNamespaces;
  }

  #endregion
}
