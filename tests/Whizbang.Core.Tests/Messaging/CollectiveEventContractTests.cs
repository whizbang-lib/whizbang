#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the <see cref="ICollectiveEvent"/> contract — the marker interface
/// the collective-events feature builds on. The contract itself has no
/// behavior; these tests pin the shape so subsequent slices can ship
/// without churning the surface.
/// </summary>
/// <remarks>
/// The contract is intentionally minimal — just <c>Scope</c>. There is
/// no captured matched-stream-id set, no defensive cap, no audit pointer.
/// Determinism is at scope level (see the docs page) and the event IS
/// the descriptor.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
public class CollectiveEventContractTests {

  [Test]
  public async Task ICollectiveEvent_ExtendsIMessage_SoExistingPipelinesCanCarryItAsync() {
    // Collective events flow through the same dispatcher / outbox /
    // transport surface as ordinary events. That only works if the root
    // is IMessage; a collective that did NOT extend IMessage would need
    // a parallel dispatch path.
    ICollectiveEvent evt = new _archiveJobsCollectiveEvent(
      new _tenantCollectiveScope("11111111-1111-1111-1111-111111111111"));

    await Assert.That(evt is IMessage).IsTrue()
      .Because("ICollectiveEvent : IMessage so the existing dispatcher / outbox / transport — which all constrain on IMessage — carry it as-is. No parallel pipeline.");
  }

  [Test]
  public async Task ICollectiveEvent_Scope_CarriedThroughEvent_Async() {
    // Scope drives runtime routing (which perspectives accept this event)
    // and is the SOLE source of the WHERE clause for the SQL UPDATE.
    // The event MUST carry it; an event without a scope can't be routed.
    var scope = new _tenantCollectiveScope("11111111-1111-1111-1111-111111111111");
    ICollectiveEvent evt = new _archiveJobsCollectiveEvent(scope);

    await Assert.That(evt.Scope).IsNotNull();
    await Assert.That(ReferenceEquals(evt.Scope, scope)).IsTrue()
      .Because("The contract holds the scope reference as-passed — no defensive copy. Resolvers downcast on ScopeKind, so identity preservation matters for performance and predictability.");
  }

  [Test]
  public async Task ICollectiveScope_ScopeKind_DiscriminatesResolverLookupAsync() {
    // Resolvers are DI-registered keyed by ScopeKind. The string is the
    // runtime discriminator that maps Scope payload → resolver impl.
    // Different scope types MUST return different discriminators.
    ICollectiveScope tenant = new _tenantCollectiveScope("11111111-1111-1111-1111-111111111111");
    ICollectiveScope global = new _globalCollectiveScope();

    await Assert.That(tenant.ScopeKind).IsEqualTo("tenant")
      .Because("Built-in tenant scope must use a stable kind so TenantCollectiveScopeResolver (Slice 4) can register against it.");
    await Assert.That(global.ScopeKind).IsEqualTo("global");
    await Assert.That(tenant.ScopeKind).IsNotEqualTo(global.ScopeKind)
      .Because("Different scope types MUST have different kinds so the resolver lookup is unambiguous.");
  }

  [Test]
  public async Task ICollectiveEvent_ContractIsMinimal_OnlyScopeAsync() {
    // Document the deliberate constraint: ICollectiveEvent has exactly
    // ONE property — Scope. No MatchedStreamIds, no MaxExpandedInnersAllowed,
    // no audit pointer. The event IS the descriptor. Adding properties
    // here is a substantial design decision (and a wire-format change).
    var props = typeof(ICollectiveEvent).GetProperties();

    await Assert.That(props.Length).IsEqualTo(1)
      .Because("The whole point of the scope-level-determinism design is that the event carries only its scope payload. Adding properties to this contract reopens questions we've already settled.");
    await Assert.That(props[0].Name).IsEqualTo(nameof(ICollectiveEvent.Scope));
  }

  // ── Inline test types ──────────────────────────────────────────────────

  private sealed record _archiveJobsCollectiveEvent(ICollectiveScope Scope) : ICollectiveEvent;

  private sealed record _tenantCollectiveScope(string TenantId) : ICollectiveScope {
    public string ScopeKind => "tenant";
  }

  private sealed record _globalCollectiveScope : ICollectiveScope {
    public string ScopeKind => "global";
  }
}
