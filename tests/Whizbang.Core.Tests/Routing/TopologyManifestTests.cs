using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for TopologyManifestBuilder — the pure derived projection of a service's
/// transport topology (topology arc phase 3). The manifest lists every publish
/// destination (union over the message catalog via GetDestination per kind) and the
/// subscription set (GetSubscriptions). Core invariant: route, split, subscribe,
/// provision are ALL projections of GetDestination — the manifest is deterministic
/// and names no entity the strategies didn't name.
/// </summary>
public class TopologyManifestTests {
  private static InboxSubscriptionContext _context(params string[] ownedDomains) =>
    new("OrderService",
        new HashSet<string>(ownedDomains, StringComparer.OrdinalIgnoreCase),
        []);

  private static MessageTypeCatalogEntry _entry(Type type, string kind) =>
    new(type, type.FullName!, kind, PinnedId: null);

  #region Projection of GetDestination

  [Test]
  public async Task Build_PublishDestinations_AreExactProjectionsOfGetDestinationAsync() {
    // Arrange
    var outbox = new SharedTopicOutboxStrategy();
    var inbox = new SharedTopicInboxStrategy();
    var context = _context("outboxtesttypes.orders.commands");
    var catalog = new[] {
      _entry(typeof(OutboxTestTypes.Orders.Commands.CreateOrder), "command"),
      _entry(typeof(OutboxTestTypes.Orders.Events.OrderCreated), "event")
    };

    // Act
    var manifest = TopologyManifestBuilder.Build(outbox, inbox, context, catalog);

    // Assert - each publish destination is the strategy's own GetDestination answer
    await Assert.That(manifest.PublishDestinations.Count).IsEqualTo(2);
    foreach (var published in manifest.PublishDestinations) {
      var type = catalog.Single(e => e.ClrTypeName == published.MessageTypeName).Type;
      var expected = outbox.GetDestination(type, context.OwnedDomains, published.Kind);
      await Assert.That(published.Address).IsEqualTo(expected.Address);
      await Assert.That(published.RoutingKey).IsEqualTo(expected.RoutingKey);
      await Assert.That(published.CompositeGroupKey)
        .IsEqualTo(((IOutboxRoutingStrategy)outbox).GetCompositeGroupKey(type, context.OwnedDomains, published.Kind))
        .Because("split (composite group key) must be the same projection of GetDestination as route.");
    }

    // Command entry → shared inbox; event entry → namespace topic
    var command = manifest.PublishDestinations.Single(d => d.Kind == MessageKind.Command);
    await Assert.That(command.Address).IsEqualTo("inbox");
    var @event = manifest.PublishDestinations.Single(d => d.Kind == MessageKind.Event);
    await Assert.That(@event.Address).IsEqualTo("outboxtesttypes.orders.events");
  }

  [Test]
  public async Task Build_SubscriptionSet_IsExactlyGetSubscriptionsAsync() {
    // Arrange
    var outbox = new DomainTopicOutboxStrategy();
    var inbox = new SharedTopicInboxStrategy();
    var context = _context("outboxtesttypes.orders.commands");

    // Act
    var manifest = TopologyManifestBuilder.Build(outbox, inbox, context, []);

    // Assert - same subscriptions as calling the strategy directly
    var direct = inbox.GetSubscriptions(context);
    await Assert.That(manifest.Subscriptions.Count).IsEqualTo(direct.Count);
    for (var i = 0; i < direct.Count; i++) {
      await Assert.That(manifest.Subscriptions[i].Topic).IsEqualTo(direct[i].Topic);
      await Assert.That(manifest.Subscriptions[i].FilterExpression).IsEqualTo(direct[i].FilterExpression);
    }
  }

  [Test]
  public async Task Build_NamesNoEntityTheStrategiesDidntNameAsync() {
    // The provisioning invariant: every address/topic in the manifest must be one the
    // strategies themselves produced — the builder invents nothing.
    var outbox = new SharedTopicOutboxStrategy();
    var inbox = new DomainTopicInboxStrategy();
    var context = _context("orders");
    var catalog = new[] {
      _entry(typeof(OutboxTestTypes.Orders.Commands.CreateOrder), "command"),
      _entry(typeof(OutboxTestTypes.Orders.Events.OrderCreated), "event"),
      _entry(typeof(OutboxTestTypes.Users.Events.UserCreated), "event")
    };

    var manifest = TopologyManifestBuilder.Build(outbox, inbox, context, catalog);

    var namedByOutbox = catalog
      .Select(e => outbox.GetDestination(
          e.Type, context.OwnedDomains, e.Kind == "command" ? MessageKind.Command : MessageKind.Event).Address)
      .ToHashSet(StringComparer.Ordinal);
    var namedByInbox = inbox.GetSubscriptions(context).Select(s => s.Topic).ToHashSet(StringComparer.Ordinal);

    foreach (var published in manifest.PublishDestinations) {
      await Assert.That(namedByOutbox.Contains(published.Address)).IsTrue()
        .Because($"publish address '{published.Address}' must come from GetDestination.");
    }
    foreach (var subscription in manifest.Subscriptions) {
      await Assert.That(namedByInbox.Contains(subscription.Topic)).IsTrue()
        .Because($"subscription topic '{subscription.Topic}' must come from GetSubscriptions.");
    }
  }

