using Whizbang.Core.Messaging;
using Whizbang.Testing.Workers;

namespace Whizbang.Testing.Tests.Workers;

/// <summary>
/// Locks <see cref="PermissiveReceptorRegistryQuery"/>'s single promise: it answers yes to every
/// question, for every message type, so a hand-built worker gates nothing and discards nothing.
/// </summary>
/// <remarks>
/// The contract is asserted directly rather than through a worker. Each answer is a gate on the
/// receive path - <c>HasAnyConsumer</c> decides whether a message is dropped as unconsumed,
/// <c>HasInboxHandler</c> whether it is handed to the inbox, <c>HasReceptors</c> whether a
/// lifecycle stage runs at all - so a single "no" here silently turns a test double meant to
/// wave everything through into one that eats messages.
/// </remarks>
public class PermissiveReceptorRegistryQueryTests {

  [Test]
  public async Task EveryGate_AnswersYes_ForTypesItHasNeverSeenAsync() {
    var registry = new PermissiveReceptorRegistryQuery();

    foreach (var messageType in new[] { "Some.Unregistered.Event", "", "Whizbang.Core.Messaging.Nothing" }) {
      await Assert.That(registry.HasInboxHandler(messageType)).IsTrue();
      await Assert.That(registry.HasAnyConsumer(messageType)).IsTrue();
      await Assert.That(registry.HasReceptors(LifecycleStage.PostInboxInline, messageType)).IsTrue();
    }
  }

  [Test]
  public async Task EveryLifecycleStage_HasReceptorsAsync() {
    var registry = new PermissiveReceptorRegistryQuery();

    foreach (var stage in Enum.GetValues<LifecycleStage>()) {
      await Assert.That(registry.HasReceptors(stage, "Some.Unregistered.Event")).IsTrue();
    }
  }

  [Test]
  public async Task GetHandledMessages_ReportsNothing_BecauseNothingIsRegisteredAsync() {
    var registry = new PermissiveReceptorRegistryQuery();

    // "Yes to everything" is an answer about gating, not a claim to a catalog: the double has
    // no registrations to enumerate, and reporting a fabricated one would mislead any caller
    // that builds routing or diagnostics from this list.
    await Assert.That(registry.GetHandledMessages()).IsEmpty();
  }
}
