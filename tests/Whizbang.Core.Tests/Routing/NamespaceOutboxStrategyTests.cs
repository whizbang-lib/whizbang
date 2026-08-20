using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// The publisher flip (topology arc phase 6): NamespaceOutboxStrategy routes events to
/// domain topics (byte-identical to today's default), FLIPPED command namespaces to their
/// per-namespace inbox entity, UNFLIPPED command namespaces byte-identically to the legacy
/// shared inbox, and System traffic to the system broadcast inbox. The flip set lives on
/// RoutingOptions (repeatable per-namespace calls, an all-namespaces switch, and a
/// configuration binding) and is consulted LIVE so rollback needs no re-registration.
/// </summary>
public class NamespaceOutboxStrategyTests {
  private static readonly IReadOnlySet<string> _noDomains =
    new HashSet<string>(StringComparer.OrdinalIgnoreCase);

  private static NamespaceOutboxStrategy _strategy(
      out RoutingOptions options, string sharedInboxTopic = "inbox") {
    options = new RoutingOptions();
    return new NamespaceOutboxStrategy(options, sharedInboxTopic);
  }

  #region RoutingOptions flip set

  [Test]
  public async Task FlipSet_Defaults_EmptyAndAllOffAsync() {
    var options = new RoutingOptions();

    await Assert.That(options.CommandNamespacesToInbox).IsEmpty();
    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsFalse();
    await Assert.That(options.IsCommandNamespaceRoutedToInbox("myapp.orders.commands")).IsFalse();
  }

  [Test]
  public async Task RouteCommandNamespaceToInbox_IsRepeatableAndLowercasesAsync() {
    var options = new RoutingOptions();

    var result = options
      .RouteCommandNamespaceToInbox("MyApp.Orders.Commands")
      .RouteCommandNamespaceToInbox("myapp.billing.commands")
      .RouteCommandNamespaceToInbox("MyApp.Orders.Commands"); // repeat is a no-op

    await Assert.That(result).IsSameReferenceAs(options);
    await Assert.That(options.CommandNamespacesToInbox.Count).IsEqualTo(2);
    await Assert.That(options.CommandNamespacesToInbox).Contains("myapp.orders.commands");
    await Assert.That(options.IsCommandNamespaceRoutedToInbox("MYAPP.ORDERS.COMMANDS")).IsTrue();
    await Assert.That(options.IsCommandNamespaceRoutedToInbox("myapp.users.commands")).IsFalse();
  }

  [Test]
  public async Task RouteCommandNamespaceToInbox_NullOrWhitespace_ThrowsAsync() {
    var options = new RoutingOptions();

    await Assert.That(() => options.RouteCommandNamespaceToInbox(null!)).Throws<ArgumentException>();
    await Assert.That(() => options.RouteCommandNamespaceToInbox(" ")).Throws<ArgumentException>();
  }

  [Test]
  public async Task RouteAllCommandNamespacesToInbox_FlipsEveryNamespaceAsync() {
    var options = new RoutingOptions().RouteAllCommandNamespacesToInbox();

    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsTrue();
    await Assert.That(options.IsCommandNamespaceRoutedToInbox("anything.at.all")).IsTrue();
  }

  #endregion

  #region Events — byte-identical to DomainTopicOutboxStrategy (default locked)

  [Test]
  public async Task Event_ByteIdenticalToDomainTopicStrategy_FlippedOrNotAsync() {
    var reference = new DomainTopicOutboxStrategy();
    var unflipped = _strategy(out _);
    var flipped = _strategy(out var flipOptions);
    flipOptions.RouteAllCommandNamespacesToInbox();

    foreach (var strategy in new IOutboxRoutingStrategy[] { unflipped, flipped }) {
      var actual = strategy.GetDestination(
        typeof(OutboxTestTypes.Orders.Events.OrderCreated), _noDomains, MessageKind.Event);
      var expected = reference.GetDestination(
        typeof(OutboxTestTypes.Orders.Events.OrderCreated), _noDomains, MessageKind.Event);

      await Assert.That(actual.Address).IsEqualTo(expected.Address);
      await Assert.That(actual.RoutingKey).IsEqualTo(expected.RoutingKey);
      await Assert.That(actual.Metadata is null).IsEqualTo(expected.Metadata is null)
        .Because("events must be byte-identical to DomainTopicOutboxStrategy — the flip never touches the event side");
    }
  }

