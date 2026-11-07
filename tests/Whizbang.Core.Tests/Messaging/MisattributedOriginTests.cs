// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The true owner of an event recorded under an origin that does not own its type (#2116). A service that answered a
/// redelivery with events it had only received named itself as their origin; the owner this receiver's ownership map
/// names replaces it, and every other case leaves the record as it is.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/MisattributedOrigin.cs</code-under-test>
[Category("Unit")]
public class MisattributedOriginTests {
  private static readonly Guid _orders = Guid.Parse("0a000000-0000-0000-0000-000000000001");
  private static readonly Guid _relay = Guid.Parse("0b000000-0000-0000-0000-000000000002");
  private static readonly Guid _billing = Guid.Parse("0c000000-0000-0000-0000-000000000003");
  private const string ORDER_PLACED = "App.Contracts.Orders.OrderPlaced, App.Contracts";

  private static IntegrityDomainOwnership _ownership(params (string Domain, Guid Owner)[] claims) =>
    new([.. claims.Select(c => new IntegrityDomainClaim { Domain = c.Domain, OwnerServiceId = c.Owner, OwnerServiceName = "svc" })]);

  [Test]
  public async Task TrueOwner_ATypeAnotherServiceOwns_IsThatOwnerAsync() {
    var owner = MisattributedOrigin.TrueOwner(ORDER_PLACED, _relay, _ownership(("app.contracts.orders", _orders)), []);

    await Assert.That(owner).IsEqualTo(_orders)
      .Because("the relay named itself as origin of an event whose domain another service announced it owns");
  }

  [Test]
  public async Task TrueOwner_ATypeTheNamedOriginOwns_StandsAsync() {
    await Assert.That(MisattributedOrigin.TrueOwner(ORDER_PLACED, _orders, _ownership(("app.contracts.orders", _orders)), [])).IsNull();
  }

  [Test]
  public async Task TrueOwner_ATypeWithNoKnownOwner_StandsAsync() {
    await Assert.That(MisattributedOrigin.TrueOwner(ORDER_PLACED, _relay, _ownership(("app.contracts.billing", _billing)), [])).IsNull()
      .Because("without a known owner there is nothing to re-attribute to; the record is left as it is");
  }

  [Test]
  public async Task TrueOwner_AContestedDomain_StandsAsync() {
    var ownership = _ownership(("app.contracts.orders", _orders), ("app.contracts.orders", _billing));

    await Assert.That(MisattributedOrigin.TrueOwner(ORDER_PLACED, _relay, ownership, [])).IsNull()
      .Because("a domain two services claim has no owner, so neither is trusted over the recorded origin");
  }

  [Test]
  public async Task TrueOwner_ATypeThisServiceOwns_StandsAsync() {
    var ownership = _ownership(("app.contracts", _orders));

    await Assert.That(MisattributedOrigin.TrueOwner(ORDER_PLACED, _relay, ownership, ["App.Contracts.Orders"])).IsNull()
      .Because("this service owns the namespace itself; a parent claim another service announced does not move its events");
    await Assert.That(MisattributedOrigin.TrueOwner(ORDER_PLACED, _relay, ownership, ["App.Contracts.Billing"])).IsEqualTo(_orders)
      .Because("owning an unrelated domain changes nothing");
  }

  [Test]
  public async Task TrueOwner_NullArguments_ThrowAsync() {
    await Assert.That(() => MisattributedOrigin.TrueOwner(ORDER_PLACED, _relay, null!, [])).Throws<ArgumentNullException>();
    await Assert.That(() => MisattributedOrigin.TrueOwner(ORDER_PLACED, _relay, IntegrityDomainOwnership.Empty, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ForBundle_WithNoKnownOwner_IsNullAsync() {
    await Assert.That(MisattributedOrigin.ForBundle(_relay, IntegrityDomainOwnership.Empty, [])).IsNull()
      .Because("a receiver that learned no owners does no per-child work");
  }

  [Test]
  public async Task ForBundle_AnswersEachChildTypeAgainstTheBundlesOriginAsync() {
    var lookup = MisattributedOrigin.ForBundle(_relay, _ownership(("app.contracts.orders", _orders), ("app.contracts.relay", _relay)), []);

    await Assert.That(lookup).IsNotNull();
    await Assert.That(lookup!(ORDER_PLACED)).IsEqualTo(_orders);
    await Assert.That(lookup("App.Contracts.Relay.Forwarded, App.Contracts")).IsNull()
      .Because("the bundle's origin owns this type, so its origin stands");
  }

  [Test]
  public async Task ForBundle_NullArguments_ThrowAsync() {
    await Assert.That(() => MisattributedOrigin.ForBundle(_relay, null!, [])).Throws<ArgumentNullException>();
    await Assert.That(() => MisattributedOrigin.ForBundle(_relay, IntegrityDomainOwnership.Empty, null!)).Throws<ArgumentNullException>();
  }
}
