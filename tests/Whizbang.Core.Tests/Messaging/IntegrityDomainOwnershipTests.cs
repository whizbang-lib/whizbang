// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The domain to owner map a consumer learns from the owned domains other services announce on their integrity
/// checkpoints (#2008, #2032). It names the owner of a stored event type by the most specific claimed domain, uses
/// neither claimant of a domain two services claim, and fingerprints itself so re-attribution knows when to rewalk.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityDomainOwnership.cs</code-under-test>
public class IntegrityDomainOwnershipTests {
  private static readonly Guid _orders = Guid.Parse("0a000000-0000-0000-0000-000000000001");
  private static readonly Guid _billing = Guid.Parse("0b000000-0000-0000-0000-000000000002");
  private static readonly Guid _archive = Guid.Parse("0c000000-0000-0000-0000-000000000003");

  private static IntegrityDomainClaim _claim(string domain, Guid owner, string name, string? topic = null) =>
    new() { Domain = domain, OwnerServiceId = owner, OwnerServiceName = name, RequestTopic = topic };

  [Test]
  public async Task FindOwner_TypeInAClaimedDomain_NamesItsOwnerAsync() {
    var ownership = new IntegrityDomainOwnership([_claim("app.contracts.orders", _orders, "orders-svc", "orders.requests")]);

    var owner = ownership.FindOwner("App.Contracts.Orders.OrderPlaced, App.Contracts");

    await Assert.That(owner).IsNotNull();
    await Assert.That(owner!.OwnerServiceId).IsEqualTo(_orders);
    await Assert.That(owner.OwnerServiceName).IsEqualTo("orders-svc");
    await Assert.That(owner.RequestTopic).IsEqualTo("orders.requests")
      .Because("domains match without case, as routing matches them, and the claim carries the owner's request address");
  }

  [Test]
  public async Task FindOwner_TypeInNoClaimedDomain_IsNullAsync() {
    var ownership = new IntegrityDomainOwnership([_claim("app.contracts.orders", _orders, "orders-svc")]);

    await Assert.That(ownership.FindOwner("App.Contracts.Billing.InvoiceRaised, App.Contracts")).IsNull();
    await Assert.That(ownership.FindOwner("NoNamespace")).IsNull();
  }

  [Test]
  public async Task FindOwner_NestedDomainsClaimedByDifferentServices_TheMostSpecificWinsAsync() {
    var ownership = new IntegrityDomainOwnership([
      _claim("app.contracts", _orders, "orders-svc"),
      _claim("app.contracts.archive", _archive, "archive-svc"),
    ]);

    await Assert.That(ownership.FindOwner("App.Contracts.Archive.Archived, App.Contracts")!.OwnerServiceId).IsEqualTo(_archive)
      .Because("a narrower declaration is the more specific statement of who authors that namespace");
    await Assert.That(ownership.FindOwner("App.Contracts.Placed, App.Contracts")!.OwnerServiceId).IsEqualTo(_orders);
  }

  [Test]
  public async Task FindOwner_ADomainTwoServicesClaim_UsesNeitherAsync() {
    var ownership = new IntegrityDomainOwnership([
      _claim("app.contracts.orders", _orders, "orders-svc"),
      _claim("app.contracts.orders", _billing, "billing-svc"),
      _claim("app.contracts", _archive, "archive-svc"),
    ]);

    await Assert.That(ownership.FindOwner("App.Contracts.Orders.OrderPlaced, App.Contracts")).IsNull()
      .Because("a contested domain has no owner, and a parent claim must not stand in for it");
    await Assert.That(ownership.ConflictedDomains).IsEquivalentTo(["app.contracts.orders"]);
    await Assert.That(ownership.Owners.Select(o => o.Domain)).IsEquivalentTo(["app.contracts"]);
  }