  #endregion

  #region Determinism

  [Test]
  public async Task Build_RecomputedFromSameInputs_IsIdenticalAsync() {
    var outbox = new SharedTopicOutboxStrategy();
    var inbox = new SharedTopicInboxStrategy();
    var context = _context("outboxtesttypes.orders.commands");
    var catalog = new[] {
      _entry(typeof(OutboxTestTypes.Orders.Events.OrderCreated), "event"),
      _entry(typeof(OutboxTestTypes.Orders.Commands.CreateOrder), "command"),
      _entry(typeof(OutboxTestTypes.Users.Events.UserCreated), "event")
    };

    var first = TopologyManifestBuilder.Build(outbox, inbox, context, catalog);
    var second = TopologyManifestBuilder.Build(outbox, inbox, context, catalog);

    await Assert.That(first.PublishDestinations.SequenceEqual(second.PublishDestinations)).IsTrue()
      .Because("Recomputing the manifest from the same strategy inputs must be deterministic.");
    await Assert.That(first.Subscriptions.Count).IsEqualTo(second.Subscriptions.Count);
    for (var i = 0; i < first.Subscriptions.Count; i++) {
      await Assert.That(first.Subscriptions[i].Topic).IsEqualTo(second.Subscriptions[i].Topic);
      await Assert.That(first.Subscriptions[i].FilterExpression).IsEqualTo(second.Subscriptions[i].FilterExpression);
    }
  }

  [Test]
  public async Task Build_CatalogOrder_DoesNotLeakIntoManifestOrderAsync() {
    var outbox = new DomainTopicOutboxStrategy();
    var inbox = new SharedTopicInboxStrategy();
    var context = _context();
    var forward = new[] {
      _entry(typeof(OutboxTestTypes.Orders.Events.OrderCreated), "event"),
      _entry(typeof(OutboxTestTypes.Users.Events.UserCreated), "event")
    };
    var reversed = forward.Reverse().ToArray();

    var fromForward = TopologyManifestBuilder.Build(outbox, inbox, context, forward);
    var fromReversed = TopologyManifestBuilder.Build(outbox, inbox, context, reversed);

    await Assert.That(fromForward.PublishDestinations.SequenceEqual(fromReversed.PublishDestinations)).IsTrue()
      .Because("The manifest is a set projection — input enumeration order must not matter.");
    // Deterministic order: sorted by message type name
    await Assert.That(fromForward.PublishDestinations[0].MessageTypeName)
      .IsEqualTo(typeof(OutboxTestTypes.Orders.Events.OrderCreated).FullName!);
  }

  [Test]
  public async Task Build_DuplicateCatalogEntries_CollapseToOneAsync() {
    var outbox = new DomainTopicOutboxStrategy();
    var inbox = new SharedTopicInboxStrategy();
    var context = _context();
    var catalog = new[] {
      _entry(typeof(OutboxTestTypes.Orders.Events.OrderCreated), "event"),
      _entry(typeof(OutboxTestTypes.Orders.Events.OrderCreated), "event")
    };

    var manifest = TopologyManifestBuilder.Build(outbox, inbox, context, catalog);

    await Assert.That(manifest.PublishDestinations.Count).IsEqualTo(1);
  }

  #endregion

  #region Catalog filtering

  [Test]
  public async Task Build_PerspectiveEntries_AreNotPublishDestinationsAsync() {
    // Perspectives are projections, not published messages — they name no outbox entity.
    var outbox = new DomainTopicOutboxStrategy();
    var inbox = new SharedTopicInboxStrategy();
    var context = _context();
    var catalog = new[] {
      _entry(typeof(OutboxTestTypes.Orders.Events.OrderCreated), "event"),
      _entry(typeof(OutboxTestTypes.Users.Events.UserCreated), "perspective")
    };

    var manifest = TopologyManifestBuilder.Build(outbox, inbox, context, catalog);

    await Assert.That(manifest.PublishDestinations.Count).IsEqualTo(1);
    await Assert.That(manifest.PublishDestinations[0].MessageTypeName)
      .IsEqualTo(typeof(OutboxTestTypes.Orders.Events.OrderCreated).FullName!);
  }

  [Test]
  public async Task Build_EmptyCatalog_SubscriptionsStillPresentAsync() {
    var outbox = new SharedTopicOutboxStrategy();
    var inbox = new SharedTopicInboxStrategy();
    var context = _context("outboxtesttypes.orders.commands");

    var manifest = TopologyManifestBuilder.Build(outbox, inbox, context, []);

    await Assert.That(manifest.PublishDestinations).IsEmpty();
    await Assert.That(manifest.Subscriptions.Count).IsEqualTo(1);
    await Assert.That(manifest.Subscriptions[0].Topic).IsEqualTo("inbox");
  }

  #endregion

  #region Multi-subscription strategies

