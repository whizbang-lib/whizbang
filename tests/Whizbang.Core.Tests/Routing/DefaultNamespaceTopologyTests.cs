using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// THE DEFAULT TOPOLOGY (topology arc, default flip): a configuration-free consumer gets the
/// per-namespace command topology out of the box — <see cref="NamespaceInboxStrategy"/> +
/// <see cref="NamespaceOutboxStrategy"/>, every command namespace flipped, and the legacy
/// catch-all <c>inbox</c> RETIRED. The migration modes (legacy, mid-migration) are still fully
/// supported, but they are now the thing you ask for rather than the thing you get.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing safety property is LEGACY COHERENCE: an existing consumer that explicitly
/// selects a legacy strategy (<c>Inbox.UseSharedTopic</c> / <c>Outbox.UseSharedTopic</c> /
/// <c>UseDomainTopics</c> / <c>UseCustom</c>) must get a topology that still PROVISIONS its
/// shared inbox. Retirement excludes the shared entity from the manifest, and both transports'
/// provisioners provision only what the manifest names — so a leaked retirement flag would
/// silently stop creating the entity such a consumer receives on.
/// </para>
/// </remarks>
public class DefaultNamespaceTopologyTests {
  private static readonly IReadOnlySet<string> _noDomains =
    new HashSet<string>(StringComparer.OrdinalIgnoreCase);

  private static InboxSubscriptionContext _context(params string[] ownedDomains) =>
    new("order-service",
        new HashSet<string>(ownedDomains, StringComparer.OrdinalIgnoreCase),
        [new HandledMessageInfo(
          "OutboxTestTypes.Orders.Commands.CreateOrder",
          "outboxtesttypes.orders.commands",
          MessageKind.Command)]);

  private static MessageTypeCatalogEntry _entry(Type type, string kind) =>
    new(type, type.FullName!, kind, PinnedId: null);

  private static MessageTypeCatalogEntry[] _catalog() => [
    _entry(typeof(OutboxTestTypes.Orders.Commands.CreateOrder), "command"),
    _entry(typeof(OutboxTestTypes.Orders.Events.OrderCreated), "event")
  ];

  private static RoutingOptions _resolve(Action<RoutingOptions> configure) {
    var services = new ServiceCollection();
    new WhizbangBuilder(services).WithRouting(configure);
    using var provider = services.BuildServiceProvider();
    return provider.GetRequiredService<IOptions<RoutingOptions>>().Value;
  }

  #region The config-free consumer gets the clean end state

  [Test]
  public async Task ConfigFreeConsumer_ResolvesNamespaceStrategies_FullyFlippedAndRetiredAsync() {
    // THE HEADLINE: AddWhizbang().WithRouting() with no inbox/outbox call at all.
    var services = new ServiceCollection();
    new WhizbangBuilder(services).WithRouting(r => r.OwnDomains("outboxtesttypes.orders.commands"));

    using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(provider.GetRequiredService<IInboxRoutingStrategy>())
      .IsTypeOf<NamespaceInboxStrategy>();
    await Assert.That(provider.GetRequiredService<IOutboxRoutingStrategy>())
      .IsTypeOf<NamespaceOutboxStrategy>();
    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsTrue()
      .Because("the default flip set is the migration END state, not the starting line");
    await Assert.That(options.SharedInboxRetired).IsTrue()
      .Because("the catch-all inbox is retired out of the box — 3 broker ops per command, not ~42");
  }

  [Test]
  public async Task ConfigFreeConsumer_RetirementGuardPassesAsync() {
    // Internal consistency: the default end state must SATISFY the retirement guard. A default
    // that throws on first options resolution would break every config-free consumer.
    var services = new ServiceCollection();
    new WhizbangBuilder(services).WithRouting(_ => { });

    using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;
    await Assert.That(options.SharedInboxRetired).IsTrue();
  }

  [Test]
  public async Task ConfigFreeConsumer_SubscriptionSet_HasNoLegacyCatchAllAsync() {
    var options = _resolve(r => r.OwnDomains("outboxtesttypes.orders.commands"));

    var topics = options.InboxStrategy.GetSubscriptions(_context("outboxtesttypes.orders.commands"))
      .Select(s => s.Topic).ToList();

    await Assert.That(topics).Contains("inbox.outboxtesttypes.orders.commands");
    await Assert.That(topics).Contains(CommandInboxNaming.SystemBroadcastTopic);
    await Assert.That(topics).DoesNotContain("inbox")
      .Because("the default subscription set is the clean end state — no transitional catch-all");
  }