  [Test]
  public async Task FindOwner_TheSameOwnerClaimingADomainTwice_IsNoConflictAsync() {
    var ownership = new IntegrityDomainOwnership([
      _claim("App.Contracts.Orders", _orders, "orders-svc"),
      _claim("app.contracts.orders.", _orders, "orders-svc"),
    ]);

    await Assert.That(ownership.ConflictedDomains).IsEmpty();
    await Assert.That(ownership.FindOwner("App.Contracts.Orders.OrderPlaced, App.Contracts")!.OwnerServiceId).IsEqualTo(_orders);
  }

  [Test]
  public async Task IsNotAuthoritative_ATypeNobodyClaims_TakesTheOriginAtItsWordAsync() {
    var ownership = new IntegrityDomainOwnership([_claim("app.contracts.orders", _orders, "orders-svc")]);

    await Assert.That(ownership.IsNotAuthoritative("App.Contracts.Billing.Raised, App.Contracts", _billing, ["App.Contracts.Self"]))
      .IsFalse()
      .Because("with no known owner and the type outside this service's own domains, the advertising origin stands, as before");
  }

  [Test]
  public async Task IsNotAuthoritative_ATypeAnotherServiceOwns_IsNotTheAdvertisingOriginsAsync() {
    var ownership = new IntegrityDomainOwnership([_claim("app.contracts.orders", _orders, "orders-svc")]);
    const string placed = "App.Contracts.Orders.OrderPlaced, App.Contracts";

    await Assert.That(ownership.IsNotAuthoritative(placed, _billing, [])).IsTrue()
      .Because("billing reporting an orders event received it; orders is the one whose count stands");
    await Assert.That(ownership.IsNotAuthoritative(placed, _orders, [])).IsFalse()
      .Because("the control: the owner's own count stands");
  }

  [Test]
  public async Task IsNotAuthoritative_ATypeThisServiceOwns_IsNoOtherOriginsAsync() {
    var ownership = new IntegrityDomainOwnership([_claim("app.contracts.billing", _billing, "billing-svc")]);
    const string placed = "App.Contracts.Orders.OrderPlaced, App.Contracts";

    await Assert.That(ownership.IsNotAuthoritative(placed, _billing, ["App.Contracts.Orders"])).IsTrue()
      .Because("this service authors its own domain's events, so another origin reporting them only received them; "
             + "a service never announces its own domains to itself, so the map alone cannot say so");
    await Assert.That(ownership.IsNotAuthoritative(placed, _billing, [])).IsFalse()
      .Because("the control: with no owner known anywhere, the reporting origin is taken at its word, as before");
  }

  [Test]
  public async Task IsNotAuthoritative_AnOriginTheMapNamesAsOwner_StandsEvenInADomainThisServiceAlsoOwnsAsync() {
    var ownership = new IntegrityDomainOwnership([_claim("app.contracts.orders", _orders, "orders-svc")]);

    await Assert.That(ownership.IsNotAuthoritative("App.Contracts.Orders.OrderPlaced, App.Contracts", _orders, ["App.Contracts.Orders"]))
      .IsFalse()
      .Because("two services claiming one domain is a contest the map reports, not a verdict that the claimant's count is false");
  }

