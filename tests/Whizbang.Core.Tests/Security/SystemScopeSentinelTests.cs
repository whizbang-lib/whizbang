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

/// <summary>
/// Separating a developer's intentionally-unscoped event from one that LOST its scope.
/// </summary>
/// <remarks>
/// <para>
/// The system marker covers framework infrastructure only. It does not cover the other legitimate
/// reason an event carries no scope: the application author knows there is none. A login attempt
/// has no authenticated user YET; a health check has none ever. Those are ordinary domain events,
/// not control-plane traffic, so they stayed null — indistinguishable from an event whose scope was
/// dropped, which is the exact ambiguity the marker was introduced to remove.
/// </para>
/// <para>
/// The two must not share a marker. "The framework published this" and "an author asserted this"
/// deserve different scrutiny in a security review, and if application code could claim the system
/// marker it would become a blanket way to silence the invariant.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Security/SystemScopeResolver.cs</code-under-test>
[Category("Security")]
public class DeclaredUnscopedMarkerTests {

  private sealed record _loginAttempt : Whizbang.Core.IEvent;

  private sealed record _ordinaryEvent : Whizbang.Core.IEvent;

  private sealed record _controlSignal : Whizbang.Core.IEvent, Whizbang.Core.Messaging.IControlPlaneMessage;

  private static readonly HashSet<Type> _declared = [typeof(_loginAttempt)];

  [Test]
  public async Task ADeclaredTypeIsMarkedDeclaredRatherThanLeftBlankAsync() {
    var scope = SystemScopeResolver.ForUnscoped(typeof(_loginAttempt), _declared);

    await Assert.That(scope).IsNotNull()
      .Because("a login attempt has no authenticated user by design; leaving it blank makes it "
             + "identical to an event that lost its scope and turns the invariant into noise");
    await Assert.That(scope!.ApplyTo(null).Scope.IsDeclaredUnscoped).IsTrue();
  }

  [Test]
  public async Task ADeclaredTypeIsNotMarkedSystemAsync() {
    var scope = SystemScopeResolver.ForUnscoped(typeof(_loginAttempt), _declared);

    await Assert.That(scope!.ApplyTo(null).Scope.IsSystem).IsFalse()
      .Because("the system marker means framework infrastructure. If application code could claim "
             + "it, it would become a blanket way to silence the invariant, and an auditor could "
             + "no longer tell what the framework did from what an author asserted");
  }

  [Test]
  public async Task ControlPlaneStillWinsOverADeclarationAsync() {
    var scope = SystemScopeResolver.ForUnscoped(typeof(_controlSignal), new HashSet<Type> { typeof(_controlSignal) });

    await Assert.That(scope!.ApplyTo(null).Scope.IsSystem).IsTrue()
      .Because("framework traffic is framework traffic whether or not someone also listed it; the "
             + "provenance an auditor sees must not depend on a consumer's configuration");
  }

  [Test]
  public async Task AnUndeclaredDomainEventIsStillLeftBlankAsync() {
    await Assert.That(SystemScopeResolver.ForUnscoped(typeof(_ordinaryEvent), _declared)).IsNull()
      .Because("this is the case the whole invariant exists to catch — if an undeclared event were "
             + "marked, a dropped scope would look intentional");
  }

  [Test]
  public async Task ADeclaredUnscopedScopeGrantsNoAuthorityAsync() {
    var resolved = SystemScopeResolver.ForUnscoped(typeof(_loginAttempt), _declared)!.ApplyTo(null).Scope;

    await Assert.That(resolved.TenantId).IsNull()
      .Because("declaring an event unscoped states that no authority exists, so the marker must "
             + "never resolve to one — least of all on a pre-authentication event");
    await Assert.That(resolved.UserId).IsNull();
  }

  [Test]
  public async Task TheDeclaredMarkerRoundTripsOnTheWireAsync() {
    var json = System.Text.Json.JsonSerializer.Serialize(
      new PerspectiveScope { IsDeclaredUnscoped = true });

    await Assert.That(json).Contains("\"dec\"")
      .Because("an operator separates intended-unscoped rows from broken ones by querying stored "
             + "jsonb, so the marker has to survive serialization to be worth anything");
    await Assert.That(System.Text.Json.JsonSerializer.Deserialize<PerspectiveScope>(json)!.IsDeclaredUnscoped).IsTrue();
  }
}

/// <summary>
/// The publish path must mark control-plane traffic too, not just the send path.
/// </summary>
/// <remarks>
/// <para>
/// The marker was wired into the envelope builder used by Send and LocalInvoke. PublishAsync does
/// not use it: it captures an ambient source envelope and hands that to the outbox hop builder,
/// which resolves scope hop-first then falls back to ambient — with no system fallback at all.
/// </para>
/// <para>
/// Control-plane events are PUBLISHED, so that was the path that mattered most, and it was the one
/// left uncovered. Observed on a deployment running the marker build: one hundred coverage-gap
/// events written in a single burst, every one with a null scope and no scope on its hop, while
/// the invariant reported them as defects. The marking looked complete because the send path
/// worked.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Security/OutboxHopScope.cs</code-under-test>
[Category("Security")]
public class PublishPathSystemScopeTests {

  private sealed record _controlSignal : Whizbang.Core.IEvent, Whizbang.Core.Messaging.IControlPlaneMessage;

  private sealed record _plainEvent : Whizbang.Core.IEvent;

  [Test]
  public async Task ControlPlaneIsMarkedWhenNothingElseResolvesAsync() {
    var resolved = OutboxHopScope.Resolve(sourceEnvelope: null, typeof(_controlSignal), declaredUnscopedTypes: null);

    await Assert.That(resolved).IsNotNull()
      .Because("control-plane events are PUBLISHED, so a marker wired only into the send path "
             + "leaves the traffic it was built for unmarked and the invariant unusable");
    await Assert.That(resolved!.ApplyTo(null).Scope.IsSystem).IsTrue();
  }

  [Test]
  public async Task AnOrdinaryEventStaysUnmarkedOnThePublishPathAsync() {
    await Assert.That(OutboxHopScope.Resolve(sourceEnvelope: null, typeof(_plainEvent), null)).IsNull()
      .Because("the fallback must not fire for domain events, or a scope dropped on the publish "
             + "path would start looking intentional");
  }

  [Test]
  public async Task ADeclaredTypeIsMarkedOnThePublishPathAsync() {
    var declared = new HashSet<Type> { typeof(_plainEvent) };
    var resolved = OutboxHopScope.Resolve(sourceEnvelope: null, typeof(_plainEvent), declared);

    await Assert.That(resolved!.ApplyTo(null).Scope.IsDeclaredUnscoped).IsTrue()
      .Because("an author's declaration has to hold on the publish path too, or their exempted "
             + "events read as defects");
  }
}
