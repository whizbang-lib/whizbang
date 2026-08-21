using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// The DURABLE-versus-SUPERSEDABLE split, implemented and locked (topology arc phase 9; the
/// decision the two specs left open).
/// <para>
/// The phase-7 system BROADCAST inbox and the phase-8 control CLASS are not the same thing, and
/// three framework contract namespaces had to be assigned between them:
/// </para>
/// <list type="bullet">
/// <item><c>whizbang.core.commands.system</c> — durable system commands (run-control,
/// killswitches, rebuild/reseed). One-shot operator intent: BROADCAST INBOX, sessions kept,
/// no TTL. A short TTL here would silently discard an operator's command.</item>
/// <item><c>whizbang.core.minting</c> — composite envelopes. Wire-only wrappers around DURABLE
/// payload (re-delivered events, coalesced singles, audit records): BROADCAST INBOX. Losing one
/// loses real events.</item>
/// <item><c>whizbang.core.messaging</c> — supersedable control signals (checkpoints, manifests,
/// re-delivery requests, gap/divergence reports). Every one is re-derived on the next cadence:
/// CONTROL CLASS.</item>
/// </list>
/// <para>
/// Splitting the ENTITY is what makes the class's delivery semantics expressible at all: session
/// enablement is a property of a Service Bus subscription, so one entity cannot be both
/// session-ordered for durable system commands and sessionless for control. The split is gated on
/// <see cref="ControlClassOptions.SessionlessSubscriptions"/> — with it off the subscription set
/// is byte-identical to phase 7.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Routing/NamespaceInboxStrategy.cs</code-under-test>
[Category("Core")]
[Category("Routing")]
public class ControlClassSubscriptionSplitTests {
  private static InboxSubscriptionContext _context() => new(
    ServiceName: "orders-service",
    OwnedDomains: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    HandledMessages: []);

  private static IReadOnlyList<string> _patternsOf(InboxSubscription subscription) =>
    subscription.Metadata?["RoutingPatterns"] as IReadOnlyList<string> ?? [];

  // ========================================
  // Default posture — the phase-7 shape, unchanged
  // ========================================

  [Test]
  public async Task ControlSplitOff_BroadcastInboxKeepsAllThreeFrameworkPatternsAsync() {
    var strategy = new NamespaceInboxStrategy(new RoutingOptions());

    var subscriptions = strategy.GetSubscriptions(_context());

    var broadcast = subscriptions.Single(s => s.Topic == CommandInboxNaming.SystemBroadcastTopic);
    await Assert.That(_patternsOf(broadcast)).Contains("whizbang.core.commands.system.#");
    await Assert.That(_patternsOf(broadcast)).Contains("whizbang.core.messaging.#");
    await Assert.That(_patternsOf(broadcast)).Contains("whizbang.core.minting.#");
    await Assert.That(subscriptions.Any(s => s.Topic == CommandInboxNaming.ControlBroadcastTopic)).IsFalse()
      .Because("no control entity exists until the split is opted into — phase-7 shape, byte-identical");
  }

  [Test]
  public async Task ControlSplitOff_NoSubscriptionIsMarkedControlClassAsync() {
    var strategy = new NamespaceInboxStrategy(new RoutingOptions());

    var subscriptions = strategy.GetSubscriptions(_context());

    await Assert.That(subscriptions.Any(NamespaceInboxStrategy.IsControlClassSubscription)).IsFalse();
  }

  // ========================================
  // Split engaged
  // ========================================

  [Test]
  public async Task ControlSplitOn_AddsADedicatedControlEntityAsync() {
    var strategy = new NamespaceInboxStrategy(
      new RoutingOptions(), controlClass: new ControlClassOptions { SessionlessSubscriptions = true });

    var subscriptions = strategy.GetSubscriptions(_context());

    var control = subscriptions.Single(s => s.Topic == CommandInboxNaming.ControlBroadcastTopic);
    await Assert.That(_patternsOf(control)).IsEquivalentTo(["whizbang.core.messaging.#"])
      .Because("the control entity carries the SUPERSEDABLE family and nothing else");
    await Assert.That(NamespaceInboxStrategy.IsControlClassSubscription(control)).IsTrue();
  }

