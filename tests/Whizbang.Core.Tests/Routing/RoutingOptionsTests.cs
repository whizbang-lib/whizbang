using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for RoutingOptions and related fluent API.
/// </summary>
public class RoutingOptionsTests {
  #region OwnDomains

  [Test]
  public async Task OwnDomains_WithSingleDomain_AddsDomainAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.OwnDomains("orders");

    // Assert
    await Assert.That(options.OwnedDomains).Contains("orders");
  }

  [Test]
  public async Task OwnDomains_WithMultipleDomains_AddsAllDomainsAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.OwnDomains("orders", "inventory", "shipping");

    // Assert
    await Assert.That(options.OwnedDomains.Count).IsEqualTo(3);
    await Assert.That(options.OwnedDomains).Contains("orders");
    await Assert.That(options.OwnedDomains).Contains("inventory");
    await Assert.That(options.OwnedDomains).Contains("shipping");
  }

  [Test]
  public async Task OwnDomains_IsCaseInsensitiveAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.OwnDomains("Orders", "INVENTORY");

    // Assert - Should store lowercase and match case-insensitively
    await Assert.That(options.OwnedDomains.Contains("orders")).IsTrue();
    await Assert.That(options.OwnedDomains.Contains("ORDERS")).IsTrue();
  }

  [Test]
  public async Task OwnDomains_CalledMultipleTimes_AccumulatesDomainsAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.OwnDomains("orders");
    options.OwnDomains("inventory");

    // Assert
    await Assert.That(options.OwnedDomains.Count).IsEqualTo(2);
  }

  [Test]
  public async Task OwnDomains_WithDuplicates_DeduplicatesAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.OwnDomains("orders", "orders", "inventory");

    // Assert
    await Assert.That(options.OwnedDomains.Count).IsEqualTo(2);
  }

  [Test]
  public async Task OwnDomains_ReturnsOptionsForChaining_FluentApiAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    var result = options.OwnDomains("orders");

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task OwnDomains_WithNullArray_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act & Assert
    await Assert.That(() => options.OwnDomains(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task OwnDomains_WithEmptyArray_DoesNothingAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.OwnDomains();

    // Assert
    await Assert.That(options.OwnedDomains.Count).IsEqualTo(0);
  }

  #endregion

  #region OwnNamespaceOf<T>

  [Test]
  public async Task OwnNamespaceOf_WithValidType_AddsNamespaceAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.OwnNamespaceOf<OutboxTestTypes.Orders.Commands.CreateOrder>();

    // Assert
    await Assert.That(options.OwnedDomains).Contains("outboxtesttypes.orders.commands");
  }

  [Test]
  public async Task OwnNamespaceOf_WithMultipleTypes_AddsAllNamespacesAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.OwnNamespaceOf<OutboxTestTypes.Orders.Commands.CreateOrder>()
           .OwnNamespaceOf<OutboxTestTypes.Users.Commands.CreateUser>();

    // Assert
    await Assert.That(options.OwnedDomains.Count).IsEqualTo(2);
    await Assert.That(options.OwnedDomains).Contains("outboxtesttypes.orders.commands");
    await Assert.That(options.OwnedDomains).Contains("outboxtesttypes.users.commands");
  }

  [Test]
  public async Task OwnNamespaceOf_ReturnsOptionsForChainingAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    var result = options.OwnNamespaceOf<OutboxTestTypes.Orders.Commands.CreateOrder>();

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task OwnNamespaceOf_WithTypeWithoutNamespace_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act & Assert
    await Assert.That(() => options.OwnNamespaceOf<TypeWithoutNamespace>())
      .Throws<InvalidOperationException>()
      .WithMessageContaining("has no namespace");
  }

  [Test]
  public async Task OwnNamespaceOf_CanChainWithOwnDomainsAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.OwnNamespaceOf<OutboxTestTypes.Orders.Commands.CreateOrder>()
           .OwnDomains("myapp.legacy.*");

    // Assert
    await Assert.That(options.OwnedDomains.Count).IsEqualTo(2);
    await Assert.That(options.OwnedDomains).Contains("outboxtesttypes.orders.commands");
    await Assert.That(options.OwnedDomains).Contains("myapp.legacy.*");
  }

  [Test]
  public async Task OwnNamespaceOf_WithSameNamespaceTwice_DeduplicatesAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act - Both types are in same namespace
    options.OwnNamespaceOf<OutboxTestTypes.Orders.Commands.CreateOrder>()
           .OwnNamespaceOf<OutboxTestTypes.Orders.Commands.UpdateOrder>();

    // Assert
    await Assert.That(options.OwnedDomains.Count).IsEqualTo(1);
  }

  #endregion

  #region SubscribeTo

  [Test]
  public async Task SubscribeTo_WithSingleNamespace_AddsNamespaceAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.SubscribeTo("myapp.orders.events");

    // Assert
    await Assert.That(options.SubscribedNamespaces).Contains("myapp.orders.events");
  }

  [Test]
  public async Task SubscribeTo_WithMultipleNamespaces_AddsAllNamespacesAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.SubscribeTo("myapp.orders.events", "myapp.payments.events", "myapp.users.events");

    // Assert
    await Assert.That(options.SubscribedNamespaces.Count).IsEqualTo(3);
    await Assert.That(options.SubscribedNamespaces).Contains("myapp.orders.events");
    await Assert.That(options.SubscribedNamespaces).Contains("myapp.payments.events");
    await Assert.That(options.SubscribedNamespaces).Contains("myapp.users.events");
  }

  [Test]
  public async Task SubscribeTo_IsCaseInsensitiveAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.SubscribeTo("MyApp.Orders.Events", "MYAPP.PAYMENTS.EVENTS");

    // Assert - Should store lowercase and match case-insensitively
    await Assert.That(options.SubscribedNamespaces.Contains("myapp.orders.events")).IsTrue();
    await Assert.That(options.SubscribedNamespaces.Contains("MYAPP.ORDERS.EVENTS")).IsTrue();
  }

  [Test]
  public async Task SubscribeTo_CalledMultipleTimes_AccumulatesNamespacesAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.SubscribeTo("myapp.orders.events");
    options.SubscribeTo("myapp.payments.events");

    // Assert
    await Assert.That(options.SubscribedNamespaces.Count).IsEqualTo(2);
  }

  [Test]
  public async Task SubscribeTo_WithDuplicates_DeduplicatesAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.SubscribeTo("myapp.orders.events", "myapp.orders.events", "myapp.payments.events");

    // Assert
    await Assert.That(options.SubscribedNamespaces.Count).IsEqualTo(2);
  }

  [Test]
  public async Task SubscribeTo_ReturnsOptionsForChainingAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    var result = options.SubscribeTo("myapp.orders.events");

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task SubscribeTo_WithNullArray_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act & Assert
    await Assert.That(() => options.SubscribeTo(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task SubscribeTo_WithEmptyArray_DoesNothingAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.SubscribeTo();

    // Assert
    await Assert.That(options.SubscribedNamespaces.Count).IsEqualTo(0);
  }

  [Test]
  public async Task SubscribeTo_WithWhitespaceOnlyStrings_IgnoresWhitespaceAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.SubscribeTo("myapp.orders.events", "  ", "", "\t", "myapp.payments.events");

    // Assert - Only non-whitespace namespaces should be added
    await Assert.That(options.SubscribedNamespaces.Count).IsEqualTo(2);
    await Assert.That(options.SubscribedNamespaces).Contains("myapp.orders.events");
    await Assert.That(options.SubscribedNamespaces).Contains("myapp.payments.events");
  }

  #endregion

  #region SubscribeToNamespaceOf<T>

  [Test]
  public async Task SubscribeToNamespaceOf_WithValidType_AddsNamespaceAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.SubscribeToNamespaceOf<OutboxTestTypes.Orders.Events.OrderCreated>();

    // Assert
    await Assert.That(options.SubscribedNamespaces).Contains("outboxtesttypes.orders.events");
  }

  [Test]
  public async Task SubscribeToNamespaceOf_WithMultipleTypes_AddsAllNamespacesAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.SubscribeToNamespaceOf<OutboxTestTypes.Orders.Events.OrderCreated>()
           .SubscribeToNamespaceOf<OutboxTestTypes.Users.Events.UserCreated>();

    // Assert
    await Assert.That(options.SubscribedNamespaces.Count).IsEqualTo(2);
    await Assert.That(options.SubscribedNamespaces).Contains("outboxtesttypes.orders.events");
    await Assert.That(options.SubscribedNamespaces).Contains("outboxtesttypes.users.events");
  }

  [Test]
  public async Task SubscribeToNamespaceOf_ReturnsOptionsForChainingAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    var result = options.SubscribeToNamespaceOf<OutboxTestTypes.Orders.Events.OrderCreated>();

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task SubscribeToNamespaceOf_WithTypeWithoutNamespace_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act & Assert
    await Assert.That(() => options.SubscribeToNamespaceOf<TypeWithoutNamespace>())
      .Throws<InvalidOperationException>()
      .WithMessageContaining("has no namespace");
  }

  [Test]
  public async Task SubscribeToNamespaceOf_CanChainWithSubscribeToAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.SubscribeToNamespaceOf<OutboxTestTypes.Orders.Events.OrderCreated>()
           .SubscribeTo("myapp.legacy.events");

    // Assert
    await Assert.That(options.SubscribedNamespaces.Count).IsEqualTo(2);
    await Assert.That(options.SubscribedNamespaces).Contains("outboxtesttypes.orders.events");
    await Assert.That(options.SubscribedNamespaces).Contains("myapp.legacy.events");
  }

  [Test]
  public async Task SubscribeToNamespaceOf_WithSameNamespaceTwice_DeduplicatesAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act - Both types are in same namespace
    options.SubscribeToNamespaceOf<OutboxTestTypes.Orders.Events.OrderCreated>()
           .SubscribeToNamespaceOf<OutboxTestTypes.Orders.Events.OrderUpdated>();

    // Assert
    await Assert.That(options.SubscribedNamespaces.Count).IsEqualTo(1);
  }

  [Test]
  public async Task SubscribeToNamespaceOf_CanMixWithOwnNamespaceOfAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.OwnNamespaceOf<OutboxTestTypes.Orders.Commands.CreateOrder>()
           .SubscribeToNamespaceOf<OutboxTestTypes.Users.Events.UserCreated>();

    // Assert
    await Assert.That(options.OwnedDomains.Count).IsEqualTo(1);
    await Assert.That(options.SubscribedNamespaces.Count).IsEqualTo(1);
    await Assert.That(options.OwnedDomains).Contains("outboxtesttypes.orders.commands");
    await Assert.That(options.SubscribedNamespaces).Contains("outboxtesttypes.users.events");
  }

  #endregion

  #region Inbox Strategy

  [Test]
  public async Task Inbox_UseSharedTopic_SetsSharedTopicStrategyAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.Inbox.UseSharedTopic();

    // Assert
    await Assert.That(options.InboxStrategy).IsTypeOf<SharedTopicInboxStrategy>();
  }

  [Test]
  public async Task Inbox_UseSharedTopic_WithCustomTopic_SetsCustomTopicAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.Inbox.UseSharedTopic("my.custom.inbox");

    // Assert
    await Assert.That(options.InboxStrategy).IsTypeOf<SharedTopicInboxStrategy>();
    var subscription = options.InboxStrategy!.GetSubscription(
      new HashSet<string> { "test" },
      "test-service",
      MessageKind.Command
    );
    await Assert.That(subscription.Topic).IsEqualTo("my.custom.inbox");
  }

  [Test]
  public async Task Inbox_UseDomainTopics_SetsDomainTopicStrategyAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.Inbox.UseDomainTopics();

    // Assert
    await Assert.That(options.InboxStrategy).IsTypeOf<DomainTopicInboxStrategy>();
  }

  [Test]
  public async Task Inbox_UseDomainTopics_WithCustomSuffix_SetsCustomSuffixAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.Inbox.UseDomainTopics(".in");

    // Assert
    var subscription = options.InboxStrategy!.GetSubscription(
      new HashSet<string> { "orders" },
      "test-service",
      MessageKind.Command
    );
    await Assert.That(subscription.Topic).IsEqualTo("orders.in");
  }

  [Test]
  public async Task Inbox_UseCustomStrategy_SetsCustomStrategyAsync() {
    // Arrange
    var options = new RoutingOptions();
    var customStrategy = new TestInboxStrategy();

    // Act
    options.Inbox.UseCustom(customStrategy);

    // Assert
    await Assert.That(options.InboxStrategy).IsSameReferenceAs(customStrategy);
  }

  [Test]
  public async Task Inbox_DefaultStrategy_IsSharedTopicAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Assert - Default should be SharedTopicInboxStrategy
    await Assert.That(options.InboxStrategy).IsTypeOf<SharedTopicInboxStrategy>();
  }

  #endregion

  #region Outbox Strategy

  [Test]
  public async Task Outbox_UseDomainTopics_SetsDomainTopicStrategyAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.Outbox.UseDomainTopics();

    // Assert
    await Assert.That(options.OutboxStrategy).IsTypeOf<DomainTopicOutboxStrategy>();
  }

  [Test]
  public async Task Outbox_UseSharedTopic_SetsSharedTopicStrategyAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.Outbox.UseSharedTopic();

    // Assert
    await Assert.That(options.OutboxStrategy).IsTypeOf<SharedTopicOutboxStrategy>();
  }

  [Test]
  public async Task Outbox_UseSharedTopic_WithCustomInboxTopic_RoutesCommandsToCustomInboxAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.Outbox.UseSharedTopic("my.custom.inbox");

    // Assert - Commands route to the custom inbox topic
    var destination = options.OutboxStrategy!.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      new HashSet<string> { "outboxtesttypes.orders.commands" },
      MessageKind.Command
    );
    await Assert.That(destination.Address).IsEqualTo("my.custom.inbox");
  }

  [Test]
  public async Task Outbox_UseSharedTopic_EventsRouteToNamespaceTopicsAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.Outbox.UseSharedTopic("my.custom.inbox");

    // Assert - Events route to namespace-specific topics, not the inbox
    var destination = options.OutboxStrategy!.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      new HashSet<string> { "outboxtesttypes.orders.events" },
      MessageKind.Event
    );
    await Assert.That(destination.Address).IsEqualTo("outboxtesttypes.orders.events");
  }

  [Test]
  public async Task Outbox_UseCustomStrategy_SetsCustomStrategyAsync() {
    // Arrange
    var options = new RoutingOptions();
    var customStrategy = new TestOutboxStrategy();

    // Act
    options.Outbox.UseCustom(customStrategy);

    // Assert
    await Assert.That(options.OutboxStrategy).IsSameReferenceAs(customStrategy);
  }

  [Test]
  public async Task Outbox_DefaultStrategy_IsDomainTopicAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Assert - Default should be DomainTopicOutboxStrategy
    await Assert.That(options.OutboxStrategy).IsTypeOf<DomainTopicOutboxStrategy>();
  }

  #endregion

  #region Fluent Chaining

  [Test]
  public async Task FluentApi_FullConfiguration_WorksCorrectlyAsync() {
    // Arrange & Act
    var options = new RoutingOptions()
      .OwnDomains("orders", "inventory")
      .ConfigureInbox(inbox => inbox.UseSharedTopic("commands.inbox"))
      .ConfigureOutbox(outbox => outbox.UseDomainTopics());

    // Assert
    await Assert.That(options.OwnedDomains.Count).IsEqualTo(2);
    await Assert.That(options.InboxStrategy).IsTypeOf<SharedTopicInboxStrategy>();
    await Assert.That(options.OutboxStrategy).IsTypeOf<DomainTopicOutboxStrategy>();
  }

  [Test]
  public async Task ConfigureInbox_WithNullAction_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act & Assert
    await Assert.That(() => options.ConfigureInbox(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ConfigureOutbox_WithNullAction_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act & Assert
    await Assert.That(() => options.ConfigureOutbox(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ConfigureInbox_ReturnsOptionsForChainingAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    var result = options.ConfigureInbox(inbox => inbox.UseSharedTopic());

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task ConfigureOutbox_ReturnsOptionsForChainingAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    var result = options.ConfigureOutbox(outbox => outbox.UseSharedTopic());

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task Inbox_UseCustom_WithNullStrategy_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act & Assert
    await Assert.That(() => options.Inbox.UseCustom(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Outbox_UseCustom_WithNullStrategy_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act & Assert
    await Assert.That(() => options.Outbox.UseCustom(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task OwnDomains_WithWhitespaceOnlyStrings_IgnoresWhitespaceAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.OwnDomains("orders", "  ", "", "\t", "inventory");

    // Assert - Only non-whitespace domains should be added
    await Assert.That(options.OwnedDomains.Count).IsEqualTo(2);
    await Assert.That(options.OwnedDomains).Contains("orders");
    await Assert.That(options.OwnedDomains).Contains("inventory");
  }

  [Test]
  public async Task Inbox_UseSharedTopic_ReturnsParentOptionsForChainingAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    var result = options.Inbox.UseSharedTopic();

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task Inbox_UseDomainTopics_ReturnsParentOptionsForChainingAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    var result = options.Inbox.UseDomainTopics();

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task Inbox_UseCustom_ReturnsParentOptionsForChainingAsync() {
    // Arrange
    var options = new RoutingOptions();
    var customStrategy = new TestInboxStrategy();

    // Act
    var result = options.Inbox.UseCustom(customStrategy);

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task Outbox_UseDomainTopics_ReturnsParentOptionsForChainingAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    var result = options.Outbox.UseDomainTopics();

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task Outbox_UseSharedTopic_ReturnsParentOptionsForChainingAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    var result = options.Outbox.UseSharedTopic();

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task Outbox_UseCustom_ReturnsParentOptionsForChainingAsync() {
    // Arrange
    var options = new RoutingOptions();
    var customStrategy = new TestOutboxStrategy();

    // Act
    var result = options.Outbox.UseCustom(customStrategy);

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  #endregion

  #region Test Helpers

  private sealed class TestInboxStrategy : IInboxRoutingStrategy {
    public InboxSubscription GetSubscription(
      IReadOnlySet<string> ownedDomains,
      string serviceName,
      MessageKind kind
    ) {
      return new InboxSubscription("test-inbox");
    }
  }

  private sealed class TestOutboxStrategy : IOutboxRoutingStrategy {
    public TransportDestination GetDestination(
      Type messageType,
      IReadOnlySet<string> ownedDomains,
      MessageKind kind
    ) {
      return new TransportDestination("test-outbox");
    }
  }

  #endregion

  #region SubscribeToAudit

  [Test]
  public async Task SubscribeToAudit_AddsAuditNamespaceAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.SubscribeToAudit();

    // Assert
    await Assert.That(options.SubscribedNamespaces)
        .Contains(Whizbang.Core.SystemEvents.AuditingEventStoreDecorator.AUDIT_TOPIC_DESTINATION);
  }

  [Test]
  public async Task SubscribeToAudit_EnablesAuditPerspectiveByDefaultAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.SubscribeToAudit();

    // Assert
    await Assert.That(options.AuditPerspectiveEnabled).IsTrue();
  }

  [Test]
  public async Task SubscribeToAudit_WithFalse_DisablesAuditPerspectiveAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.SubscribeToAudit(autoGeneratePerspective: false);

    // Assert
    await Assert.That(options.AuditPerspectiveEnabled).IsFalse();
    // But still subscribes to the namespace
    await Assert.That(options.SubscribedNamespaces)
        .Contains(Whizbang.Core.SystemEvents.AuditingEventStoreDecorator.AUDIT_TOPIC_DESTINATION);
  }

  [Test]
  public async Task SubscribeToAudit_ReturnsSelfForChainingAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    var result = options.SubscribeToAudit();

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task SubscribeToAudit_CanChainWithOwnDomainsAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act - typical BFF usage pattern
    options.OwnDomains("jdx.contracts.bff")
           .SubscribeToAudit()
           .SubscribeTo("jdx.contracts.job");

    // Assert
    await Assert.That(options.OwnedDomains).Contains("jdx.contracts.bff");
    await Assert.That(options.SubscribedNamespaces)
        .Contains(Whizbang.Core.SystemEvents.AuditingEventStoreDecorator.AUDIT_TOPIC_DESTINATION);
    await Assert.That(options.SubscribedNamespaces).Contains("jdx.contracts.job");
    await Assert.That(options.AuditPerspectiveEnabled).IsTrue();
  }

  [Test]
  public async Task AuditPerspectiveEnabled_DefaultsFalse_WhenNotSubscribedAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Assert - Not enabled until SubscribeToAudit is called
    await Assert.That(options.AuditPerspectiveEnabled).IsFalse();
  }

  #endregion
}
