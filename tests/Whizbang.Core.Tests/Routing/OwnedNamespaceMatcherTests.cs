using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// The one hierarchical ownership rule: a namespace is owned when it exactly matches an owned domain
/// or is a child of one (owned prefix followed by a <c>.</c> boundary), case-insensitively. Event
/// subscription discovery and the owned-and-subscribed guard must agree on it, so it lives in one
/// place rather than as inline copies that can drift.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Routing/OwnedNamespaceMatcher.cs</code-under-test>
public class OwnedNamespaceMatcherTests {
  private static readonly HashSet<string> _owned = new(StringComparer.OrdinalIgnoreCase) { "app.contracts.orders" };

  [Test]
  public async Task IsOwned_ExactMatch_IsOwnedAsync() {
    await Assert.That(OwnedNamespaceMatcher.IsOwned("app.contracts.orders", _owned)).IsTrue();
  }

  [Test]
  public async Task IsOwned_ChildNamespace_IsOwnedAsync() {
    await Assert.That(OwnedNamespaceMatcher.IsOwned("app.contracts.orders.events", _owned)).IsTrue()
      .Because("a child of an owned domain is owned — the '.' boundary is what makes it a child");
  }

  [Test]
  public async Task IsOwned_SiblingSharingThePrefixWithoutADotBoundary_IsNotOwnedAsync() {
    await Assert.That(OwnedNamespaceMatcher.IsOwned("app.contracts.ordersarchive", _owned)).IsFalse()
      .Because("a shared textual prefix is not ownership; only a dot-separated segment is");
  }

  [Test]
  public async Task IsOwned_CaseDiffers_IsOwnedAsync() {
    await Assert.That(OwnedNamespaceMatcher.IsOwned("App.Contracts.ORDERS.Events", _owned)).IsTrue();
  }

  [Test]
  public async Task IsOwned_OwnedDomainDeclaredWithTrailingDot_MatchesChildrenWithoutDoublingTheDotAsync() {
    var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "app.contracts.bff." };

    await Assert.That(OwnedNamespaceMatcher.IsOwned("app.contracts.bff.events", owned)).IsTrue();
  }

  [Test]
  public async Task IsOwned_NullOrEmptyCandidate_IsNotOwnedAsync() {
    await Assert.That(OwnedNamespaceMatcher.IsOwned(null, _owned)).IsFalse();
    await Assert.That(OwnedNamespaceMatcher.IsOwned(string.Empty, _owned)).IsFalse();
  }

  [Test]
  public async Task IsOwned_NoOwnedDomains_IsNotOwnedAsync() {
    await Assert.That(OwnedNamespaceMatcher.IsOwned("app.contracts.orders", [])).IsFalse()
      .Because("a service that declares no owned domains owns nothing");
  }

  [Test]
  public async Task IsOwned_OwnedDomainThatIsOnlyADot_OwnsNothingAsync() {
    // "." normalizes to an empty domain; an empty prefix would otherwise match every namespace.
    var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "." };

    await Assert.That(OwnedNamespaceMatcher.IsOwned("app.contracts.orders", owned)).IsFalse();
    await Assert.That(OwnedNamespaceMatcher.FindOwner("app.contracts.orders", owned)).IsNull();
  }

  [Test]
  public async Task IsOwned_NullOwnedDomains_ThrowsAsync() {
    await Assert.That(() => OwnedNamespaceMatcher.IsOwned("app.contracts.orders", null!))
      .Throws<ArgumentNullException>();
  }
}
