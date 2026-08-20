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
  public async Task SharedTopicInboxStrategy_GetSubscription_IncludesControlPlaneNamespaceAsync() {
    // The integrity control plane (checkpoint-driven manifest/redelivery/drill-down requests and
    // their responses) lives in whizbang.core.messaging — a namespace no service "owns". Without
    // this pattern the shared-inbox broker filter silently drops every directed integrity
    // message (observed live: requests fired every cycle, zero receipts, zero dead-letters).
    var strategy = new SharedTopicInboxStrategy();

    var subscription = strategy.GetSubscription(
      new HashSet<string> { "orders" }, "svc", MessageKind.Command);

    await Assert.That(subscription.FilterExpression!).Contains("whizbang.core.messaging.#")
      .Because("directed integrity traffic must be broker-admissible on EVERY service's inbox " +
               "subscription — it is directed, so non-targets still discard locally.");
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

  #region GetSubscriptions (plural seam - topology arc phase 3)

  // Zero-behavior-change contract: the plural surface must produce EXACTLY the same
  // single subscription as today's singular GetSubscription for both built-in strategies.

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscriptions_BitIdenticalToSingularAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.commands" };
    var context = new InboxSubscriptionContext("OrderService", ownedDomains, []);

    // Act
    var singular = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);
    var plural = strategy.GetSubscriptions(context);

    // Assert - exactly one subscription, identical in every observable dimension
    await Assert.That(plural.Count).IsEqualTo(1);
    await Assert.That(plural[0].Topic).IsEqualTo(singular.Topic);
    await Assert.That(plural[0].FilterExpression).IsEqualTo(singular.FilterExpression);
    var singularPatterns = (IReadOnlyList<string>)singular.Metadata!["RoutingPatterns"];
    var pluralPatterns = (IReadOnlyList<string>)plural[0].Metadata!["RoutingPatterns"];
    await Assert.That(pluralPatterns.SequenceEqual(singularPatterns)).IsTrue();
  }

  [Test]
  public async Task SharedTopicInboxStrategy_GetSubscriptions_NullContext_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var strategy = new SharedTopicInboxStrategy();

    // Act & Assert
    await Assert.That(() => strategy.GetSubscriptions(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task CustomSingularOnlyStrategy_GetSubscriptions_DefaultWrapsSingularAsCommandAsync() {
    // Arrange - a strategy implementing ONLY the singular method (pre-phase-3 shape) gets
    // the plural surface for free via the default interface member, which must wrap
    // GetSubscription(ownedDomains, serviceName, MessageKind.Command) in a one-element list.
    var strategy = new RecordingSingularOnlyStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.commands" };
    var context = new InboxSubscriptionContext("OrderService", ownedDomains, []);

    // Act
    var plural = ((IInboxRoutingStrategy)strategy).GetSubscriptions(context);

    // Assert
    await Assert.That(plural.Count).IsEqualTo(1);
    await Assert.That(plural[0].Topic).IsEqualTo("custom-topic");
    await Assert.That(strategy.LastKind).IsEqualTo(MessageKind.Command);
    await Assert.That(strategy.LastServiceName).IsEqualTo("OrderService");
    await Assert.That(strategy.LastOwnedDomains).IsSameReferenceAs(ownedDomains);
  }

  [Test]
  public async Task InboxSubscriptionContext_CarriesAllComponentsAsync() {
    // Arrange
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.commands" };
    var handled = new List<Whizbang.Core.Messaging.HandledMessageInfo> {
      new("MyApp.Orders.Commands.CreateOrder", "myapp.orders.commands", MessageKind.Command)
    };

    // Act
    var context = new InboxSubscriptionContext("OrderService", ownedDomains, handled);

    // Assert
    await Assert.That(context.ServiceName).IsEqualTo("OrderService");
    await Assert.That(context.OwnedDomains).IsSameReferenceAs(ownedDomains);
    await Assert.That(context.HandledMessages.Count).IsEqualTo(1);
    await Assert.That(context.HandledMessages[0].MessageTypeName).IsEqualTo("MyApp.Orders.Commands.CreateOrder");
    await Assert.That(context.HandledMessages[0].ContractNamespace).IsEqualTo("myapp.orders.commands");
    await Assert.That(context.HandledMessages[0].Kind).IsEqualTo(MessageKind.Command);
  }

  /// <summary>Singular-only strategy proving pre-phase-3 implementations keep working and
  /// recording what the plural default interface member passes through.</summary>
  private sealed class RecordingSingularOnlyStrategy : IInboxRoutingStrategy {
    public IReadOnlySet<string>? LastOwnedDomains { get; private set; }
    public string? LastServiceName { get; private set; }
    public MessageKind? LastKind { get; private set; }

    public InboxSubscription GetSubscription(
        IReadOnlySet<string> ownedDomains, string serviceName, MessageKind kind) {
      LastOwnedDomains = ownedDomains;
      LastServiceName = serviceName;
      LastKind = kind;
      return new InboxSubscription("custom-topic");
    }
  }

  #endregion
}