  [Test]
  public async Task ConfigFreeConsumer_Manifest_ContainsNoLegacyInboxEntityAsync() {
    // The manifest is the provisioning authority on both transports: what it does not name is
    // not created. Under the default topology it must name no legacy shared entity at all.
    var options = _resolve(_ => { });

    var manifest = TopologyManifestBuilder.Build(
      options.OutboxStrategy, options.InboxStrategy, _context(), _catalog());

    foreach (var entity in manifest.PublishDestinations.Select(d => d.Address)
        .Concat(manifest.Subscriptions.Select(s => s.Topic))) {
      await Assert.That(entity).IsNotEqualTo("inbox")
        .Because("a default-configured service must never provision or publish to the catch-all");
    }
    await Assert.That(manifest.PublishDestinations.Select(d => d.Address))
      .Contains("inbox.outboxtesttypes.orders.commands");
    await Assert.That(manifest.Subscriptions.Select(s => s.Topic))
      .Contains("inbox.outboxtesttypes.orders.commands");
  }

  [Test]
  public async Task ConfigFreeConsumer_ManifestThroughDi_ContainsNoLegacyInboxEntityAsync() {
    // Same lock through the REAL registration path the consumer worker uses.
    var services = new ServiceCollection();
    new WhizbangBuilder(services).WithRouting(_ => { });
    services.AddTransportSubscriptionBuilder("order-service");

    using var provider = services.BuildServiceProvider();
    var manifest = provider.GetRequiredService<TopologyManifest>();

    await Assert.That(manifest.Subscriptions.Select(s => s.Topic)).DoesNotContain("inbox");
    await Assert.That(manifest.Subscriptions.Select(s => s.Topic))
      .Contains(CommandInboxNaming.SystemBroadcastTopic);
  }

  #endregion

  #region Legacy coherence — an explicit legacy strategy clears the flip/retirement flags

  [Test]
  public async Task ExplicitLegacyStrategies_AreByteIdenticalToTodaysSharedTopologyAsync() {
    // THE UPGRADE LOCK: the configuration existing consumers already ship. Same strategies,
    // same subscription set, same destinations — and retirement OFF, so the shared inbox is
    // still named by the manifest and therefore still provisioned.
    var options = _resolve(r => {
      r.OwnDomains("outboxtesttypes.orders.commands");
      r.Inbox.UseSharedTopic("inbox");
      r.Outbox.UseSharedTopic("inbox");
    });

    await Assert.That(options.InboxStrategy).IsTypeOf<SharedTopicInboxStrategy>();
    await Assert.That(options.OutboxStrategy).IsTypeOf<SharedTopicOutboxStrategy>();
    await Assert.That(options.SharedInboxRetired).IsFalse()
      .Because("retiring the entity a legacy consumer receives on would break them silently");
    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsFalse()
      .Because("a legacy consumer's publishers must keep targeting the shared inbox");

    var context = _context("outboxtesttypes.orders.commands");
    var referenceInbox = new SharedTopicInboxStrategy("inbox");
    var referenceOutbox = new SharedTopicOutboxStrategy("inbox");

    var subscriptions = options.InboxStrategy.GetSubscriptions(context);
    var reference = referenceInbox.GetSubscriptions(context);
    await Assert.That(subscriptions.Count).IsEqualTo(reference.Count);
    await Assert.That(subscriptions[0].Topic).IsEqualTo(reference[0].Topic);
    await Assert.That(subscriptions[0].FilterExpression).IsEqualTo(reference[0].FilterExpression);

    foreach (var kind in new[] { MessageKind.Command, MessageKind.Event }) {
      var type = kind == MessageKind.Command
        ? typeof(OutboxTestTypes.Orders.Commands.CreateOrder)
        : typeof(OutboxTestTypes.Orders.Events.OrderCreated);
      var actual = options.OutboxStrategy.GetDestination(type, context.OwnedDomains, kind);
      var expected = referenceOutbox.GetDestination(type, context.OwnedDomains, kind);
      await Assert.That(actual.Address).IsEqualTo(expected.Address);
      await Assert.That(actual.RoutingKey).IsEqualTo(expected.RoutingKey);
    }
  }

