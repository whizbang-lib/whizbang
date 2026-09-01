using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The runtime message-kind classifier the publish strategy uses to route an outbox row.
/// </summary>
/// <remarks>
/// This is a second implementation of the ladder in <c>CompileTimeMessageClassification</c>: the
/// generator classifies from Roslyn symbols at build time, and this classifies from the stored
/// assembly-qualified type name at publish time, because the outbox row carries a string rather
/// than a type.
///
/// <para>
/// Two implementations of one rule is a drift hazard by construction. A message that the
/// generator called a Command and this calls Unknown routes one way in the registry and another
/// on the wire, and nothing reports the disagreement — the message simply goes somewhere nobody
/// is listening. So the shapes pinned here are the same ones pinned for the compile-time ladder.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/TransportPublishStrategy.cs</code-under-test>
[Category("Core")]
[Category("Routing")]
public class TransportPublishStrategyMessageKindTests {

  private static MessageKind _kindOf(string typeFullName)
    => TransportPublishStrategy.DetectMessageKindForTest(typeFullName);

  // ============================================================
  // Namespace convention wins first
  // ============================================================

  [Test]
  [Arguments("Shop.Commands.PlaceOrder, Shop", MessageKind.Command)]
  [Arguments("Shop.Events.OrderPlaced, Shop", MessageKind.Event)]
  [Arguments("Shop.Queries.GetOrder, Shop", MessageKind.Query)]
  public async Task ANamespaceSegment_DecidesTheKindAsync(string typeName, MessageKind expected) {
    await Assert.That(_kindOf(typeName)).IsEqualTo(expected);
  }

  [Test]
  public async Task TheNamespaceSegmentMatchIsCaseInsensitiveAsync() {
    // The compile-time ladder matches case-insensitively too. Diverging on case here would
    // classify the same type differently in the two places.
    await Assert.That(_kindOf("Shop.EVENTS.OrderPlaced, Shop")).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task ANamespaceSegmentAnywhereInThePathCountsAsync() {
    await Assert.That(_kindOf("Shop.Events.Fulfillment.OrderShipped, Shop"))
      .IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task TheNamespaceBeatsAContradictingSuffixAsync() {
    // Same precedence as the compile-time ladder: the folder a contract lives in is a
    // deliberate choice, a suffix is often habit.
    await Assert.That(_kindOf("Shop.Events.ArchiveCommand, Shop")).IsEqualTo(MessageKind.Event);
  }

  // ============================================================
  // Then the type-name suffix
  // ============================================================

  [Test]
  [Arguments("Shop.PlaceOrderCommand, Shop", MessageKind.Command)]
  [Arguments("Shop.GetOrderQuery, Shop", MessageKind.Query)]
  [Arguments("Shop.OrderShippedEvent, Shop", MessageKind.Event)]
  public async Task ASuffix_DecidesTheKindWhenTheNamespaceIsSilentAsync(
      string typeName, MessageKind expected) {
    await Assert.That(_kindOf(typeName)).IsEqualTo(expected);
  }

  // ============================================================
  // The floor, and the shapes a stored type name can actually take
  // ============================================================

  [Test]
  public async Task ATypeNameWithNoSignal_IsUnknownAsync() {
    // Unknown is a real answer, not a failure: it tells the publish path it has no basis to
    // pick a routing shape, which is safer than guessing one.
    await Assert.That(_kindOf("Shop.Contracts.Payload, Shop")).IsEqualTo(MessageKind.Unknown);
  }

  [Test]
  public async Task AnEmptyTypeName_IsUnknownAsync() {
    // The outbox row's message type is a stored string, so it can be empty in a way a Roslyn
    // symbol never is — a truncated or hand-written row must not throw on the publish path.
    await Assert.That(_kindOf(string.Empty)).IsEqualTo(MessageKind.Unknown);
  }

  [Test]
  public async Task ATypeNameWithNoNamespace_IsClassifiedBySuffixAloneAsync() {
    // A global-namespace type has no segments to read, and the extraction must not mistake the
    // type name itself for a namespace.
    await Assert.That(_kindOf("PlaceOrderCommand, Shop")).IsEqualTo(MessageKind.Command);
  }

  [Test]
  public async Task ABareTypeNameWithNoAssembly_StillClassifiesAsync() {
    // Not every stored name carries the assembly part; the parse has to tolerate both.
    await Assert.That(_kindOf("Shop.Commands.PlaceOrder")).IsEqualTo(MessageKind.Command);
  }

  [Test]
  public async Task AGenericTypeName_DoesNotThrowAsync() {
    // Envelope-wrapped payload names are the common stored shape and carry nested brackets.
    var kind = _kindOf(
      "Whizbang.Core.Observability.MessageEnvelope`1[[Shop.Commands.PlaceOrder, Shop]], Whizbang.Core");

    await Assert.That(kind).IsNotEqualTo((MessageKind)(-1));
  }

  [Test]
  public async Task TheRuntimeAndCompileTimeLaddersAgreeOnPrecedenceAsync() {
    // The property that matters across the pair: for each shape, both must reach the same
    // answer. The compile-time side is pinned in MessageKindClassificationTests; these are the
    // same inputs expressed as stored type names.
    await Assert.That(_kindOf("Shop.Commands.PlaceOrder, Shop")).IsEqualTo(MessageKind.Command);
    await Assert.That(_kindOf("Shop.Events.OrderPlaced, Shop")).IsEqualTo(MessageKind.Event);
    await Assert.That(_kindOf("Shop.Queries.GetOrder, Shop")).IsEqualTo(MessageKind.Query);
    await Assert.That(_kindOf("Shop.Events.ArchiveCommand, Shop")).IsEqualTo(MessageKind.Event);
    await Assert.That(_kindOf("Shop.Contracts.Payload, Shop")).IsEqualTo(MessageKind.Unknown);
  }
}
