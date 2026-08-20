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