  [Test]
  public async Task ExplicitLegacyStrategies_ManifestStillProvisionsTheSharedInboxAsync() {
    // The concrete failure this prevents: retirement excludes the shared entity from the
    // manifest, and provisioners create only what the manifest names.
    var options = _resolve(r => {
      r.Inbox.UseSharedTopic("inbox");
      r.Outbox.UseSharedTopic("inbox");
    });

    var manifest = TopologyManifestBuilder.Build(
      options.OutboxStrategy, options.InboxStrategy, _context(), _catalog());

    await Assert.That(manifest.Subscriptions.Select(s => s.Topic)).Contains("inbox")
      .Because("the legacy consumer's inbox must still be created on upgrade");
    await Assert.That(manifest.PublishDestinations.Select(d => d.Address)).Contains("inbox");
  }

  [Test]
  public async Task ExplicitLegacyInboxAlone_ClearsFlipAndRetirementAsync() {
    var options = _resolve(r => r.Inbox.UseSharedTopic("inbox"));

    await Assert.That(options.SharedInboxRetired).IsFalse();
    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsFalse()
      .Because("publishers must not flip to entities this service's catch-all subscription cannot cover");
  }

  [Test]
  public async Task ExplicitLegacyOutboxAlone_ClearsFlipAndRetirementAsync() {
    var options = _resolve(r => r.Outbox.UseSharedTopic("inbox"));

    await Assert.That(options.SharedInboxRetired).IsFalse();
    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsFalse();

    // …and the inbox side (still the default namespace strategy) keeps the transitional
    // shared subscription, so a legacy publisher's commands are still received.
    await Assert.That(options.InboxStrategy.GetSubscriptions(_context()).Select(s => s.Topic))
      .Contains("inbox");
  }

  [Test]
  public async Task UseDomainTopics_EitherSide_ClearsFlipAndRetirementAsync() {
    var inboxSide = _resolve(r => r.Inbox.UseDomainTopics());
    var outboxSide = _resolve(r => r.Outbox.UseDomainTopics());

    await Assert.That(inboxSide.SharedInboxRetired).IsFalse();
    await Assert.That(inboxSide.AllCommandNamespacesRouteToInbox).IsFalse();
    await Assert.That(outboxSide.SharedInboxRetired).IsFalse();
    await Assert.That(outboxSide.AllCommandNamespacesRouteToInbox).IsFalse()
      .Because("DomainTopicOutboxStrategy has no flip seam — a flipped set would be a lie");
  }

  [Test]
  public async Task UseCustom_EitherSide_ClearsFlipAndRetirementAsync() {
    // A custom strategy cannot be assumed to subscribe to per-namespace inboxes or to honor
    // the flip, so the safe default is the transitional superset.
    var inboxSide = _resolve(r => r.Inbox.UseCustom(new SharedTopicInboxStrategy("custom")));
    var outboxSide = _resolve(r => r.Outbox.UseCustom(new DomainTopicOutboxStrategy()));

    await Assert.That(inboxSide.SharedInboxRetired).IsFalse();
    await Assert.That(inboxSide.AllCommandNamespacesRouteToInbox).IsFalse();
    await Assert.That(outboxSide.SharedInboxRetired).IsFalse();
    await Assert.That(outboxSide.AllCommandNamespacesRouteToInbox).IsFalse();
  }

  [Test]
  public async Task ExplicitFlipCalls_WinOverTheLegacyStrategyClearingAsync() {
    // Clearing only ever overrides the DEFAULT: an explicit opt-in stays honored regardless of
    // call order, so a consumer mid-migration on a legacy subscription can still flip publishers.
    var before = _resolve(r => {
      r.RouteAllCommandNamespacesToInbox();
      r.Inbox.UseSharedTopic("inbox");
    });
    var after = _resolve(r => {
      r.Inbox.UseSharedTopic("inbox");
      r.RouteAllCommandNamespacesToInbox();
    });

    await Assert.That(before.AllCommandNamespacesRouteToInbox).IsTrue();
    await Assert.That(after.AllCommandNamespacesRouteToInbox).IsTrue();
  }

  [Test]
  public async Task NamespaceStrategySelection_KeepsTheDefaultEndStateAsync() {
    // Opting INTO the namespace strategies is not a legacy selection — it must not clear.
    var options = _resolve(r => {
      r.Inbox.UseNamespaceInboxes();
      r.Outbox.UseNamespaceRouting();
    });

    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsTrue();
    await Assert.That(options.SharedInboxRetired).IsTrue();
  }

