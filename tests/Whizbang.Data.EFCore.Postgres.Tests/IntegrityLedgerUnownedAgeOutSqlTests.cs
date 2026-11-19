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
/// #2185: the audit forgets ledger rows for a type no service owns once the origin that reported them has been silent
/// past a threshold. The ledger hands each (origin, type) pair to the audit's judge with that origin's last report
/// (migration 372): the later of its last checkpoint and the pair's last report on the ledger, so an origin whose
/// checkpoints were never kept is judged by the ledger's own clock, and an origin that still checkpoints is never
/// judged silent.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/372_LedgerAgesOutUnownedTypes.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#a-type-no-service-owns</docs>
[Category("Shard5")]
public class IntegrityLedgerUnownedAgeOutSqlTests : EFCoreTestBase {
  private const string SAGA = "Contracts.Sagas.StepTaken, Contracts";
  private const string LOOSE = "Contracts.Loose.Happened, Contracts";
  private static readonly DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

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

  private async Task<Guid> _trackAsync(Guid origin, string eventType, DateTimeOffset? lastReported, DateTimeOffset firstSeen) {
    var stream = TrackedGuid.New().Value;
    var key = new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", eventType, stream);
    _ = await _coordinator().IntegrityTryBeginReportBatchAsync(
      origin, [new IntegrityReportObservation(key, 10, 20, 0, 0, IsDeficit: true)], _now, TimeSpan.FromMinutes(5));
    await using var ctx = CreateDbContext();
    if (lastReported is { } reported) {
      await ctx.Database.ExecuteSqlRawAsync(
        "UPDATE wh_integrity_ledger SET last_reported_at = {0}, first_seen_at = {1} WHERE stream_id = {2}", reported, firstSeen, stream);
    } else {
      await ctx.Database.ExecuteSqlRawAsync(
        "UPDATE wh_integrity_ledger SET last_reported_at = NULL, first_seen_at = {0} WHERE stream_id = {1}", firstSeen, stream);
    }
    return stream;
  }

  [Test]
  public async Task Read_ReportsEachPairsOriginLastReport_TheLaterOfItsCheckpointAndItsRowsAsync() {
    var checkpointed = TrackedGuid.New().Value;   // last checkpoint 10 days ago, rows last reported 20 days ago
    var neverKept = TrackedGuid.New().Value;      // no checkpoint on record, rows never reported since first seen 30 days ago
    var reportedLater = TrackedGuid.New().Value;  // last checkpoint 40 days ago, one row reported 5 days ago
    await _coordinator().IntegrityRecordOriginCheckpointAsync(checkpointed, "checkpointed-svc", null, _now.AddDays(-10));
    await _coordinator().IntegrityRecordOriginCheckpointAsync(reportedLater, "later-svc", null, _now.AddDays(-40));
    await _trackAsync(checkpointed, SAGA, _now.AddDays(-20), _now.AddDays(-25));
    await _trackAsync(neverKept, SAGA, null, _now.AddDays(-30));
    await _trackAsync(reportedLater, LOOSE, _now.AddDays(-50), _now.AddDays(-60));
    await _trackAsync(reportedLater, LOOSE, _now.AddDays(-5), _now.AddDays(-60));
    var seen = new Dictionary<(Guid, string), DateTimeOffset>();

    var forgotten = await _coordinator().IntegrityForgetNonOwnerLedgerRowsAsync((origin, type, lastReported) => {
      seen[(origin, type)] = lastReported;
      return false;
    });

    await Assert.That(forgotten).IsEqualTo(0);
    await Assert.That(seen.Count).IsEqualTo(3).Because("each distinct (origin, type) pair is judged once");
    await Assert.That(seen[(checkpointed, SAGA)]).IsEqualTo(_now.AddDays(-10))
      .Because("the origin's last checkpoint is its liveness clock");
    await Assert.That(seen[(neverKept, SAGA)]).IsEqualTo(_now.AddDays(-30))
      .Because("with no checkpoint on record, the pair's last report on the ledger (its first sighting) is the latest known");
    await Assert.That(seen[(reportedLater, LOOSE)]).IsEqualTo(_now.AddDays(-5))
      .Because("a report on the ledger after the last kept checkpoint is the later evidence the origin answered");
  }

  [Test]
  public async Task Forget_ThePairsTheJudgeAgesOut_GoesWithTheOthersInOneSweepAsync() {
    var silent = TrackedGuid.New().Value;
    var live = TrackedGuid.New().Value;
    await _coordinator().IntegrityRecordOriginCheckpointAsync(silent, "silent-svc", null, _now.AddDays(-8));
    await _coordinator().IntegrityRecordOriginCheckpointAsync(live, "live-svc", null, _now.AddDays(-1));
    await _trackAsync(silent, SAGA, _now.AddDays(-9), _now.AddDays(-9));
    await _trackAsync(silent, SAGA, _now.AddDays(-9), _now.AddDays(-9));
    await _trackAsync(live, SAGA, _now.AddDays(-9), _now.AddDays(-9));
    var cutoff = _now.AddDays(-7);

    var forgotten = await _coordinator().IntegrityForgetNonOwnerLedgerRowsAsync((_, _, lastReported) => lastReported < cutoff);

    await Assert.That(forgotten).IsEqualTo(2).Because("every row of the silent origin's pair goes");
    await using var ctx = CreateDbContext();
    var left = await ctx.Database.SqlQueryRaw<Guid>("SELECT origin_service_id AS \"Value\" FROM wh_integrity_ledger").ToListAsync();
    await Assert.That(left).IsEquivalentTo([live]).Because("an origin that checkpointed yesterday is not silent");
  }
}
