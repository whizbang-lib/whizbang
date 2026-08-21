using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for <see cref="NamespaceInboxStrategy"/> (topology arc phase 5, spec migration step 1):
/// one subscription per DISTINCT handled command contract namespace (<c>inbox.&lt;ns&gt;</c>),
/// plus the system broadcast inbox (<c>inbox.whizbang</c>), plus — transitionally, until the
/// shared-inbox retirement — today's shared-inbox subscription unchanged (strict superset of
/// the shared strategy; publishers still send everything to the shared inbox, the new entities
/// sit DARK).
/// </summary>
public class NamespaceInboxStrategyTests {
  private static InboxSubscriptionContext _context(
      IReadOnlyList<HandledMessageInfo>? handled = null,
      IReadOnlySet<string>? consumedEventNamespaces = null,
      params string[] ownedDomains) =>
    new("OrderService",
        new HashSet<string>(ownedDomains, StringComparer.OrdinalIgnoreCase),
        handled ?? []) {
      ConsumedEventNamespaces = consumedEventNamespaces ?? new HashSet<string>()
    };

  private static HandledMessageInfo _command(string typeName, string contractNamespace) =>
    new(typeName, contractNamespace, MessageKind.Command);

  #region Per-namespace derivation

  [Test]
  public async Task GetSubscriptions_HandledCommandNamespaces_OnePerDistinctNamespaceAsync() {
    // Arrange - commands in two namespaces, plus an event and a query that must NOT create inboxes
    var strategy = new NamespaceInboxStrategy();
    var context = _context(handled: [
      _command("MyApp.Orders.Commands.CreateOrder", "myapp.orders.commands"),
      _command("MyApp.Billing.Commands.IssueInvoice", "myapp.billing.commands"),
      new("MyApp.Orders.Events.OrderCreated", "myapp.orders.events", MessageKind.Event),
      new("MyApp.Orders.Queries.GetOrder", "myapp.orders.queries", MessageKind.Query),
    ]);

    // Act
    var subscriptions = strategy.GetSubscriptions(context);

    // Assert - one inbox per distinct COMMAND namespace, nothing for events/queries
    var topics = subscriptions.Select(s => s.Topic).ToList();
    await Assert.That(topics).Contains("inbox.myapp.orders.commands");
    await Assert.That(topics).Contains("inbox.myapp.billing.commands");
    await Assert.That(topics).DoesNotContain("inbox.myapp.orders.events")
      .Because("events ride domain topics, not command inboxes");
    await Assert.That(topics).DoesNotContain("inbox.myapp.orders.queries")
      .Because("only Kind==Command derives a per-namespace inbox in this phase");
  }

  [Test]
  public async Task GetSubscriptions_DuplicateCommandNamespace_DeduplicatesAsync() {
    // Arrange - two commands from the SAME namespace
    var strategy = new NamespaceInboxStrategy();
    var context = _context(handled: [
      _command("MyApp.Orders.Commands.CreateOrder", "myapp.orders.commands"),
      _command("MyApp.Orders.Commands.CancelOrder", "myapp.orders.commands"),
    ]);

    // Act
    var subscriptions = strategy.GetSubscriptions(context);

    // Assert
    var matching = subscriptions.Where(s => s.Topic == "inbox.myapp.orders.commands").ToList();
    await Assert.That(matching.Count).IsEqualTo(1)
      .Because("the subscription set is per DISTINCT contract namespace, not per command type");
  }

  [Test]
  public async Task GetSubscriptions_MixedCaseNamespace_LowercasesTopicAsync() {
    // Arrange - defensive casing lock: registries emit lowercase, but a hand-built
    // HandledMessageInfo must not produce a case-variant broker entity
    var strategy = new NamespaceInboxStrategy();
    var context = _context(handled: [
      _command("MyApp.Orders.Commands.CreateOrder", "MyApp.Orders.Commands"),
    ]);

    // Act
    var subscriptions = strategy.GetSubscriptions(context);

    // Assert
    await Assert.That(subscriptions.Select(s => s.Topic)).Contains("inbox.myapp.orders.commands");
    await Assert.That(subscriptions.Select(s => s.Topic)).DoesNotContain("inbox.MyApp.Orders.Commands");
  }