  [Test]
  public async Task Build_MultiSubscriptionStrategy_ListsEverySubscriptionAsync() {
    var outbox = new DomainTopicOutboxStrategy();
    var inbox = new TwoSubscriptionStrategy();
    var context = _context();

    var manifest = TopologyManifestBuilder.Build(outbox, inbox, context, []);

    await Assert.That(manifest.Subscriptions.Count).IsEqualTo(2);
    await Assert.That(manifest.Subscriptions.Select(s => s.Topic)).Contains("inbox.first");
    await Assert.That(manifest.Subscriptions.Select(s => s.Topic)).Contains("inbox.second");
  }

  private sealed class TwoSubscriptionStrategy : IInboxRoutingStrategy {
    public InboxSubscription GetSubscription(
        IReadOnlySet<string> ownedDomains, string serviceName, MessageKind kind)
      => new("inbox.first");

    public IReadOnlyList<InboxSubscription> GetSubscriptions(InboxSubscriptionContext context)
      => [new InboxSubscription("inbox.first"), new InboxSubscription("inbox.second")];
  }

  #endregion

  #region Provisioning surface (topology arc phase 5)

  [Test]
  public async Task Build_StampsServiceNameFromContextAsync() {
    // The provisioning path derives broker subscription/queue names from the service name
    // (ASB: ServiceBusSubscriptionNameHelper, RabbitMQ: "{service}-{exchange}") — the manifest
    // must carry it so ProvisionManifestAsync needs nothing beyond the manifest itself.
    var manifest = TopologyManifestBuilder.Build(
        new SharedTopicOutboxStrategy(), new SharedTopicInboxStrategy(), _context(), []);

    await Assert.That(manifest.ServiceName).IsEqualTo("OrderService");
  }

  [Test]
  public async Task DefaultSharedStrategy_Manifest_NamesExactlyTodaysEntitiesAsync() {
    // DARK guarantee: with the DEFAULT SharedTopicInboxStrategy the manifest names exactly
    // today's entities — one shared-inbox subscription and only strategy-named publish
    // destinations. Zero new entities for existing consumers.
    var outbox = new SharedTopicOutboxStrategy();
    var inbox = new SharedTopicInboxStrategy();
    var context = _context("outboxtesttypes.orders.commands");
    var catalog = new[] {
      _entry(typeof(OutboxTestTypes.Orders.Commands.CreateOrder), "command"),
      _entry(typeof(OutboxTestTypes.Orders.Events.OrderCreated), "event")
    };

    var manifest = TopologyManifestBuilder.Build(outbox, inbox, context, catalog);

    await Assert.That(manifest.Subscriptions.Count).IsEqualTo(1)
      .Because("the default topology has exactly one inbox subscription — the shared inbox");
    await Assert.That(manifest.Subscriptions[0].Topic).IsEqualTo("inbox");
    var topics = manifest.PublishDestinations.Select(d => d.Address).Distinct().ToList();
    await Assert.That(topics.Count).IsEqualTo(2);
    await Assert.That(topics).Contains("inbox");
    await Assert.That(topics).Contains("outboxtesttypes.orders.events");
    foreach (var topic in topics) {
      await Assert.That(topic.StartsWith("inbox.", StringComparison.Ordinal)).IsFalse()
        .Because("no per-namespace inbox entity may appear for a default-strategy consumer");
    }
  }

  [Test]
  public async Task NamespaceStrategy_Manifest_ListsPerNamespaceEntitiesAsync() {
    // Opt-in counterpart of the DARK guarantee: with NamespaceInboxStrategy the manifest lists
    // the per-namespace entities (they sit idle until phase 6 flips publishers).
    var inbox = new NamespaceInboxStrategy();
    var context = new InboxSubscriptionContext(
        "OrderService",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.commands" },
        [new Whizbang.Core.Messaging.HandledMessageInfo(
            "OutboxTestTypes.Orders.Commands.CreateOrder", "outboxtesttypes.orders.commands", MessageKind.Command)]);

    var manifest = TopologyManifestBuilder.Build(new SharedTopicOutboxStrategy(), inbox, context, []);

    var topics = manifest.Subscriptions.Select(s => s.Topic).ToList();
    await Assert.That(topics).Contains("inbox.outboxtesttypes.orders.commands");
    await Assert.That(topics).Contains(NamespaceInboxStrategy.SystemBroadcastInboxTopic);
    await Assert.That(topics).Contains("inbox");
  }

  #endregion

  #region Validation

  [Test]
  public async Task Build_NullOutboxStrategy_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => TopologyManifestBuilder.Build(
        null!, new SharedTopicInboxStrategy(), _context(), []))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Build_NullInboxStrategy_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => TopologyManifestBuilder.Build(
        new SharedTopicOutboxStrategy(), null!, _context(), []))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Build_NullContext_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => TopologyManifestBuilder.Build(
        new SharedTopicOutboxStrategy(), new SharedTopicInboxStrategy(), null!, []))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Build_NullCatalog_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => TopologyManifestBuilder.Build(
        new SharedTopicOutboxStrategy(), new SharedTopicInboxStrategy(), _context(), null!))
      .Throws<ArgumentNullException>();
  }

  #endregion
}