  [Test]
  public async Task IsNotAuthoritative_WithoutTheLocalDomains_ThrowsAsync() {
    var ownership = new IntegrityDomainOwnership([]);

    await Assert.That(() => ownership.IsNotAuthoritative("App.Contracts.Orders.OrderPlaced, App.Contracts", _orders, null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Fingerprint_IsOrderIndependent_AndMovesWithTheMapAndTheOwnDomainsAsync() {
    var a = new IntegrityDomainOwnership([_claim("x", _orders, "o"), _claim("y", _billing, "b")]);
    var b = new IntegrityDomainOwnership([_claim("Y", _billing, "b"), _claim("x", _orders, "o")]);
    var moved = new IntegrityDomainOwnership([_claim("x", _orders, "o"), _claim("y", _archive, "a")]);

    await Assert.That(a.Fingerprint(["App.Own"])).IsEqualTo(b.Fingerprint(["app.own."]));
    await Assert.That(moved.Fingerprint(["App.Own"])).IsNotEqualTo(a.Fingerprint(["App.Own"]))
      .Because("a domain that changed owner is what a re-attribution rewalk is for");
    await Assert.That(a.Fingerprint(["App.Other"])).IsNotEqualTo(a.Fingerprint(["App.Own"]));
  }

  [Test]
  public async Task Empty_HasNoOwners_AndFindsNothingAsync() {
    await Assert.That(IntegrityDomainOwnership.Empty.Owners).IsEmpty();
    await Assert.That(IntegrityDomainOwnership.Empty.FindOwner("App.Contracts.Orders.OrderPlaced, App.Contracts")).IsNull();
  }

  [Test]
  public async Task Constructor_NullClaims_ThrowsAsync() {
    await Assert.That(() => new IntegrityDomainOwnership(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => IntegrityDomainOwnership.Empty.Fingerprint(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Conflicts_WarnOncePerPair_InEitherOrderAsync() {
    var conflicts = new IntegrityOwnershipConflicts();

    await Assert.That(conflicts.FirstSighting("app.contracts.orders", _orders, _billing)).IsTrue();
    await Assert.That(conflicts.FirstSighting("APP.CONTRACTS.ORDERS", _billing, _orders)).IsFalse()
      .Because("the pair is unordered and the domain compares without case: both sides' checkpoints report one conflict");
    await Assert.That(conflicts.FirstSighting("app.contracts.orders", _orders, _archive)).IsTrue();
    await Assert.That(conflicts.FirstSighting("app.contracts.billing", _orders, _billing)).IsTrue();
  }

  [Test]
  public async Task Constructor_AnEmptyDomain_AndAThirdClaimantOfAContestedDomain_AreIgnoredAsync() {
    var ownership = new IntegrityDomainOwnership([
      _claim(" . ", _orders, "orders-svc"),
      _claim("app.contested", _orders, "orders-svc"),
      _claim("app.contested", _billing, "billing-svc"),
      _claim("app.contested", _archive, "archive-svc"),
    ]);

    await Assert.That(ownership.Owners).IsEmpty()
      .Because("a blank declaration claims nothing, and a contested domain stays contested whoever else claims it");
    await Assert.That(ownership.ConflictedDomains).IsEquivalentTo(["app.contested"]);
  }

  [Test]
  public async Task FindOwner_TheMostSpecificWins_WhateverOrderTheClaimsArriveInAsync() {
    var ownership = new IntegrityDomainOwnership([
      _claim("app.contracts.archive", _archive, "archive-svc"),
      _claim("app.contracts", _orders, "orders-svc"),
    ]);

    await Assert.That(ownership.FindOwner("App.Contracts.Archive.Archived, App.Contracts")!.OwnerServiceId).IsEqualTo(_archive);
  }

  [Test]
  public async Task Fingerprint_AContestedDomain_IsPartOfIt_AndDiffersFromTheUncontestedMapAsync() {
    var uncontested = new IntegrityDomainOwnership([_claim("app.orders", _orders, "orders-svc")]);
    var contested = new IntegrityDomainOwnership([
      _claim("app.orders", _orders, "orders-svc"),
      _claim("app.orders", _billing, "billing-svc"),
    ]);
    var otherContested = new IntegrityDomainOwnership([
      _claim("app.billing", _orders, "orders-svc"),
      _claim("app.billing", _billing, "billing-svc"),
    ]);

    await Assert.That(contested.Fingerprint(["app.own"])).IsNotEqualTo(uncontested.Fingerprint(["app.own"]))
      .Because("a domain becoming contested changes who owns it, so the re-attribution walk must restart");
    await Assert.That(contested.Fingerprint(["app.own"])).IsNotEqualTo(otherContested.Fingerprint(["app.own"]))
      .Because("which domain is contested is part of the fingerprint, not only that one is");
  }
}
