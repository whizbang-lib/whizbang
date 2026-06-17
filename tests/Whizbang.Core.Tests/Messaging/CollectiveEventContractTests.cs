#pragma warning disable CA1707

using System.Collections.Generic;
using System.Linq;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the <see cref="ICollectiveEvent"/> contract — the marker interface
/// the collective-events feature (Slices 1–10) builds on. The contract
/// itself has no behavior; these tests pin the shape so subsequent slices
/// can ship without churning the surface.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public class CollectiveEventContractTests {

  [Test]
  public async Task ICollectiveEvent_ExtendsIMessage_SoExistingPipelinesCanCarryItAsync() {
    // Collective events flow through the same dispatcher / outbox /
    // transport surface as ordinary events. That only works if the root
    // is IMessage; a collective that did NOT extend IMessage would need
    // a parallel dispatch path.
    ICollectiveEvent evt = new _archiveJobsCollectiveEvent(
      new _tenantCollectiveScope("11111111-1111-1111-1111-111111111111"),
      []);

    await Assert.That(evt is IMessage).IsTrue()
      .Because("ICollectiveEvent : IMessage so the existing dispatcher / outbox / transport — which all constrain on IMessage — carry it as-is. No parallel pipeline.");
  }

  [Test]
  public async Task ICollectiveEvent_MatchedStreamIds_IsImmutableSnapshotAsync() {
    // The matched set is captured at write time and immutable thereafter.
    // The contract exposes IReadOnlyList<Guid>, not List<Guid>, so callers
    // can't mutate the snapshot after construction. This is load-bearing
    // for the snapshot-determinism invariant (Locked Decisions B): replay
    // reads the captured set as-is, immune to subsequent state changes.
    var streamA = TrackedGuid.NewMedo();
    var streamB = TrackedGuid.NewMedo();
    ICollectiveEvent evt = new _archiveJobsCollectiveEvent(
      new _tenantCollectiveScope("11111111-1111-1111-1111-111111111111"),
      [streamA, streamB]);

    // Compile-time: IReadOnlyList<Guid> has no Add/Remove. Runtime: assert
    // the list reflects what was passed.
    IReadOnlyList<Guid> ids = evt.MatchedStreamIds;

    await Assert.That(ids.Count).IsEqualTo(2);
    await Assert.That(ids[0]).IsEqualTo(streamA);
    await Assert.That(ids[1]).IsEqualTo(streamB);
  }

  [Test]
  public async Task ICollectiveEvent_Scope_CarriedThroughEvent_Async() {
    // Scope drives runtime routing (which perspectives accept this event)
    // and authorization filter composition. The event MUST carry it; an
    // event without a scope can't be routed by ICollectiveScopeResolver.
    var scope = new _tenantCollectiveScope("11111111-1111-1111-1111-111111111111");
    ICollectiveEvent evt = new _archiveJobsCollectiveEvent(scope, []);

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
  public async Task ICollectiveEvent_MatchedStreamIds_EmptySetIsValidAsync() {
    // A collective event with an empty matched set is well-formed —
    // semantically "this mutation applied to zero rows" — and must not
    // throw at construction. The runner handles the no-op apply.
    ICollectiveEvent evt = new _archiveJobsCollectiveEvent(
      new _tenantCollectiveScope("11111111-1111-1111-1111-111111111111"),
      []);

    await Assert.That(evt.MatchedStreamIds).IsNotNull();
    await Assert.That(evt.MatchedStreamIds.Count).IsEqualTo(0);
  }

  // ── Inline test types ──────────────────────────────────────────────────

  private sealed record _archiveJobsCollectiveEvent(
    ICollectiveScope Scope,
    IReadOnlyList<Guid> MatchedStreamIds) : ICollectiveEvent;

  private sealed record _tenantCollectiveScope(string TenantId) : ICollectiveScope {
    public string ScopeKind => "tenant";
  }

  private sealed record _globalCollectiveScope : ICollectiveScope {
    public string ScopeKind => "global";
  }
}