  [Test]
  public async Task QueryAndUnknownKinds_ByteIdenticalToDomainTopicStrategyAsync() {
    var reference = new DomainTopicOutboxStrategy();
    var strategy = _strategy(out var options);
    options.RouteAllCommandNamespacesToInbox();

    foreach (var kind in new[] { MessageKind.Query, MessageKind.Unknown }) {
      var actual = strategy.GetDestination(
        typeof(OutboxTestTypes.Users.Events.UserCreated), _noDomains, kind);
      var expected = reference.GetDestination(
        typeof(OutboxTestTypes.Users.Events.UserCreated), _noDomains, kind);

      await Assert.That(actual.Address).IsEqualTo(expected.Address);
      await Assert.That(actual.RoutingKey).IsEqualTo(expected.RoutingKey);
    }
  }

  #endregion

  #region Commands — unflipped stays byte-identical to the legacy shared inbox

  [Test]
  public async Task Command_UnflippedNamespace_ByteIdenticalToSharedTopicStrategyAsync() {
    var reference = new SharedTopicOutboxStrategy();
    var strategy = _strategy(out var options);
    // Flip a DIFFERENT namespace — Orders stays legacy.
    options.RouteCommandNamespaceToInbox("outboxtesttypes.users.commands");

    var actual = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.Command);
    var expected = reference.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.Command);

    await Assert.That(actual.Address).IsEqualTo(expected.Address);
    await Assert.That(actual.RoutingKey).IsEqualTo(expected.RoutingKey);
    await _assertSameMetadataAsync(actual, expected);
  }

  [Test]
  public async Task Command_UnflippedWithCustomSharedTopic_ByteIdenticalToSharedTopicStrategyAsync() {
    var reference = new SharedTopicOutboxStrategy("my-inbox");
    var strategy = _strategy(out _, sharedInboxTopic: "my-inbox");

    var actual = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.Command);
    var expected = reference.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.Command);

    await Assert.That(actual.Address).IsEqualTo("my-inbox");
    await Assert.That(actual.Address).IsEqualTo(expected.Address);
    await Assert.That(actual.RoutingKey).IsEqualTo(expected.RoutingKey);
  }

  #endregion

  #region Commands — flipped namespace goes to its per-namespace inbox

  [Test]
  public async Task Command_FlippedNamespace_RoutesToPerNamespaceInboxAsync() {
    var strategy = _strategy(out var options);
    options.RouteCommandNamespaceToInbox("OutboxTestTypes.Orders.Commands"); // any casing

    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.Command);

    await Assert.That(destination.Address).IsEqualTo("inbox.outboxtesttypes.orders.commands");
    await Assert.That(destination.RoutingKey).IsEqualTo("outboxtesttypes.orders.commands.createorder")
      .Because("the routing key keeps the shared-inbox '{namespace}.{typename}' shape — filters and subjects stay compatible");
  }

  [Test]
  public async Task Command_FlippedNamespace_MarksDestinationRequireProvisionedEntityAsync() {
    var strategy = _strategy(out var options);
    options.RouteCommandNamespaceToInbox("outboxtesttypes.orders.commands");

    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.Command);

    await Assert.That(CommandInboxNaming.RequiresProvisionedEntity(destination)).IsTrue()
      .Because("transports must fail LOUDLY when the handling service never provisioned the entity — never a silent broker drop");
  }

  [Test]
  public async Task Command_RouteAll_FlipsEveryDomainNamespaceAsync() {
    var strategy = _strategy(out var options);
    options.RouteAllCommandNamespacesToInbox();

    var orders = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.Command);
    var users = strategy.GetDestination(
      typeof(OutboxTestTypes.Users.Commands.CreateUser), _noDomains, MessageKind.Command);

    await Assert.That(orders.Address).IsEqualTo("inbox.outboxtesttypes.orders.commands");
    await Assert.That(users.Address).IsEqualTo("inbox.outboxtesttypes.users.commands");
  }

  [Test]
  public async Task Command_FlipConsultedLive_FlipAndRollbackWithoutReRegistrationAsync() {
    // The migration story: flip and rollback are CONFIG-level acts on the options object;
    // the registered strategy instance must honor them immediately.
    var strategy = _strategy(out var options);
    var type = typeof(OutboxTestTypes.Orders.Commands.CreateOrder);

    var before = strategy.GetDestination(type, _noDomains, MessageKind.Command);
    await Assert.That(before.Address).IsEqualTo("inbox");

    options.RouteCommandNamespaceToInbox("outboxtesttypes.orders.commands");
    var flipped = strategy.GetDestination(type, _noDomains, MessageKind.Command);
    await Assert.That(flipped.Address).IsEqualTo("inbox.outboxtesttypes.orders.commands");
  }

  [Test]
  public async Task Command_FlippedAddress_MatchesNamespaceInboxSubscriptionExactlyAsync() {
    // CROSS-CONSISTENCY LOCK (the load-bearing one): the entity the publisher flips to IS
    // the entity the handling service subscribed to in phase 5 — same string, same casing.
    var inboxStrategy = new NamespaceInboxStrategy();
    var context = new InboxSubscriptionContext(
      "order-service",
      _noDomains,
      [new HandledMessageInfo(
        "OutboxTestTypes.Orders.Commands.CreateOrder", "outboxtesttypes.orders.commands", MessageKind.Command)]);
    var subscribed = inboxStrategy.GetSubscriptions(context)
      .First(s => s.Metadata?.ContainsKey(NamespaceInboxStrategy.OwnedCommandInboxMetadataKey) == true);

    var outboxStrategy = _strategy(out var options);
    options.RouteCommandNamespaceToInbox("outboxtesttypes.orders.commands");
    var published = outboxStrategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.Command);

    await Assert.That(published.Address).IsEqualTo(subscribed.Topic);
  }

  #endregion

  #region Framework-reserved namespaces and the System kind

  [Test]
  public async Task Command_FrameworkReservedNamespace_FlippedRoutesToBroadcastInboxAsync() {
    // whizbang.core.* NEVER gets a per-namespace inbox (phase 5 reserved the subtree).
    // Under a flip the reserved subtree routes to the system broadcast inbox instead.
    var strategy = _strategy(out var options);
    options.RouteAllCommandNamespacesToInbox();

    var destination = strategy.GetDestination(
      typeof(Whizbang.Core.Commands.System.RebuildPerspectiveCommand), _noDomains, MessageKind.Command);

    await Assert.That(destination.Address).IsEqualTo(CommandInboxNaming.SystemBroadcastTopic);
    await Assert.That(destination.Address).IsNotEqualTo("inbox.whizbang.core.commands.system")
      .Because("a per-namespace inbox for the reserved subtree would have no subscriber — ever");
  }

  [Test]
  public async Task Command_FrameworkReservedNamespace_UnflippedStaysOnLegacySharedInboxAsync() {
    var reference = new SharedTopicOutboxStrategy();
    var strategy = _strategy(out _);

    var actual = strategy.GetDestination(
      typeof(Whizbang.Core.Commands.System.RebuildPerspectiveCommand), _noDomains, MessageKind.Command);
    var expected = reference.GetDestination(
      typeof(Whizbang.Core.Commands.System.RebuildPerspectiveCommand), _noDomains, MessageKind.Command);

    await Assert.That(actual.Address).IsEqualTo(expected.Address);
    await Assert.That(actual.RoutingKey).IsEqualTo(expected.RoutingKey);
  }

  [Test]
  public async Task SystemKind_RoutesToBroadcastInboxUnconditionallyAsync() {
    var strategy = _strategy(out _); // nothing flipped

    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.System);

    await Assert.That(destination.Address).IsEqualTo("inbox.whizbang");
    await Assert.That(destination.RoutingKey).IsEqualTo("outboxtesttypes.orders.commands.createorder")
      .Because("the broadcast inbox subscription patterns match on '{namespace}.{typename}' subjects");
  }

  [Test]
  public async Task Retirement_SystemKindAndFrameworkReservedCommands_RouteToBroadcastAndNowhereElseAsync() {
    // Phase-7 broadcast publish-side lock: under full flip + retirement, MessageKind.System
    // and framework-reserved command namespaces route to the system broadcast inbox — the
    // sole carve-out — and NEVER to the retired shared topic or a per-namespace inbox.
    var strategy = _strategy(out var options);
    options.RouteAllCommandNamespacesToInbox().RetireSharedInbox();

    var system = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.System);
    var reserved = strategy.GetDestination(
      typeof(Whizbang.Core.Commands.System.RebuildPerspectiveCommand), _noDomains, MessageKind.Command);

    await Assert.That(system.Address).IsEqualTo(CommandInboxNaming.SystemBroadcastTopic);
    await Assert.That(reserved.Address).IsEqualTo(CommandInboxNaming.SystemBroadcastTopic);
  }

  [Test]
  public async Task Retirement_DomainCommands_RouteToPerNamespaceInbox_NeverTheSharedTopicAsync() {
    // Under retirement every domain command has a per-namespace destination — nothing may
    // resolve to the legacy shared topic (there is nobody left subscribed to it).
    var strategy = _strategy(out var options);
    options.RouteAllCommandNamespacesToInbox().RetireSharedInbox();

    foreach (var type in new[] {
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder),
      typeof(OutboxTestTypes.Users.Commands.CreateUser)
    }) {
      var destination = strategy.GetDestination(type, _noDomains, MessageKind.Command);
      await Assert.That(destination.Address).IsNotEqualTo(strategy.SharedInboxTopic)
        .Because("a retirement-mode publish to the shared topic would be delivered to nobody");
      await Assert.That(destination.Address).IsEqualTo(
        CommandInboxNaming.TopicFor(type.Namespace!));
    }
  }

  #endregion

  #region Registration and validation

  [Test]
  public async Task Outbox_UseNamespaceRouting_SetsNamespaceStrategyAsync() {
    var options = new RoutingOptions();

    options.Outbox.UseNamespaceRouting();

    await Assert.That(options.OutboxStrategy).IsTypeOf<NamespaceOutboxStrategy>();
    await Assert.That(((NamespaceOutboxStrategy)options.OutboxStrategy).SharedInboxTopic).IsEqualTo("inbox");
  }

  [Test]
  public async Task Outbox_UseNamespaceRouting_FlipSetOnSameOptionsIsConsultedAsync() {
    var options = new RoutingOptions();
    options.Outbox.UseNamespaceRouting();
    options.RouteCommandNamespaceToInbox("outboxtesttypes.orders.commands");

    var destination = options.OutboxStrategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.Command);

    await Assert.That(destination.Address).IsEqualTo("inbox.outboxtesttypes.orders.commands");
  }

  [Test]
  public async Task DefaultOutboxStrategy_UnchangedByPhase6Async() {
    // DEFAULT LOCK: nothing changes unless the consumer opts in.
    await Assert.That(new RoutingOptions().OutboxStrategy).IsTypeOf<DomainTopicOutboxStrategy>();
  }

  [Test]
  public async Task Constructor_NullOptionsOrBlankTopic_ThrowsAsync() {
    await Assert.That(() => new NamespaceOutboxStrategy(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new NamespaceOutboxStrategy(new RoutingOptions(), " ")).Throws<ArgumentException>();
  }

  [Test]
  public async Task GetDestination_NullArguments_ThrowAsync() {
    var strategy = _strategy(out _);

    await Assert.That(() => strategy.GetDestination(null!, _noDomains, MessageKind.Command))
      .Throws<ArgumentNullException>();
    await Assert.That(() => strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), null!, MessageKind.Command))
      .Throws<ArgumentNullException>();
  }

  #endregion

  #region ResolveFlippedCommandInboxAddress — the publish-time name-based seam

  [Test]
  public async Task ResolveFlipped_ParityWithGetDestinationForEveryCaseAsync() {
    var strategy = _strategy(out var options);
    options.RouteCommandNamespaceToInbox("outboxtesttypes.orders.commands");

    // Flipped namespace → the per-namespace entity.
    await Assert.That(strategy.ResolveFlippedCommandInboxAddress("outboxtesttypes.orders.commands"))
      .IsEqualTo("inbox.outboxtesttypes.orders.commands");
    // Any casing.
    await Assert.That(strategy.ResolveFlippedCommandInboxAddress("OutboxTestTypes.Orders.Commands"))
      .IsEqualTo("inbox.outboxtesttypes.orders.commands");
    // Unflipped namespace → null (caller keeps the legacy wire shape).
    await Assert.That(strategy.ResolveFlippedCommandInboxAddress("outboxtesttypes.users.commands")).IsNull();
    // Null/empty namespace → null.
    await Assert.That(strategy.ResolveFlippedCommandInboxAddress(null)).IsNull();
    await Assert.That(strategy.ResolveFlippedCommandInboxAddress("")).IsNull();

    // Framework-reserved + flipped → the broadcast inbox.
    options.RouteAllCommandNamespacesToInbox();
    await Assert.That(strategy.ResolveFlippedCommandInboxAddress("whizbang.core.commands.system"))
      .IsEqualTo(CommandInboxNaming.SystemBroadcastTopic);
  }

  [Test]
  public async Task ResolveFlipped_AgreesWithGetDestinationAddressAsync() {
    // Parity lock: the name-based seam and the type-based routing answer must be the same
    // projection for every command type.
    var strategy = _strategy(out var options);
    options.RouteCommandNamespaceToInbox("outboxtesttypes.orders.commands");

    foreach (var type in new[] {
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder),
      typeof(OutboxTestTypes.Users.Commands.CreateUser)
    }) {
      var byType = strategy.GetDestination(type, _noDomains, MessageKind.Command).Address;
      var byName = strategy.ResolveFlippedCommandInboxAddress(type.Namespace!.ToLowerInvariant())
        ?? strategy.SharedInboxTopic;
      await Assert.That(byName).IsEqualTo(byType);
    }
  }

  #endregion

  #region Configuration binding — Whizbang:Routing:CommandNamespacesToInbox

  [Test]
  public async Task WithRouting_ConfigurationFlipSet_AppliedOnOptionsResolutionAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:CommandNamespacesToInbox:0"] = "MyApp.Orders.Commands",
        ["Whizbang:Routing:CommandNamespacesToInbox:1"] = "myapp.billing.commands"
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    new WhizbangBuilder(services).WithRouting(r => r.Outbox.UseNamespaceRouting());

    using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.CommandNamespacesToInbox).Contains("myapp.orders.commands");
    await Assert.That(options.CommandNamespacesToInbox).Contains("myapp.billing.commands");
    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsFalse();
  }

  [Test]
  public async Task WithRouting_ConfigurationWildcard_RoutesAllNamespacesAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:CommandNamespacesToInbox:0"] = "*"
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    new WhizbangBuilder(services).WithRouting(r => r.Outbox.UseNamespaceRouting());

    using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsTrue();
  }

  [Test]
  public async Task WithRouting_NoConfigurationSection_DefaultsLockedAsync() {
    // Also covers the no-IConfiguration-registered host (locked no-op).
    var services = new ServiceCollection();
    new WhizbangBuilder(services).WithRouting(r => r.Outbox.UseNamespaceRouting());

    using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.CommandNamespacesToInbox).IsEmpty();
    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsFalse();
  }

  [Test]
  public async Task WithRouting_ResolvingOutboxStrategy_AppliesConfigurationBindingFirstAsync() {
    // A consumer that resolves ONLY the strategy (never IOptions<RoutingOptions>) must still
    // see the configuration flips — the strategy factory forces the binding.
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:CommandNamespacesToInbox:0"] = "outboxtesttypes.orders.commands"
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    new WhizbangBuilder(services).WithRouting(r => r.Outbox.UseNamespaceRouting());

    using var provider = services.BuildServiceProvider();
    var strategy = provider.GetRequiredService<IOutboxRoutingStrategy>();
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.Command);

    await Assert.That(destination.Address).IsEqualTo("inbox.outboxtesttypes.orders.commands");
  }

  [Test]
  public async Task WithRouting_ConfigurationRollback_EntryRemoved_NamespaceReturnsToSharedInboxAsync() {
    // ROLLBACK LOCK: the same host, rebooted without the entry, routes legacy again.
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:SomethingElse"] = "x" // section exists, flip key absent
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    new WhizbangBuilder(services).WithRouting(r => r.Outbox.UseNamespaceRouting());

    using var provider = services.BuildServiceProvider();
    var strategy = provider.GetRequiredService<IOutboxRoutingStrategy>();
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder), _noDomains, MessageKind.Command);

    await Assert.That(destination.Address).IsEqualTo("inbox");
  }

  #endregion

  private static async Task _assertSameMetadataAsync(TransportDestination actual, TransportDestination expected) {
    await Assert.That(actual.Metadata is null).IsEqualTo(expected.Metadata is null);
    if (expected.Metadata is null || actual.Metadata is null) {
      return;
    }

    await Assert.That(actual.Metadata.Count).IsEqualTo(expected.Metadata.Count);
    foreach (var (key, value) in expected.Metadata) {
      await Assert.That(actual.Metadata.ContainsKey(key)).IsTrue();
      await Assert.That(actual.Metadata[key].ToString()).IsEqualTo(value.ToString());
    }
  }
}
