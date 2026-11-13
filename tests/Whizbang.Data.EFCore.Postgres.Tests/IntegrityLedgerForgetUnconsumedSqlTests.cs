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
/// The repair ledger keeps rows only for the event types this service consumes (#2058, migration 331). A type nothing
/// here consumes is never stored here, so its every bucket reads as a deficit, and its redelivery is discarded at the
/// receive boundary: the row can never heal. The audit hands the consumed set to the ledger each cycle and the rest go.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/331_LedgerKeepsOnlyConsumedTypes.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#the-integrity-surface-is-what-the-service-consumes</docs>
[Category("Shard5")]
public class IntegrityLedgerForgetUnconsumedSqlTests : EFCoreTestBase {
  private const string CONSUMED = "Contracts.OrderShipped, Contracts";
  private const string UNCONSUMED = "Contracts.KnownButUnconsumed, Contracts";
  private static readonly DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

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

  private async Task _trackAsync(Guid origin, string eventType) {
    var key = new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", eventType, TrackedGuid.New().Value);
    _ = await _coordinator().IntegrityTryBeginReportBatchAsync(
      origin, [new IntegrityReportObservation(key, 10, 20, 0, 0, IsDeficit: true)], _now, TimeSpan.FromMinutes(5));
  }

  private async Task<List<string>> _ledgerTypesAsync() {
    await using var ctx = CreateDbContext();
    return await ctx.Database.SqlQueryRaw<string>("SELECT event_type AS \"Value\" FROM wh_integrity_ledger ORDER BY 1").ToListAsync();
  }

  [Test]
  public async Task Forget_RemovesEveryRowOfAnUnconsumedType_AndKeepsTheConsumedOnesAsync() {
    var origin = TrackedGuid.New().Value;
    var otherOrigin = TrackedGuid.New().Value;
    await _trackAsync(origin, CONSUMED);
    await _trackAsync(origin, UNCONSUMED);
    await _trackAsync(otherOrigin, UNCONSUMED);

    var forgotten = await _coordinator().IntegrityForgetUnconsumedEventTypesAsync([CONSUMED], []);

    await Assert.That(forgotten).IsEqualTo(2)
      .Because("both rows of the unconsumed type go, whichever origin they came from");
    await Assert.That(await _ledgerTypesAsync()).IsEquivalentTo([CONSUMED]);
  }

  [Test]
  public async Task Forget_AnEmptyConsumedSet_RemovesEveryRowAsync() {
    await _trackAsync(TrackedGuid.New().Value, UNCONSUMED);

    var forgotten = await _coordinator().IntegrityForgetUnconsumedEventTypesAsync([], []);

    await Assert.That(forgotten).IsEqualTo(1)
      .Because("a service that consumes no events has no ledger row it can heal");
    await Assert.That(await _ledgerTypesAsync()).IsEmpty();
  }

  [Test]
  public async Task Forget_KeepsTheRowsOfATypeInAnAbsorbedNamespaceAsync() {
    var origin = TrackedGuid.New().Value;
    await _trackAsync(origin, "Contracts.Jobs.JobStarted, Contracts");
    await _trackAsync(origin, "Contracts.Jobs.Outer+JobNested, Contracts");
    await _trackAsync(origin, UNCONSUMED);

    var forgotten = await _coordinator().IntegrityForgetUnconsumedEventTypesAsync([CONSUMED], ["contracts.jobs"]);

    await Assert.That(forgotten).IsEqualTo(1);
    await Assert.That(await _ledgerTypesAsync()).IsEquivalentTo(
      ["Contracts.Jobs.JobStarted, Contracts", "Contracts.Jobs.Outer+JobNested, Contracts"])
      .Because("an absorbed namespace's events are stored here on purpose, nested types included, so their rows can heal");
  }

  [Test]
  public async Task Forget_ANullConsumedSet_ForgetsNothingAsync() {
    await _trackAsync(TrackedGuid.New().Value, UNCONSUMED);
    await using var ctx = CreateDbContext();

    var forgotten = await ctx.Database.SqlQueryRaw<int>(
      "SELECT wh_integrity_forget_unconsumed(NULL, NULL) AS \"Value\"").SingleAsync();

    await Assert.That(forgotten).IsEqualTo(0)
      .Because("only an explicit consumed set may empty the ledger; a caller that lost it must not");
    await Assert.That(await _ledgerTypesAsync()).IsEquivalentTo([UNCONSUMED]);
  }

  [Test]
  public async Task Forget_ACanceledCall_IsCanceled_NotSwallowedAsync() {
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.That(async () => await _coordinator().IntegrityForgetUnconsumedEventTypesAsync([CONSUMED], [], canceled.Token))
      .Throws<OperationCanceledException>()
      .Because("a canceled audit stops; it is not a failed prune to log and shrug off");
  }

  [Test]
  public async Task Forget_ThroughAThroughputGatedCoordinator_ForgetsAsync() {
    await _trackAsync(TrackedGuid.New().Value, UNCONSUMED);
    await using var ctx = CreateDbContext();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1,
      logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorGate>.Instance);
    IWorkCoordinator coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), gate: gate);

    var forgotten = await coordinator.IntegrityForgetUnconsumedEventTypesAsync([CONSUMED], []);

    await Assert.That(forgotten).IsEqualTo(1);
  }

  [Test]
  public async Task Forget_WithTheFunctionUnavailable_ForgetsNothing_AndDoesNotThrowAsync() {
    await _trackAsync(TrackedGuid.New().Value, UNCONSUMED);
    await using (var ctx = CreateDbContext()) {
      await ctx.Database.ExecuteSqlRawAsync("DROP FUNCTION IF EXISTS wh_integrity_forget_unconsumed_counted(TEXT[], TEXT[])");
    }

    var forgotten = await _coordinator().IntegrityForgetUnconsumedEventTypesAsync([CONSUMED], []);

    await Assert.That(forgotten).IsEqualTo(0)
      .Because("a ledger that cannot be pruned this cycle is pruned on the next; the audit must not fail over it");
    await Assert.That(await _ledgerTypesAsync()).IsEquivalentTo([UNCONSUMED]);
  }
}