  [Test]
  public async Task GetSubscriptions_PerNamespaceSubscriptions_CarryOwnedCommandInboxMarkerAsync() {
    // The provisioners key the startup ownership-drift check on this marker — it must be on
    // every per-namespace inbox and on NOTHING else (system/shared subscriptions are shared
    // entities, not exclusively owned).
    var strategy = new NamespaceInboxStrategy();
    var context = _context(
      handled: [_command("MyApp.Orders.Commands.CreateOrder", "myapp.orders.commands")],
      consumedEventNamespaces: null,
      "myapp.orders.commands");

    var subscriptions = strategy.GetSubscriptions(context);

    var perNamespace = subscriptions.Single(s => s.Topic == "inbox.myapp.orders.commands");
    await Assert.That(perNamespace.Metadata!.ContainsKey(NamespaceInboxStrategy.OwnedCommandInboxMetadataKey)).IsTrue();
    await Assert.That((bool)perNamespace.Metadata![NamespaceInboxStrategy.OwnedCommandInboxMetadataKey]).IsTrue();
    foreach (var other in subscriptions.Where(s => s.Topic != "inbox.myapp.orders.commands")) {
      var marked = other.Metadata?.ContainsKey(NamespaceInboxStrategy.OwnedCommandInboxMetadataKey) == true;
      await Assert.That(marked).IsFalse()
        .Because($"'{other.Topic}' is a shared entity — the ownership drift check must not fire on it");
    }
  }

  #endregion

  #region System broadcast inbox

  [Test]
  public async Task GetSubscriptions_AlwaysIncludesSystemBroadcastInboxAsync() {
    // Arrange
    var strategy = new NamespaceInboxStrategy();
    var context = _context(handled: [
      _command("MyApp.Orders.Commands.CreateOrder", "myapp.orders.commands"),
    ]);

    // Act
    var subscriptions = strategy.GetSubscriptions(context);

    // Assert - the system inbox carries the same three patterns the shared inbox carries today
    var system = subscriptions.Single(s => s.Topic == NamespaceInboxStrategy.SystemBroadcastInboxTopic);
    var patterns = (IReadOnlyList<string>)system.Metadata!["RoutingPatterns"];
    await Assert.That(patterns).Contains("whizbang.core.commands.system.#");
    await Assert.That(patterns).Contains("whizbang.core.messaging.#");
    await Assert.That(patterns).Contains("whizbang.core.minting.#");
    await Assert.That(patterns.Count).IsEqualTo(3)
      .Because("no owned-domain pattern belongs on the broadcast inbox — those are per-namespace now");
  }

  [Test]
  public async Task GetSubscriptions_EmptyRegistry_SystemInboxAndTransitionalSharedOnlyAsync() {
    // A service with no receptors (pure event consumer / BFF) still subscribes to the system
    // broadcast inbox and — transitionally — the shared inbox. Nothing else.
    var strategy = new NamespaceInboxStrategy();
    var context = _context();

    var subscriptions = strategy.GetSubscriptions(context);

    await Assert.That(subscriptions.Count).IsEqualTo(2);
    await Assert.That(subscriptions.Select(s => s.Topic))
      .Contains(NamespaceInboxStrategy.SystemBroadcastInboxTopic);
    await Assert.That(subscriptions.Select(s => s.Topic)).Contains("inbox");
  }

  [Test]
  public async Task GetSubscriptions_FrameworkReservedCommandNamespaces_NeverGetPerNamespaceInboxesAsync() {
    // Broadcast/control/minted envelope types never route to per-namespace inboxes — the whole
    // whizbang.core contract subtree rides the system broadcast inbox patterns instead.
    var strategy = new NamespaceInboxStrategy();
    var context = _context(handled: [
      _command("Whizbang.Core.Commands.System.RebuildPerspective", "whizbang.core.commands.system"),
      new("Whizbang.Core.Messaging.IntegrityManifestRequest", "whizbang.core.messaging", MessageKind.Command),
      new("Whizbang.Core.Minting.RedeliveryComposite", "whizbang.core.minting", MessageKind.Command),
    ]);

    var subscriptions = strategy.GetSubscriptions(context);

    foreach (var topic in subscriptions.Select(s => s.Topic)) {
      await Assert.That(topic.StartsWith("inbox.whizbang.core", StringComparison.Ordinal)).IsFalse()
        .Because("framework-reserved namespaces ride the system broadcast inbox, never a dedicated entity");
    }
    // ...and the system inbox patterns admit every one of those namespaces.
    var system = subscriptions.Single(s => s.Topic == NamespaceInboxStrategy.SystemBroadcastInboxTopic);
    var patterns = (IReadOnlyList<string>)system.Metadata!["RoutingPatterns"];
    foreach (var reserved in new[] { "whizbang.core.commands.system", "whizbang.core.messaging", "whizbang.core.minting" }) {
      var admitted = patterns.Any(p =>
        p.EndsWith(".#", StringComparison.Ordinal)
        && $"{reserved}.sometype".StartsWith(p[..^1], StringComparison.Ordinal));
      await Assert.That(admitted).IsTrue()
        .Because($"'{reserved}' subjects must be broker-admissible on the system broadcast inbox");
    }
  }

