using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Locks the ONE derivation for per-namespace command inbox entity names (topology arc
/// phase 6). Publisher (NamespaceOutboxStrategy / TransportPublishStrategy) and subscriber
/// (NamespaceInboxStrategy) both derive names from this helper — these tests lock the
/// derivation itself AND its cross-consistency with the phase-5 subscription naming.
/// </summary>
public class CommandInboxNamingTests {
  #region TopicFor derivation

  [Test]
  public async Task TopicFor_PrefixesAndLowercasesTheContractNamespaceAsync() {
    await Assert.That(CommandInboxNaming.TopicFor("MyApp.Orders.Commands"))
      .IsEqualTo("inbox.myapp.orders.commands");
  }

  [Test]
  public async Task TopicFor_AlreadyLowercase_IsStableAsync() {
    await Assert.That(CommandInboxNaming.TopicFor("myapp.orders.commands"))
      .IsEqualTo("inbox.myapp.orders.commands");
  }

  [Test]
  public async Task TopicFor_NullOrWhitespace_ThrowsArgumentExceptionAsync() {
    await Assert.That(() => CommandInboxNaming.TopicFor(null!)).Throws<ArgumentException>();
    await Assert.That(() => CommandInboxNaming.TopicFor("  ")).Throws<ArgumentException>();
  }

  [Test]
  public async Task TopicFor_MatchesNamespaceInboxStrategySubscriptionNamingExactlyAsync() {
    // CROSS-CONSISTENCY LOCK: the phase-5 inbox subscription for a handled command namespace
    // and the phase-6 helper derivation must be the SAME string — same casing, same prefix.
    var strategy = new NamespaceInboxStrategy();
    var context = new InboxSubscriptionContext(
      "order-service",
      new HashSet<string>(StringComparer.OrdinalIgnoreCase),
      [new Whizbang.Core.Messaging.HandledMessageInfo(
        "MyApp.Orders.Commands.PlaceOrder", "myapp.orders.commands", MessageKind.Command)]);

    var subscriptions = strategy.GetSubscriptions(context);
    var perNamespace = subscriptions.First(s =>
      s.Metadata?.ContainsKey(NamespaceInboxStrategy.OwnedCommandInboxMetadataKey) == true);

    await Assert.That(CommandInboxNaming.TopicFor("MyApp.Orders.Commands"))
      .IsEqualTo(perNamespace.Topic)
      .Because("publisher-side and subscriber-side entity naming must agree by construction");
  }

  #endregion

  #region System broadcast topic

  [Test]
  public async Task SystemBroadcastTopic_IsInboxWhizbangAndMatchesInboxStrategyAsync() {
    await Assert.That(CommandInboxNaming.SystemBroadcastTopic).IsEqualTo("inbox.whizbang");
    await Assert.That(CommandInboxNaming.SystemBroadcastTopic)
      .IsEqualTo(NamespaceInboxStrategy.SystemBroadcastInboxTopic)
      .Because("outbox System branch and inbox broadcast subscription must name the same entity");
  }

  #endregion

  #region Framework-reserved classification

  [Test]
  [Arguments("whizbang.core", true)]
  [Arguments("whizbang.core.commands.system", true)]
  [Arguments("whizbang.core.minting", true)]
  [Arguments("Whizbang.Core.Messaging", true)] // any casing
  [Arguments("whizbang.corex", false)]
  [Arguments("whizbang", false)]
  [Arguments("myapp.orders.commands", false)]
  public async Task IsFrameworkReserved_ClassifiesTheReservedSubtreeAsync(string ns, bool expected) {
    await Assert.That(CommandInboxNaming.IsFrameworkReserved(ns)).IsEqualTo(expected);
  }

  [Test]
  public async Task IsFrameworkReserved_NullOrEmpty_IsFalseAsync() {
    await Assert.That(CommandInboxNaming.IsFrameworkReserved(null)).IsFalse();
    await Assert.That(CommandInboxNaming.IsFrameworkReserved("")).IsFalse();
  }

  #endregion

  #region Consumer-provisioned entity classification

  [Test]
  [Arguments("inbox.myapp.orders.commands", true)]
  [Arguments("inbox.whizbang", true)]
  [Arguments("inbox", false)] // the legacy shared inbox stays publisher-provisioned
  [Arguments("myapp.orders.events", false)]
  [Arguments("inboxx.myapp", false)]
  public async Task IsConsumerProvisionedInboxEntity_ClassifiesInboxPrefixedEntitiesAsync(string address, bool expected) {
    await Assert.That(CommandInboxNaming.IsConsumerProvisionedInboxEntity(address)).IsEqualTo(expected);
  }

  [Test]
  public async Task IsConsumerProvisionedInboxEntity_NullOrEmpty_IsFalseAsync() {
    await Assert.That(CommandInboxNaming.IsConsumerProvisionedInboxEntity(null)).IsFalse();
    await Assert.That(CommandInboxNaming.IsConsumerProvisionedInboxEntity("")).IsFalse();
  }

  #endregion

  #region Phase-8 naming forward-compat (tag→namespace routability)

  // The traffic-classes arc (phase 8) maps tag→TransportNamespace as a post-process over the
  // entity names the strategies emit, using a "{namespaceKey}" prefix/suffix scheme. These
  // locks assert — NOW, before traffic classes ship — that every entity name the manifest can
  // emit is routable under those rules: ASB-legal characters only, lowercase, dot-separated
  // segments, and enough length headroom under ASB's 260-char entity limit for class
  // prefixes/suffixes.

  /// <summary>Headroom reserved under ASB's 260-char entity-name limit for a future
  /// "{namespaceKey}" class prefix/suffix (e.g. a "sys-control" style decoration).</summary>
  private const int CLASS_DECORATION_HEADROOM = 64;

  /// <summary>ASB entity-name limit (topics/queues): 260 characters.</summary>
  private const int ASB_ENTITY_NAME_LIMIT = 260;

  private static async Task _assertRoutableEntityNameAsync(string name) {
    await Assert.That(name).IsEqualTo(name.ToLowerInvariant())
      .Because($"'{name}': entity names are lowercase-invariant — casing variants would split broker entities");
    await Assert.That(name.Length).IsLessThanOrEqualTo(ASB_ENTITY_NAME_LIMIT - CLASS_DECORATION_HEADROOM)
      .Because($"'{name}': the name must leave {CLASS_DECORATION_HEADROOM} chars of headroom under "
             + $"ASB's {ASB_ENTITY_NAME_LIMIT}-char limit for a phase-8 class prefix/suffix");

    var segments = name.Split('.');
    foreach (var segment in segments) {
      await Assert.That(segment.Length).IsGreaterThan(0)
        .Because($"'{name}': dot-separated with non-empty segments — no leading/trailing/double dots "
               + "(a '{namespaceKey}' prefix scheme joins on '.')");
      foreach (var c in segment) {
        var legal = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '-';
        await Assert.That(legal).IsTrue()
          .Because($"'{name}': '{c}' is outside [a-z0-9_-] — ASB forbids most punctuation, '/' creates "
                 + "entity hierarchy, and exotic characters would break tag→namespace name rewriting");
      }
    }
  }

  [Test]
  public async Task RetirementManifest_EveryEmittedEntityName_IsRoutableUnderTagNamespaceRulesAsync() {
    // The manifest is the provisioning authority — every name it can emit (per-namespace
    // inboxes, the broadcast inbox, domain topics) must satisfy the routability rules.
    var options = new RoutingOptions().RouteAllCommandNamespacesToInbox().RetireSharedInbox();
    var context = new InboxSubscriptionContext(
      "order-service",
      new HashSet<string>(StringComparer.OrdinalIgnoreCase),
      [
        new Whizbang.Core.Messaging.HandledMessageInfo(
          "OutboxTestTypes.Orders.Commands.CreateOrder", "outboxtesttypes.orders.commands", MessageKind.Command),
        new Whizbang.Core.Messaging.HandledMessageInfo(
          "OutboxTestTypes.Users.Commands.CreateUser", "outboxtesttypes.users.commands", MessageKind.Command),
      ]);
    var manifest = TopologyManifestBuilder.Build(
      new NamespaceOutboxStrategy(options),
      new NamespaceInboxStrategy(options),
      context,
      [
        new MessageTypeCatalogEntry(
          typeof(OutboxTestTypes.Orders.Commands.CreateOrder),
          typeof(OutboxTestTypes.Orders.Commands.CreateOrder).FullName!, "command", null),
        new MessageTypeCatalogEntry(
          typeof(OutboxTestTypes.Orders.Events.OrderCreated),
          typeof(OutboxTestTypes.Orders.Events.OrderCreated).FullName!, "event", null)
      ]);

    var entityNames = manifest.PublishDestinations.Select(d => d.Address)
      .Concat(manifest.Subscriptions.Select(s => s.Topic))
      .Distinct(StringComparer.Ordinal)
      .ToList();

    await Assert.That(entityNames.Count).IsGreaterThanOrEqualTo(4)
      .Because("precondition: the manifest names per-namespace inboxes, the broadcast inbox, and a domain topic");
    foreach (var name in entityNames) {
      await _assertRoutableEntityNameAsync(name);
    }
  }

  [Test]
  public async Task TopicFor_AddsOnlyThePrefixLength_LengthBoundHoldsForDeepNamespacesAsync() {
    // The derivation adds exactly the "inbox." prefix — so a contract namespace stays within
    // the headroom bound as long as the namespace itself does. Locked at the boundary.
    var deepNamespace = string.Join(".", Enumerable.Repeat("segment0", 20)); // 179 chars
    var name = CommandInboxNaming.TopicFor(deepNamespace);

    await Assert.That(name.Length).IsEqualTo(CommandInboxNaming.TopicPrefix.Length + deepNamespace.Length);
    await _assertRoutableEntityNameAsync(name);
  }

  [Test]
  public async Task SystemBroadcastTopic_IsRoutableUnderTagNamespaceRulesAsync() {
    await _assertRoutableEntityNameAsync(CommandInboxNaming.SystemBroadcastTopic);
  }

  #endregion

  #region RequiresProvisionedEntity marker

  [Test]
  public async Task RequiresProvisionedEntity_TrueMarker_IsDetectedAsync() {
    var destination = new TransportDestination(
      "inbox.myapp.orders.commands",
      "myapp.orders.commands.placeorder",
      new Dictionary<string, JsonElement> {
        [CommandInboxNaming.RequireProvisionedEntityMetadataKey] = JsonDocument.Parse("true").RootElement
      });

    await Assert.That(CommandInboxNaming.RequiresProvisionedEntity(destination)).IsTrue();
  }

  [Test]
  public async Task RequiresProvisionedEntity_FalseMarkerOrAbsent_IsNotDetectedAsync() {
    var falseMarker = new TransportDestination(
      "inbox.myapp.orders.commands",
      null,
      new Dictionary<string, JsonElement> {
        [CommandInboxNaming.RequireProvisionedEntityMetadataKey] = JsonDocument.Parse("false").RootElement
      });
    var noMetadata = new TransportDestination("inbox");

    await Assert.That(CommandInboxNaming.RequiresProvisionedEntity(falseMarker)).IsFalse();
    await Assert.That(CommandInboxNaming.RequiresProvisionedEntity(noMetadata)).IsFalse();
    await Assert.That(CommandInboxNaming.RequiresProvisionedEntity(null)).IsFalse();
  }

  #endregion
}
