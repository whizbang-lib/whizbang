// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The repair ledger forgets rows recorded under an origin that does not own the row's event type (migration 370). An
/// origin's answer leaves out every type it does not own (#2008), so such a row is never compared again: it can neither
/// heal nor clear, and with repair on it is marked unhealable and holds the unhealed gauge up forever. The audit asks the
/// ledger which (origin, type) pairs it holds, judges each by the ownership rule, and the ledger forgets the ones judged.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/370_LedgerForgetsANonOwnersRows.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
[Category("Shard5")]
public class IntegrityLedgerForgetNonOwnerSqlTests : EFCoreTestBase {
  private const string ORDERS = "Contracts.Orders.OrderPlaced, Contracts";
  private const string BILLING = "Contracts.Billing.InvoiceRaised, Contracts";
  private static readonly DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  private IWorkCoordinator _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  private async Task _trackAsync(Guid origin, string eventType, string tenant = "tenant-a") {
    var key = new IntegrityRepairLedger.DivergenceKey(origin, tenant, eventType, TrackedGuid.New().Value);
    _ = await _coordinator().IntegrityTryBeginReportBatchAsync(
      origin, [new IntegrityReportObservation(key, 10, 20, 0, 0, IsDeficit: true)], _now, TimeSpan.FromMinutes(5));
  }

  private async Task<List<string>> _ledgerAsync() {
    await using var ctx = CreateDbContext();
    return await ctx.Database.SqlQueryRaw<string>(
      "SELECT origin_service_id::text || '|' || event_type AS \"Value\" FROM wh_integrity_ledger ORDER BY 1").ToListAsync();
  }

  [Test]
  public async Task Forget_RemovesEveryRowOfAJudgedPair_AndKeepsTheRestAsync() {
    var owner = TrackedGuid.New().Value;
    var copyHolder = TrackedGuid.New().Value;
    await _trackAsync(copyHolder, ORDERS, "tenant-a");
    await _trackAsync(copyHolder, ORDERS, "tenant-b");
    await _trackAsync(copyHolder, BILLING);
    await _trackAsync(owner, ORDERS);
    var judged = new List<(Guid, string)>();

    var forgotten = await _coordinator().IntegrityForgetNonOwnerLedgerRowsAsync((origin, type) => {
      judged.Add((origin, type));
      return origin == copyHolder && type == ORDERS;
    });

    await Assert.That(judged).IsEquivalentTo([(copyHolder, ORDERS), (copyHolder, BILLING), (owner, ORDERS)])
      .Because("each distinct (origin, type) pair is judged once, however many tenants and streams hold it");
    await Assert.That(forgotten).IsEqualTo(2)
      .Because("every row of the judged pair goes, in every tenant and stream");
    await Assert.That(await _ledgerAsync()).IsEquivalentTo([$"{copyHolder}|{BILLING}", $"{owner}|{ORDERS}"])
      .Because("the same origin's other type and the owner's own row of the same type are not the judged pair");
  }

  [Test]
  public async Task Forget_WhenNothingIsJudged_RemovesNothingAsync() {
    var origin = TrackedGuid.New().Value;
    await _trackAsync(origin, ORDERS);

    var forgotten = await _coordinator().IntegrityForgetNonOwnerLedgerRowsAsync((_, _) => false);

    await Assert.That(forgotten).IsEqualTo(0);
    await Assert.That(await _ledgerAsync()).IsEquivalentTo([$"{origin}|{ORDERS}"]);
  }

  [Test]
  public async Task Forget_AnEmptyPairList_RemovesNothingAsync() {
    var origin = TrackedGuid.New().Value;
    await _trackAsync(origin, ORDERS);
    await using var ctx = CreateDbContext();

    var forgotten = await ctx.Database.SqlQueryRaw<int>(
      "SELECT wh_integrity_forget_origin_types(ARRAY[]::uuid[], ARRAY[]::text[]) AS \"Value\"").SingleAsync();
    var nulls = await ctx.Database.SqlQueryRaw<int>(
      "SELECT wh_integrity_forget_origin_types(NULL, NULL) AS \"Value\"").SingleAsync();

    await Assert.That(forgotten).IsEqualTo(0);
    await Assert.That(nulls).IsEqualTo(0)
      .Because("only a named pair is forgotten; a caller that lost its list must not empty the ledger");
    await Assert.That(await _ledgerAsync()).IsEquivalentTo([$"{origin}|{ORDERS}"]);
  }

  [Test]
  public async Task Forget_ANullJudge_ThrowsAsync() {
    await Assert.That(async () => await _coordinator().IntegrityForgetNonOwnerLedgerRowsAsync(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Forget_ACanceledCall_IsCanceled_NotSwallowedAsync() {
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.That(async () => await _coordinator().IntegrityForgetNonOwnerLedgerRowsAsync((_, _) => true, canceled.Token))
      .Throws<OperationCanceledException>()
      .Because("a canceled audit stops; it is not a failed prune to log and shrug off");
  }

  [Test]
  public async Task Forget_ThroughAThroughputGatedCoordinator_ForgetsAsync() {
    await _trackAsync(TrackedGuid.New().Value, ORDERS);
    await using var ctx = CreateDbContext();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1,
      logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorGate>.Instance);
    IWorkCoordinator coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), gate: gate);

    var forgotten = await coordinator.IntegrityForgetNonOwnerLedgerRowsAsync((_, _) => true);

    await Assert.That(forgotten).IsEqualTo(1);
  }

  [Test]
  public async Task Forget_WithTheFunctionsUnavailable_ForgetsNothing_AndDoesNotThrowAsync() {
    await _trackAsync(TrackedGuid.New().Value, ORDERS);
    await using (var ctx = CreateDbContext()) {
      await ctx.Database.ExecuteSqlRawAsync("DROP FUNCTION IF EXISTS wh_integrity_ledger_origin_types()");
    }

    var forgotten = await _coordinator().IntegrityForgetNonOwnerLedgerRowsAsync((_, _) => true);

    await Assert.That(forgotten).IsEqualTo(0)
      .Because("a ledger that cannot be pruned this cycle is pruned on the next; the audit must not fail over it");
    await Assert.That((await _ledgerAsync()).Count).IsEqualTo(1);
  }
}