  [Test]
  public async Task GetSubscriptions_MintedCompositeSubjects_RideSystemBroadcastPatternsAsync() {
    // Composite/raw-carry lock (the spec's flagged highest-risk mapping): the minted composite
    // ENVELOPE types publish under whizbang.core.minting.* subjects — the system broadcast
    // inbox must admit every one of them, and no per-namespace inbox may claim them.
    var strategy = new NamespaceInboxStrategy();
    var subscriptions = strategy.GetSubscriptions(_context());
    var system = subscriptions.Single(s => s.Topic == NamespaceInboxStrategy.SystemBroadcastInboxTopic);
    var patterns = (IReadOnlyList<string>)system.Metadata!["RoutingPatterns"];
    Type[] mintedFamilies = [
      typeof(Whizbang.Core.Minting.RedeliveryComposite),
      typeof(Whizbang.Core.Minting.CoalescedEventsComposite),
      typeof(Whizbang.Core.Minting.AuditEventsComposite),
    ];

    foreach (var family in mintedFamilies) {
      var destination = Whizbang.Core.Transports.ControlPlaneDestination.For("inbox", Guid.NewGuid(), family);
      var admitted = patterns.Any(p =>
        p.EndsWith(".#", StringComparison.Ordinal)
        && destination.RoutingKey!.StartsWith(p[..^1], StringComparison.Ordinal));
      await Assert.That(admitted).IsTrue()
        .Because($"subject '{destination.RoutingKey}' must match a system-inbox routing pattern "
               + "or repair bundles are silently dropped by the broker rule");
    }
  }

  #endregion

  #region Superset-of-today lock

  [Test]
  public async Task GetSubscriptions_ContainsTodaysSharedSubscriptionUnchangedAsync() {
    // Transitional guarantee (retires with the shared inbox, phase 7): for a service with owned
    // domains the returned set contains EXACTLY today's shared-inbox subscription — same topic,
    // same filter, same routing patterns, bit for bit.
    var strategy = new NamespaceInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
      "myapp.orders.commands", "myapp.billing.*"
    };
    var context = new InboxSubscriptionContext("OrderService", ownedDomains, [
      _command("MyApp.Orders.Commands.CreateOrder", "myapp.orders.commands"),
    ]);
    var today = new SharedTopicInboxStrategy()
      .GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    var subscriptions = strategy.GetSubscriptions(context);

