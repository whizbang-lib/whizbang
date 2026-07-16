using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Integration.Tests.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Integration.Tests;

/// <summary>
/// End-to-end DI-wiring test for the A1 absorb-all-topic feature. Unit tests construct
/// <see cref="MessageDiscardPolicy"/> directly with an <c>Options.Create(...)</c> — they do NOT
/// prove the production registration chain actually threads the configured
/// <see cref="RoutingOptions.AbsorbedNamespaces"/> into the resolved policy. This test builds the
/// container the way a real service boots — <c>AddWhizbang().WithRouting(AbsorbNamespaces(...))</c>
/// then <c>AddWhizbangWorkers()</c> — resolves the singleton <see cref="IMessageDiscardPolicy"/>,
/// and confirms the receive gate keeps an unconsumed event whose namespace is absorbed while still
/// dropping an unconsumed event on a non-absorbed namespace (the bypass is namespace-scoped).
/// </summary>
/// <remarks>
/// The two payload types are pure routing test types (see <c>NamespaceRoutingTestTypes.cs</c>) with
/// no receptor or perspective, so <c>HasAnyConsumer</c> is false for BOTH — the only differentiator
/// is whether their namespace was absorbed. That isolates the wiring under test: <c>WithRouting</c>
/// registering <c>Options.Create(RoutingOptions)</c> and the <c>AddWhizbangWorkers</c> factory
/// passing that <c>IOptions&lt;RoutingOptions&gt;</c> into the <see cref="MessageDiscardPolicy"/> ctor.
/// </remarks>
[Category("Integration")]
public class AbsorbNamespaceWiringIntegrationTests {
  // Namespace of the absorbed unconsumed type below. Passed to AbsorbNamespaces() verbatim.
  private const string ABSORBED_NAMESPACE = "TestNamespaces.MyApp.Orders.Events";

  // Unconsumed event on the ABSORBED namespace → must be KEPT at the receive gate.
  private static readonly string _absorbedUnconsumedType =
    typeof(TestNamespaces.MyApp.Orders.Events.OrderUpdated).FullName!;

  // Unconsumed event on a DIFFERENT, non-absorbed namespace → must still be DROPPED.
  private static readonly string _nonAbsorbedUnconsumedType =
    typeof(TestNamespaces.MyApp.Contracts.Events.OrderCreated).FullName!;

  [Test]
  public async Task WithRouting_AbsorbNamespaces_ResolvedDiscardPolicy_KeepsAbsorbedButDropsNonAbsorbedAsync() {
    var services = new ServiceCollection();
    services.AddLogging();

    // Production wiring: AddWhizbang → WithRouting(AbsorbNamespaces) → AddWhizbangWorkers.
    // AddReceptors() mirrors a real bootstrap (registry present) — neither test type is consumed.
    services.AddWhizbang()
      .WithRouting(r => r.AbsorbNamespaces(ABSORBED_NAMESPACE));
    services.AddReceptors();
    services.AddWhizbangWorkers();

    using var provider = services.BuildServiceProvider();
    var policy = provider.GetRequiredService<IMessageDiscardPolicy>();

    // Absorbed namespace, no local consumer → KEPT (persist-for-later; the unconditional
    // event-store write captures it for a future perspective/rebuild).
    var absorbed = policy.EvaluateReceive(
      _absorbedUnconsumedType, topic: "orders-events-queue", subscription: "test-subscription");
    await Assert.That(absorbed.ShouldDiscard).IsFalse();

    // Non-absorbed namespace, no local consumer → still DROPPED. Proves the bypass is
    // namespace-scoped and did not become a blanket "keep everything".
    var nonAbsorbed = policy.EvaluateReceive(
      _nonAbsorbedUnconsumedType, topic: "contracts-events-queue", subscription: "test-subscription");
    await Assert.That(nonAbsorbed.ShouldDiscard).IsTrue();
    await Assert.That(nonAbsorbed.Reason).IsEqualTo(MessageDiscardReason.NoLocalConsumer);
  }
}
