// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The domain claims a consumer persists from the owned domains other services announce on their checkpoints
/// (#2008), against the real <c>wh_integrity_record_domain_claims</c>: an owner's announcement replaces its claim set,
/// domains key without case, an absent request address keeps the known one, and a domain another owner already claims
/// comes back as a conflict.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/321_LocalLaneFollowsOwnership.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityDomainClaimsSqlTests : EFCoreTestBase {
  private static readonly Guid _orders = Guid.Parse("0a000000-0000-0000-0000-0000000000a1");
  private static readonly Guid _billing = Guid.Parse("0b000000-0000-0000-0000-0000000000b2");

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  private IWorkCoordinator _coordinator(WorkCoordinatorGate? gate = null) {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), gate: gate);
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  [Test]
  public async Task Record_StoresTheOwnersClaims_KeyedWithoutCaseAsync() {
    var coordinator = _coordinator();

    var conflicts = await coordinator.RecordIntegrityDomainClaimsAsync(
      _orders, "orders-svc", "orders.requests", ["App.Contracts.Orders", "app.contracts.shipping"]);
    var claims = await coordinator.GetIntegrityDomainClaimsAsync();

    await Assert.That(conflicts).IsEmpty();
    await Assert.That(claims.Select(c => c.Domain)).IsEquivalentTo(["app.contracts.orders", "app.contracts.shipping"]);
    await Assert.That(claims.All(c => c.OwnerServiceId == _orders && c.OwnerServiceName == "orders-svc"
                                  && c.RequestTopic == "orders.requests")).IsTrue();
  }

  [Test]
  public async Task Record_AnAnnouncementReplacesTheOwnersClaimSetAsync() {
    var coordinator = _coordinator();
    await coordinator.RecordIntegrityDomainClaimsAsync(_orders, "orders-svc", "orders.requests", ["app.a", "app.b"]);

    await coordinator.RecordIntegrityDomainClaimsAsync(_orders, "orders-svc", requestTopic: null, ["app.a"]);
    var claims = await coordinator.GetIntegrityDomainClaimsAsync();

    await Assert.That(claims.Select(c => c.Domain)).IsEquivalentTo(["app.a"])
      .Because("a service that stopped claiming a domain gives it up");
    await Assert.That(claims.Single().RequestTopic).IsEqualTo("orders.requests")
      .Because("an announcement without a request address keeps the one already known");
  }

  [Test]
  public async Task Record_AnEmptyAnnouncement_RemovesEveryClaimOfThatOwnerOnlyAsync() {
    var coordinator = _coordinator();
    await coordinator.RecordIntegrityDomainClaimsAsync(_orders, "orders-svc", null, ["app.a"]);
    await coordinator.RecordIntegrityDomainClaimsAsync(_billing, "billing-svc", null, ["app.b"]);

    await coordinator.RecordIntegrityDomainClaimsAsync(_orders, "orders-svc", null, []);

    await Assert.That((await coordinator.GetIntegrityDomainClaimsAsync()).Select(c => c.Domain)).IsEquivalentTo(["app.b"]);
  }

  [Test]
  public async Task Record_ADomainAnotherOwnerClaims_ComesBackAsAConflictAsync() {
    var coordinator = _coordinator();
    await coordinator.RecordIntegrityDomainClaimsAsync(_orders, "orders-svc", null, ["app.contracts.orders", "app.x"]);

    var conflicts = await coordinator.RecordIntegrityDomainClaimsAsync(_billing, "billing-svc", null, ["APP.CONTRACTS.ORDERS", "app.y"]);

    var conflict = conflicts.Single();
    await Assert.That(conflict.Domain).IsEqualTo("app.contracts.orders");
    await Assert.That(conflict.OwnerServiceId).IsEqualTo(_orders);
    await Assert.That(conflict.OwnerServiceName).IsEqualTo("orders-svc");
    await Assert.That((await coordinator.GetIntegrityDomainClaimsAsync()).Count(c => c.Domain == "app.contracts.orders")).IsEqualTo(2)
      .Because("both claims are kept; the map uses neither while two owners contest the domain");
  }

  [Test]
  public async Task Claims_ThroughAThroughputGatedCoordinator_RecordAndReadAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1,
      logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorGate>.Instance);
    var coordinator = _coordinator(gate);

    await coordinator.RecordIntegrityDomainClaimsAsync(_orders, "orders-svc", "orders.requests", ["app.gated"]);

    await Assert.That((await coordinator.GetIntegrityDomainClaimsAsync()).Single().Domain).IsEqualTo("app.gated");
  }
}