    var shared = subscriptions.Single(s => s.Topic == today.Topic);
    await Assert.That(shared.FilterExpression).IsEqualTo(today.FilterExpression);
    var todayPatterns = (IReadOnlyList<string>)today.Metadata!["RoutingPatterns"];
    var sharedPatterns = (IReadOnlyList<string>)shared.Metadata!["RoutingPatterns"];
    await Assert.That(sharedPatterns.SequenceEqual(todayPatterns)).IsTrue()
      .Because("phase 5 is additive — the shared-inbox subscription must be a bit-identical "
             + "superset component until the publisher flip");
  }

  [Test]
  public async Task GetSubscriptions_CustomSharedTopic_TransitionalPartUsesItAsync() {
    var strategy = new NamespaceInboxStrategy("custom.inbox");

    var subscriptions = strategy.GetSubscriptions(_context());

    await Assert.That(subscriptions.Select(s => s.Topic)).Contains("custom.inbox");
  }

  [Test]
  public async Task GetSubscription_Singular_IsTodaysSharedSubscriptionAsync() {
    // The legacy singular surface cannot express a multi-subscription set — it answers with
    // the transitional shared subscription so pre-plural consumers keep today's behavior.
    var strategy = new NamespaceInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.commands" };
    var today = new SharedTopicInboxStrategy()
      .GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    var singular = strategy.GetSubscription(ownedDomains, "OrderService", MessageKind.Command);

    await Assert.That(singular.Topic).IsEqualTo(today.Topic);
    await Assert.That(singular.FilterExpression).IsEqualTo(today.FilterExpression);
  }

  #endregion

  #region Consumed-namespace surface (composite/raw-carry routing)

  [Test]
  public async Task ComputeConsumedNamespaces_UnionsHandledAndConsumedEventNamespacesAsync() {
    // "A composite must route to every service that handles any constituent's namespace" —
    // receptor enumeration alone misses perspective-consumed events (phase-3 review flag), so
    // the consumed-namespace surface unions the handled-message namespaces with the
    // EventSubscriptionDiscovery-fed consumed event namespaces.
    var context = _context(
      handled: [_command("MyApp.Orders.Commands.CreateOrder", "MyApp.Orders.Commands")],
      consumedEventNamespaces: new HashSet<string> { "MyApp.Payments.Events" });

    var consumed = NamespaceInboxStrategy.ComputeConsumedNamespaces(context);

    await Assert.That(consumed).Contains("myapp.orders.commands");
    await Assert.That(consumed).Contains("myapp.payments.events")
      .Because("perspective-consumed event namespaces are constituent sources even when no "
             + "receptor handles the type — the registry surface widened for exactly this");
  }

  [Test]
  public async Task InboxSubscriptionContext_ConsumedEventNamespaces_DefaultsToEmptyAsync() {
    var context = new InboxSubscriptionContext("svc", new HashSet<string>(), []);

    await Assert.That(context.ConsumedEventNamespaces.Count).IsEqualTo(0)
      .Because("pre-phase-5 construction sites must keep compiling with an empty (not null) set");
  }

  #endregion

  #region Shared-inbox retirement (topology arc phase 7)

  private static RoutingOptions _retiredOptions() =>
    new RoutingOptions().RouteAllCommandNamespacesToInbox().RetireSharedInbox();

  [Test]
  public async Task GetSubscriptions_UnderRetirement_ExactlyPerNamespaceInboxesPlusBroadcastAsync() {
    // THE RETIREMENT SHAPE: the transitional shared-inbox subscription is DROPPED — the set
    // is exactly one inbox per handled command namespace + the system broadcast inbox.
    // No catch-all remnant.
    var strategy = new NamespaceInboxStrategy(_retiredOptions());
    var context = _context(handled: [
      _command("MyApp.Orders.Commands.CreateOrder", "myapp.orders.commands"),
      _command("MyApp.Billing.Commands.IssueInvoice", "myapp.billing.commands"),
    ]);

    var subscriptions = strategy.GetSubscriptions(context);

    var topics = subscriptions.Select(s => s.Topic).ToList();
    await Assert.That(topics.Count).IsEqualTo(3);
    await Assert.That(topics).Contains("inbox.myapp.orders.commands");
    await Assert.That(topics).Contains("inbox.myapp.billing.commands");
    await Assert.That(topics).Contains(NamespaceInboxStrategy.SystemBroadcastInboxTopic);
    await Assert.That(topics).DoesNotContain("inbox")
      .Because("retirement drops the transitional shared-inbox subscription — the migration is complete");
  }

  [Test]
  public async Task GetSubscriptions_UnderRetirement_NoReceptors_BroadcastInboxOnlyAsync() {
    // A pure event consumer under retirement holds exactly ONE inbox subscription: the
    // system broadcast inbox (the sole carve-out).
    var strategy = new NamespaceInboxStrategy(_retiredOptions());

    var subscriptions = strategy.GetSubscriptions(_context());

    await Assert.That(subscriptions.Count).IsEqualTo(1);
    await Assert.That(subscriptions[0].Topic).IsEqualTo(NamespaceInboxStrategy.SystemBroadcastInboxTopic);
  }

  [Test]
  public async Task GetSubscriptions_UnderRetirement_FrameworkReservedStillNeverGetsPerNamespaceInboxesAsync() {
    // The phase-5 lock, extended to the retirement shape: broadcast/control/minted types
    // ride the system broadcast inbox and NOWHERE else — with the shared inbox gone there
    // is no other admissible path.
    var strategy = new NamespaceInboxStrategy(_retiredOptions());
    var context = _context(handled: [
      _command("Whizbang.Core.Commands.System.RebuildPerspective", "whizbang.core.commands.system"),
      new("Whizbang.Core.Minting.RedeliveryComposite", "whizbang.core.minting", MessageKind.Command),
      _command("MyApp.Orders.Commands.CreateOrder", "myapp.orders.commands"),
    ]);

    var subscriptions = strategy.GetSubscriptions(context);

    foreach (var topic in subscriptions.Select(s => s.Topic)) {
      await Assert.That(topic.StartsWith("inbox.whizbang.core", StringComparison.Ordinal)).IsFalse();
    }
    var system = subscriptions.Single(s => s.Topic == NamespaceInboxStrategy.SystemBroadcastInboxTopic);
    var patterns = (IReadOnlyList<string>)system.Metadata!["RoutingPatterns"];
    await Assert.That(patterns).Contains("whizbang.core.commands.system.#");
    await Assert.That(patterns).Contains("whizbang.core.minting.#")
      .Because("under retirement the broadcast inbox is the ONLY path for system/minted subjects");
  }

  [Test]
  public async Task GetSubscriptions_RetirementWithoutFullFlip_ThrowsTheRetirementGuardAsync() {
    // Defense in depth for manually constructed options: the same guard WithRouting enforces
    // at startup fires here — dropping the shared subscription while any namespace still
    // publishes to the shared inbox would be silent loss.
    var options = new RoutingOptions()
      .RouteCommandNamespaceToInbox("myapp.orders.commands")
      .RetireSharedInbox();
    var strategy = new NamespaceInboxStrategy(options);

    var exception = Assert.Throws<InvalidOperationException>(
      () => strategy.GetSubscriptions(_context()));
    await Assert.That(exception!.Message).Contains("RouteAllCommandNamespacesToInbox");
  }

  [Test]
  public async Task GetSubscription_Singular_UnderRetirement_ThrowsAsync() {
    // The singular surface can only answer with the transitional shared subscription — under
    // retirement that entity no longer exists for this service, so answering would recreate
    // the catch-all. Loud failure instead; consumers use the plural seam.
    var strategy = new NamespaceInboxStrategy(_retiredOptions());

    var exception = Assert.Throws<InvalidOperationException>(
      () => strategy.GetSubscription(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), "OrderService", MessageKind.Command));
    await Assert.That(exception!.Message).Contains("GetSubscriptions");
  }

  [Test]
  public async Task Inbox_UseNamespaceInboxes_BindsParentOptionsSoRetirementIsConsultedLiveAsync() {
    // The builder hands the strategy its parent RoutingOptions — a configuration-bound
    // retirement (applied on options resolution) is visible without re-registration.
    var options = new RoutingOptions();
    options.Inbox.UseNamespaceInboxes();
    options.RouteAllCommandNamespacesToInbox().RetireSharedInbox();

    var subscriptions = options.InboxStrategy.GetSubscriptions(_context());

    await Assert.That(subscriptions.Select(s => s.Topic)).DoesNotContain("inbox")
      .Because("the strategy consults the SAME options instance the config binder mutates");
  }

  [Test]
  public async Task GetSubscriptions_FullFlipButNotRetired_TransitionalSharedStillPresentAsync() {
    // The dual-delivery rollback rung: publishers fully flipped, but the receiver explicitly
    // KEEPS the catch-all. (Retirement follows the flip by default since the topology arc's
    // default flip, so this state is now asked for rather than inherited.)
    var options = new RoutingOptions().RouteAllCommandNamespacesToInbox().KeepSharedInbox();
    var strategy = new NamespaceInboxStrategy(options);

    var subscriptions = strategy.GetSubscriptions(_context());

    await Assert.That(subscriptions.Select(s => s.Topic)).Contains("inbox");
  }

  #endregion

  #region Validation and registration

  [Test]
  public async Task GetSubscriptions_NullContext_ThrowsArgumentNullExceptionAsync() {
    var strategy = new NamespaceInboxStrategy();

    await Assert.That(() => strategy.GetSubscriptions(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ComputeConsumedNamespaces_NullContext_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => NamespaceInboxStrategy.ComputeConsumedNamespaces(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Inbox_UseNamespaceInboxes_SetsNamespaceStrategyAsync() {
    var options = new RoutingOptions();

    options.Inbox.UseNamespaceInboxes();

    await Assert.That(options.InboxStrategy).IsTypeOf<NamespaceInboxStrategy>();
  }

  [Test]
  public async Task RoutingOptions_Default_IsNamespaceInboxStrategy_FullyRetiredAsync() {
    // DEFAULT LOCK (changed by the topology arc's default flip — this asserted the legacy
    // SharedTopicInboxStrategy while namespace inboxes were opt-in): a config-free consumer
    // gets the per-namespace topology with no catch-all remnant.
    var options = new RoutingOptions();

    await Assert.That(options.InboxStrategy).IsTypeOf<NamespaceInboxStrategy>();
    await Assert.That(options.SharedInboxRetired).IsTrue();
    await Assert.That(options.InboxStrategy.GetSubscriptions(_context()).Select(s => s.Topic))
      .DoesNotContain("inbox");
  }

  #endregion
}
