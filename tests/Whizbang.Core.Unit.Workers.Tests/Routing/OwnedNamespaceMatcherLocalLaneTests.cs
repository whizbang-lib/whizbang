// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// The stream-integrity local lane follows registered ownership (#2008): a stored event type is in this service's
/// local lane only when its namespace is owned, or when the service registered no owned domains at all (the
/// behavior before ownership was consulted). The namespace rule is the one <see cref="OwnedNamespaceMatcher"/>
/// already applies for routing, read from the stored wire form of the type name.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Routing/OwnedNamespaceMatcher.cs</code-under-test>
public class OwnedNamespaceMatcherLocalLaneTests {
  private const string ORDERS_TYPE = "App.Contracts.Orders.OrderPlaced, App.Contracts";
  private static readonly HashSet<string> _ownsOrders = new(StringComparer.OrdinalIgnoreCase) { "App.Contracts.Orders" };

  [Test]
  public async Task IsLocalEventType_NoOwnedDomains_EveryTypeIsLocalAsync() {
    await Assert.That(OwnedNamespaceMatcher.IsLocalEventType("Other.Contracts.Shipped, Other", [])).IsTrue()
      .Because("a service that registered no owned domains keeps the lane it always had: every event it holds "
             + "without an origin counts as its own");
  }

  [Test]
  public async Task IsLocalEventType_TypeInAnOwnedNamespace_IsLocalAsync() {
    await Assert.That(OwnedNamespaceMatcher.IsLocalEventType(ORDERS_TYPE, _ownsOrders)).IsTrue();
  }

  [Test]
  public async Task IsLocalEventType_TypeInAChildOfAnOwnedNamespace_IsLocalAsync() {
    await Assert.That(OwnedNamespaceMatcher.IsLocalEventType(
      "App.Contracts.Orders.Events.OrderShipped, App.Contracts", _ownsOrders)).IsTrue();
  }

  [Test]
  public async Task IsLocalEventType_NestedTypeInAnOwnedNamespace_IsLocalAsync() {
    await Assert.That(OwnedNamespaceMatcher.IsLocalEventType(
      "App.Contracts.Orders.OrderEvents+Placed, App.Contracts", _ownsOrders)).IsTrue()
      .Because("a nested type's namespace is its outer type's; the '+' never reads as a namespace segment");
  }

  [Test]
  public async Task IsLocalEventType_TypeOutsideEveryOwnedNamespace_IsNotLocalAsync() {
    await Assert.That(OwnedNamespaceMatcher.IsLocalEventType("App.Contracts.Billing.InvoiceRaised, App.Contracts", _ownsOrders))
      .IsFalse()
      .Because("an event whose namespace this service does not own was authored elsewhere, whatever its origin column says");
  }

  [Test]
  public async Task IsLocalEventType_TypeWithoutANamespace_IsNotLocalWhenDomainsAreOwnedAsync() {
    await Assert.That(OwnedNamespaceMatcher.IsLocalEventType("OrderPlaced", _ownsOrders)).IsFalse();
  }

  [Test]
  public async Task IsLocalEventType_NullOrEmptyType_IsNotLocalWhenDomainsAreOwnedAsync() {
    await Assert.That(OwnedNamespaceMatcher.IsLocalEventType(null, _ownsOrders)).IsFalse();
    await Assert.That(OwnedNamespaceMatcher.IsLocalEventType(string.Empty, _ownsOrders)).IsFalse();
  }

  [Test]
  public async Task IsLocalEventType_NullOwnedDomains_ThrowsAsync() {
    await Assert.That(() => OwnedNamespaceMatcher.IsLocalEventType(ORDERS_TYPE, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task NormalizeDomain_TrimsWhitespaceAndATrailingDotAsync() {
    await Assert.That(OwnedNamespaceMatcher.NormalizeDomain("  App.Contracts.Orders. ")).IsEqualTo("App.Contracts.Orders")
      .Because("\"App.Contracts.Orders.\" and \"App.Contracts.Orders\" declare the same domain, so they exchange as one");
  }

  [Test]
  public async Task NormalizeDomain_Null_ThrowsAsync() {
    await Assert.That(() => OwnedNamespaceMatcher.NormalizeDomain(null!)).Throws<ArgumentNullException>();
  }
}
