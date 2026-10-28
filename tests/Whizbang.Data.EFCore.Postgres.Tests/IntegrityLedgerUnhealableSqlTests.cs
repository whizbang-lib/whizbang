// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A bucket repair cannot change, in the durable ledger (#1790, migration 249): once a redelivery answered with events
/// that were all already stored under their origin, neither the burst grant nor the paced drain asks for the bucket
/// again and the cooldown stops re-reporting it, until its signature changes or it heals.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/249_RedeliveredHistoryAdoptsItsOrigin.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#unhealable-buckets</docs>
[Category("Shard5")]
public class IntegrityLedgerUnhealableSqlTests : EFCoreTestBase {
  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(5);
  private static readonly TimeSpan _backoff = TimeSpan.FromSeconds(30);
  private static readonly DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
  private const int MAX_ATTEMPTS = 3;

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

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin, string? tenant = "tenant-a") =>
    new(origin, tenant, "Contracts.OrderPlaced", TrackedGuid.New().Value);

  // Tracked as a deficit, the only kind the paced drain claims (#1842).
  private async Task _trackAsync(IntegrityRepairLedger.DivergenceKey key, long localLo = 0) =>
    _ = await _coordinator().IntegrityTryBeginReportBatchAsync(
      key.OriginServiceId, [new IntegrityReportObservation(key, 10, 20, localLo, 0, IsDeficit: true)], _now, _cooldown);

  [Test]
  public async Task MarkUnhealable_ATrackedBucket_IsReturnedOnce_AndAKeyWithoutARowIsIgnoredAsync() {
    var origin = TrackedGuid.New().Value;
    var tracked = _key(origin);
    var untracked = _key(origin);
    await _trackAsync(tracked);

    var first = await _coordinator().IntegrityMarkUnhealableBatchAsync(origin, [tracked, untracked], _now);
    var second = await _coordinator().IntegrityMarkUnhealableBatchAsync(origin, [tracked], _now.AddMinutes(1));

    await Assert.That(first).IsNotNull();
    await Assert.That(first).IsEquivalentTo([tracked]);
    await Assert.That(second).IsEmpty().Because("a bucket is reported once when repair gives up on it");
  }

  [Test]
  public async Task MarkUnhealable_AnEmptyTenant_MatchesTheRowTheLedgerKeyedWithoutOneAsync() {
    var origin = TrackedGuid.New().Value;
    var key = _key(origin, tenant: null);
    await _trackAsync(key);

    var marked = await _coordinator().IntegrityMarkUnhealableBatchAsync(origin, [key], _now);

    await Assert.That(marked).IsEquivalentTo([key])
      .Because("the ledger keys an absent tenant as an empty one, and the answer speaks the caller's form");
  }

  [Test]
  public async Task TryBeginRepair_AnUnhealableBucket_IsRefusedPastItsTerminalCadenceAsync() {
    var origin = TrackedGuid.New().Value;
    var key = _key(origin);
    await _trackAsync(key);
    await Assert.That(await _coordinator().IntegrityTryBeginRepairAsync(key, _now, _backoff, MAX_ATTEMPTS)).IsTrue();
    _ = await _coordinator().IntegrityMarkUnhealableBatchAsync(origin, [key], _now);

    var later = await _coordinator().IntegrityTryBeginRepairAsync(key, _now.AddDays(30), _backoff, MAX_ATTEMPTS);
    var batch = await _coordinator().IntegrityTryBeginRepairBatchAsync(origin, [key], _now.AddDays(30), _backoff, MAX_ATTEMPTS, 5);

    await Assert.That(later).IsFalse();
    await Assert.That(batch).IsEquivalentTo([false]);
  }

  [Test]
  public async Task ClaimRepairDrain_SkipsAnUnhealableBucketAsync() {
    var origin = TrackedGuid.New().Value;
    var unhealable = _key(origin);
    var repairable = _key(origin);
    await _trackAsync(unhealable);
    await _trackAsync(repairable);
    _ = await _coordinator().IntegrityMarkUnhealableBatchAsync(origin, [unhealable], _now);

    var claimed = await _coordinator().IntegrityClaimRepairDrainAsync([origin], _now.AddDays(1), _backoff, MAX_ATTEMPTS, 10);

    await Assert.That(claimed.Select(c => c.StreamId)).IsEquivalentTo([repairable.StreamId])
      .Because("the drain re-sending a bucket repair cannot change is the standing loop this ends");
  }

  [Test]
  public async Task TryBeginReport_AnUnhealableBucketUnchanged_IsNotReReportedOnTheCooldownAsync() {
    var origin = TrackedGuid.New().Value;
    var key = _key(origin);
    await _trackAsync(key);
    _ = await _coordinator().IntegrityMarkUnhealableBatchAsync(origin, [key], _now);

    var report = await _coordinator().IntegrityTryBeginReportAsync(key, 10, 20, 0, 0, _now.AddDays(1), _cooldown);

    await Assert.That(report).IsFalse();
  }

  [Test]
  public async Task TryBeginReport_ASignatureChange_LiftsTheMarkAndRepairResumesAsync() {
    var origin = TrackedGuid.New().Value;
    var key = _key(origin);
    await _trackAsync(key);
    _ = await _coordinator().IntegrityMarkUnhealableBatchAsync(origin, [key], _now);

    var report = await _coordinator().IntegrityTryBeginReportAsync(key, 10, 20, 7, 0, _now.AddMinutes(1), _cooldown);
    var repair = await _coordinator().IntegrityTryBeginRepairAsync(key, _now.AddMinutes(1), _backoff, MAX_ATTEMPTS);

    await Assert.That(report).IsTrue().Because("a moved digest is a fresh incident");
    await Assert.That(repair).IsTrue();
    await Assert.That(await _coordinator().IntegrityMarkUnhealableBatchAsync(origin, [key], _now.AddMinutes(2)))
      .IsEquivalentTo([key]);
  }

  [Test]
  public async Task MarkUnhealable_WithNoKeys_IsHarmlessAsync() {
    var marked = await _coordinator().IntegrityMarkUnhealableBatchAsync(TrackedGuid.New().Value, [], _now);

    await Assert.That(marked).IsEmpty();
  }

  [Test]
  public async Task MarkUnhealable_StoreFailure_AnswersUnsupportedAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    await conn.OpenAsync();
    await using (var drop = conn.CreateCommand()) {
      drop.CommandText = "DROP FUNCTION wh_integrity_mark_unhealable_batch";
      await drop.ExecuteNonQueryAsync();
    }
    IWorkCoordinator coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var marked = await coordinator.IntegrityMarkUnhealableBatchAsync(TrackedGuid.New().Value, [_key(Guid.NewGuid())], _now);

    await Assert.That(marked).IsNull()
      .Because("a ledger that cannot be reached leaves repair on its terminal cadence, the harmless direction");
  }

  [Test]
  public async Task MarkUnhealable_ThroughAThroughputGatedCoordinator_MarksAsync() {
    var origin = TrackedGuid.New().Value;
    var key = _key(origin);
    await _trackAsync(key);
    await using var ctx = CreateDbContext();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorGate>.Instance);
    IWorkCoordinator coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), gate: gate);

    var marked = await coordinator.IntegrityMarkUnhealableBatchAsync(origin, [key], _now);

    await Assert.That(marked).IsEquivalentTo([key]);
  }

  [Test]
  public async Task MarkUnhealable_CanceledDuringShutdown_PropagatesInsteadOfReadingAsUnsupportedAsync() {
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    await Assert.That(async () => await _coordinator().IntegrityMarkUnhealableBatchAsync(
        TrackedGuid.New().Value, [_key(TrackedGuid.New().Value)], _now, stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("a swallowed shutdown would read as a ledger that cannot answer");
  }
}