  [Test]
  public async Task ControlSplitOn_BroadcastInboxKeepsDurableSystemCommandsAndCompositesAsync() {
    var strategy = new NamespaceInboxStrategy(
      new RoutingOptions(), controlClass: new ControlClassOptions { SessionlessSubscriptions = true });

    var subscriptions = strategy.GetSubscriptions(_context());

    var broadcast = subscriptions.Single(s => s.Topic == CommandInboxNaming.SystemBroadcastTopic);
    await Assert.That(_patternsOf(broadcast)).Contains("whizbang.core.commands.system.#")
      .Because("durable operator intent stays on the durable, session-ordered entity");
    await Assert.That(_patternsOf(broadcast)).Contains("whizbang.core.minting.#")
      .Because("a composite envelope wraps DURABLE payload — losing one loses real events");
    await Assert.That(_patternsOf(broadcast)).DoesNotContain("whizbang.core.messaging.#")
      .Because("the supersedable family MOVED — leaving it here would double-deliver every "
             + "checkpoint and defeat the sessionless provisioning the split exists for");
    await Assert.That(NamespaceInboxStrategy.IsControlClassSubscription(broadcast)).IsFalse();
  }

  [Test]
  public async Task ControlSplitOn_EveryFrameworkPatternIsStillCoveredExactlyOnceAsync() {
    // Completeness lock: the split PARTITIONS the framework patterns. A pattern lost here is
    // framework traffic no service subscribes to — the exact silent-drop failure mode the
    // checkpoint routing fix was written for.
    var split = new NamespaceInboxStrategy(
      new RoutingOptions(), controlClass: new ControlClassOptions { SessionlessSubscriptions = true })
      .GetSubscriptions(_context());
    var unsplit = new NamespaceInboxStrategy(new RoutingOptions()).GetSubscriptions(_context());

    var unsplitPatterns = _patternsOf(
      unsplit.Single(s => s.Topic == CommandInboxNaming.SystemBroadcastTopic)).Order().ToList();
    var splitPatterns = split
      .Where(s => s.Topic is not null
        && (s.Topic == CommandInboxNaming.SystemBroadcastTopic || s.Topic == CommandInboxNaming.ControlBroadcastTopic))
      .SelectMany(_patternsOf)
      .Order()
      .ToList();

    await Assert.That(splitPatterns).IsEquivalentTo(unsplitPatterns);
    await Assert.That(splitPatterns.Distinct().Count()).IsEqualTo(splitPatterns.Count)
      .Because("a pattern on BOTH entities would double-deliver every message matching it");
  }

  [Test]
  public async Task ControlSplitOn_UnderRetirement_SetIsPerNamespacePlusBroadcastPlusControlAsync() {
    var routing = new RoutingOptions();
    routing.RouteAllCommandNamespacesToInbox();
    routing.RetireSharedInbox();
    var strategy = new NamespaceInboxStrategy(
      routing, controlClass: new ControlClassOptions { SessionlessSubscriptions = true });

    var subscriptions = strategy.GetSubscriptions(_context());

    await Assert.That(subscriptions.Select(s => s.Topic).Order()).IsEquivalentTo([
      CommandInboxNaming.ControlBroadcastTopic,
      CommandInboxNaming.SystemBroadcastTopic,
    ]).Because("no catch-all remnant survives the split either");
  }

  // ========================================
  // The phase-8.5 interaction — the reason sessionless matters beyond cost
  // ========================================

  [Test]
  public async Task ControlClassEntity_IsSessionless_SoTheAgeDetectorIsNotItsOnlyValveAsync() {
    // Phase 8.5 established (spike + a live Standard-namespace probe): on a SESSION-enabled
    // entity, lock loss via connection death does NOT increment DeliveryCount, so the broker's
    // MaxDeliveryCount valve — and the transport branch reading the same counter — can never fire.
    // A NON-session entity's message-lock loss DOES increment it, and the plain-subscription DLQ
    // valve fires end to end. Provisioning the control class sessionless therefore restores the
    // broker's own valve for this class: the age-based detector is a backstop here, not the only
    // thing standing between a control storm and an unbounded redelivery loop.
    var control = new NamespaceInboxStrategy(
        new RoutingOptions(), controlClass: new ControlClassOptions { SessionlessSubscriptions = true })
      .GetSubscriptions(_context())
      .Single(s => s.Topic == CommandInboxNaming.ControlBroadcastTopic);

    await Assert.That(NamespaceInboxStrategy.IsControlClassSubscription(control)).IsTrue()
      .Because("the marker is what the provisioners read to create the subscription WITHOUT "
             + "sessions — and a sessionless entity is one where DeliveryCount actually rises");
  }
}