  #endregion

  #region Mid-migration — the transitional superset is still reachable

  [Test]
  public async Task MidMigration_PerNamespaceFlip_KeepsTheTransitionalSupersetAsync() {
    // Naming a namespace explicitly means "I am managing the flip set myself" — that is the
    // migrate-one-at-a-time API, so the all-flip default steps aside and retirement (which is
    // only valid under a full flip) follows it.
    var options = _resolve(r => r.RouteCommandNamespaceToInbox("outboxtesttypes.orders.commands"));

    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsFalse();
    await Assert.That(options.SharedInboxRetired).IsFalse();
    await Assert.That(options.IsCommandNamespaceRoutedToInbox("outboxtesttypes.orders.commands")).IsTrue();
    await Assert.That(options.IsCommandNamespaceRoutedToInbox("outboxtesttypes.users.commands")).IsFalse();

    var topics = options.InboxStrategy.GetSubscriptions(_context()).Select(s => s.Topic).ToList();
    await Assert.That(topics).Contains("inbox")
      .Because("mid-migration is a strict SUPERSET — unflipped namespaces still ride the catch-all");
    await Assert.That(topics).Contains("inbox.outboxtesttypes.orders.commands");
  }

  [Test]
  public async Task MidMigration_ExplicitInverses_RollBackToThePreMigrationStateAsync() {
    var options = _resolve(r => {
      r.RouteNoCommandNamespacesToInbox();
      r.KeepSharedInbox();
    });

    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsFalse();
    await Assert.That(options.SharedInboxRetired).IsFalse();
    await Assert.That(options.OutboxStrategy.GetDestination(
        typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.Command).Address)
      .IsEqualTo("inbox")
      .Because("a fully rolled-back namespace strategy publishes byte-identically to the legacy shared inbox");
  }

  [Test]
  public async Task RetirementGuard_StillThrowsForTheIncoherentCombinationAsync() {
    // Retirement ON with the flip incomplete is still silent loss — the guard must survive the
    // default change (it can only be reached now by asking for both explicitly).
    var services = new ServiceCollection();
    new WhizbangBuilder(services).WithRouting(r => {
      r.RouteCommandNamespaceToInbox("outboxtesttypes.orders.commands");
      r.RetireSharedInbox();
    });

    using var provider = services.BuildServiceProvider();

    var exception = Assert.Throws<InvalidOperationException>(
      () => provider.GetRequiredService<IOptions<RoutingOptions>>());
    await Assert.That(exception!.Message).Contains("RouteAllCommandNamespacesToInbox");
  }

  #endregion

  #region Configuration-driven rollback of the new defaults

  [Test]
  public async Task Configuration_RouteAllFalse_RollsBackTheDefaultFlipAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:RouteAllCommandNamespacesToInbox"] = "false"
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    new WhizbangBuilder(services).WithRouting(_ => { });

    using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsFalse();
    await Assert.That(options.SharedInboxRetired).IsFalse()
      .Because("retirement follows the flip by default, so rolling back the flip cannot strand it "
             + "in the guard-throwing combination");
  }

  [Test]
  public async Task Configuration_RetireSharedInboxFalse_KeepsTheTransitionalSubscriptionAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:RetireSharedInbox"] = "false"
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    new WhizbangBuilder(services).WithRouting(_ => { });

    using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.SharedInboxRetired).IsFalse();
    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsTrue()
      .Because("un-retiring alone keeps the full flip — this is the dual-delivery rollback rung");
    await Assert.That(options.InboxStrategy.GetSubscriptions(_context()).Select(s => s.Topic))
      .Contains("inbox");
  }

  [Test]
  public async Task Configuration_ExplicitFlipList_ReplacesTheAllFlipDefaultAsync() {
    // Naming namespaces in configuration is the same statement as naming them in code.
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:CommandNamespacesToInbox:0"] = "outboxtesttypes.orders.commands"
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    new WhizbangBuilder(services).WithRouting(_ => { });

    using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsFalse();
    await Assert.That(options.SharedInboxRetired).IsFalse();
    await Assert.That(options.IsCommandNamespaceRoutedToInbox("outboxtesttypes.orders.commands")).IsTrue();
  }

  #endregion
}
