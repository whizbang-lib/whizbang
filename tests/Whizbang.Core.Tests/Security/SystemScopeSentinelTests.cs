using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// A message that is MEANT to carry no user authority must say so explicitly, so that a missing
/// scope always means a defect.
/// </summary>
/// <remarks>
/// <para>
/// Control-plane traffic — checkpoints, manifests, re-delivery bundles — is published by background
/// workers with no ambient user, by design. Until now those events were stored with a null scope,
/// which is exactly what a business event looks like after something has dropped its scope. The two
/// are indistinguishable in storage, so "scope is null" could not be treated as a fault.
/// </para>
/// <para>
/// The cost of that ambiguity was measured: an audit of stored scope reported a column fully
/// populated while a seven-figure population of events had lost theirs, and three rounds of
/// investigation went to the read path instead of the writer. A sentinel makes the absent case
/// assertable — an invariant and an alert rather than a forensic exercise.
/// </para>
/// <para>
/// The sentinel marks INTENT, never permission. It must never be mistaken for authority: a system
/// scope grants no tenant, no user, and no principal. Anything else would turn a diagnostic marker
/// into a privilege escalation.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Security/ScopeDelta.cs</code-under-test>
[Category("Security")]
public class SystemScopeSentinelTests {

  [Test]
  public async Task ASystemScopeIsDistinguishableFromAnAbsentOneAsync() {
    var system = ScopeDelta.System;

    await Assert.That(system).IsNotNull()
      .Because("if the intentional case is also null, then null carries two meanings and neither "
             + "can be asserted — which is precisely the ambiguity this exists to remove");
    await Assert.That(system.ApplyTo(null).Scope.IsSystem).IsTrue();
  }

  [Test]
  public async Task ASystemScopeGrantsNoAuthorityAsync() {
    // The security-critical half. The marker says "no user was involved", not "trusted".
    var resolved = ScopeDelta.System.ApplyTo(null).Scope;

    await Assert.That(resolved.TenantId).IsNull()
      .Because("a marker that resolved to a tenant would read as access to that tenant's data — "
             + "turning a diagnostic flag into privilege escalation");
    await Assert.That(resolved.UserId).IsNull();
    await Assert.That(resolved.CustomerId).IsNull();
    await Assert.That(resolved.OrganizationId).IsNull();
  }

  [Test]
  public async Task TheSystemMarkerSurvivesFromPerspectiveScopeAsync() {
    // FromPerspectiveScope returns null when every field is empty, so that an empty object never
    // becomes a hollow authority. The marker must not be swallowed by that same rule.
    var delta = ScopeDelta.FromPerspectiveScope(new PerspectiveScope { IsSystem = true });

    await Assert.That(delta).IsNotNull()
      .Because("collapsing a system-marked scope to null would restore the exact ambiguity the "
             + "marker was added to remove");
    await Assert.That(delta!.ApplyTo(null).Scope.IsSystem).IsTrue();
  }

  [Test]
  public async Task AnEmptyScopeWithNoMarkerStillCollapsesToNullAsync() {
    // The existing contract must hold: an all-empty scope is not an authority and not an intent.
    await Assert.That(ScopeDelta.FromPerspectiveScope(new PerspectiveScope())).IsNull()
      .Because("an empty scope carries neither authority nor a statement of intent, so it must "
             + "stay indistinguishable from nothing at all");
  }

  [Test]
  public async Task TheMarkerRoundTripsOnTheWireAsync() {
    // Stored rows are read back by other services and by operators writing audit queries; a marker
    // that does not survive serialization cannot be relied on for either.
    var json = JsonSerializer.Serialize(new PerspectiveScope { IsSystem = true });
    var restored = JsonSerializer.Deserialize<PerspectiveScope>(json);

    await Assert.That(json).Contains("\"sys\"")
      .Because("the short wire key keeps the sentinel greppable in stored jsonb, which is how an "
             + "operator separates intended-unscoped rows from broken ones");
    await Assert.That(restored!.IsSystem).IsTrue();
  }

  [Test]
  public async Task ASystemScopeIsNotConfusedWithATenantScopeAsync() {
    var tenant = ScopeDelta.FromPerspectiveScope(new PerspectiveScope { TenantId = "tenant-a" });

    await Assert.That(tenant!.ApplyTo(null).Scope.IsSystem).IsFalse()
      .Because("a real tenant scope must never read as system-originated, or the invariant "
             + "'unscoped business events are a bug' would silently exempt real traffic");
  }
}

/// <summary>
/// Where the sentinel is actually stamped, and — just as importantly — where it must not be.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Security/SystemScopeResolver.cs</code-under-test>
[Category("Security")]
public class SystemScopeResolverTests {

  private sealed record _plainEvent : Whizbang.Core.IEvent;

  private sealed record _controlSignal : Whizbang.Core.IEvent, Whizbang.Core.Messaging.IControlPlaneMessage;

  [Test]
  public async Task AControlPlaneMessageWithNoAmbientScopeIsMarkedSystemAsync() {
    var resolved = SystemScopeResolver.ForUnscoped(typeof(_controlSignal));

    await Assert.That(resolved).IsNotNull()
      .Because("control-plane traffic has no ambient user BY DESIGN; saying so explicitly is what "
             + "lets an absent scope be treated as a fault everywhere else");
    await Assert.That(resolved!.ApplyTo(null).Scope.IsSystem).IsTrue();
  }

  [Test]
  public async Task AnOrdinaryMessageIsNeverMarkedSystemAsync() {
    await Assert.That(SystemScopeResolver.ForUnscoped(typeof(_plainEvent))).IsNull()
      .Because("marking a domain event system-originated would exempt it from the invariant and "
             + "hide exactly the class of bug the marker exists to expose");
  }

  [Test]
  public async Task ACompositeIsNeverMarkedSystemEvenWhenItIsControlPlaneAsync() {
    // RedeliveryComposite is registered control-plane — as a TRANSPORT WRAPPER. Its children are
    // domain events that inherit the composite's hop scope at fan-out. Marking the bundle system
    // would stamp that marker onto ordinary business events and silently exempt them.
    await Assert.That(SystemScopeResolver.ForUnscoped(typeof(Whizbang.Core.Minting.RedeliveryComposite))).IsNull()
      .Because("a composite's scope becomes its CHILDREN's scope; a wrapper must never launder a "
             + "system marker onto the domain events it carries");
  }

  [Test]
  public async Task ANullTypeIsNotMarkedSystemAsync() {
    await Assert.That(SystemScopeResolver.ForUnscoped(null)).IsNull()
      .Because("an unknown type is not evidence of intent, and guessing would mark real traffic");
  }
}
