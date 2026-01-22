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
  public async Task Outbox_UseSharedTopic_WithCustomTopic_SetsCustomTopicAsync() {
    // Arrange
    var options = new RoutingOptions();

    // Act
    options.Outbox.UseSharedTopic("my.custom.events");

    // Assert
    var destination = options.OutboxStrategy!.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      new HashSet<string> { "orders" },
      MessageKind.Event
    );
    await Assert.That(destination.Address).IsEqualTo("my.custom.events");
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
}
