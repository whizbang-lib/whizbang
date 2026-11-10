// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// How much data is left to heal, and how many buckets repair cannot heal (#2203), read by the ledger gauges' one
/// periodic read against the real function. The deficit events of the buckets repair can still act on are summed; an
/// unhealable or exhausted bucket's deficit is not data repair will bring, so it is left out; a surplus bucket has no
/// deficit; and the unhealable rows are counted whatever else they are.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/373_LedgerGaugesCountDataLeftToHeal.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#integrity-gauges</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityLedgerHealRemainingGaugesSqlTests : EFCoreTestBase {
  private const int MAX_ATTEMPTS = 8;

  // Fixed instants, not the host clock: the report takes the caller's clock.
  private static readonly DateTimeOffset _t0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(60);

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  [Test]
  public async Task Summary_SumsTheDeficitOfRepairableBuckets_AndCountsTheUnhealableOnesAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var repairableA = TrackedGuid.New().Value;
    var repairableB = TrackedGuid.New().Value;
    var unhealable = TrackedGuid.New().Value;
    var exhausted = TrackedGuid.New().Value;
    var unhealableAndExhausted = TrackedGuid.New().Value;
    await _reportAsync(coordinator, origin, repairableA, isDeficit: true, deficit: 5);
    await _reportAsync(coordinator, origin, repairableB, isDeficit: true, deficit: 100_000);
    await _reportAsync(coordinator, origin, unhealable, isDeficit: true, deficit: 7);
    await _reportAsync(coordinator, origin, exhausted, isDeficit: true, deficit: 11);
    await _reportAsync(coordinator, origin, unhealableAndExhausted, isDeficit: true, deficit: 13);
    await _reportAsync(coordinator, origin, TrackedGuid.New().Value, isDeficit: false, deficit: 0);
    await using var conn = await _openAsync();
    await _execAsync(conn, "UPDATE wh_integrity_ledger SET unhealable_at = NOW() WHERE stream_id = ANY($1)",
      new[] { unhealable, unhealableAndExhausted });
    await _execAsync(conn, "UPDATE wh_integrity_ledger SET exhausted_at = NOW() WHERE stream_id = ANY($1)",
      new[] { exhausted, unhealableAndExhausted });

    var summary = await coordinator.GetIntegrityLedgerSummaryAsync(MAX_ATTEMPTS);

    await Assert.That(summary.UnhealedDeficitEvents).IsEqualTo(100_005L)
      .Because("only the buckets repair can still act on hold data left to heal; the unhealable and exhausted ones do not");
    await Assert.That(summary.UnhealableBuckets).IsEqualTo(2L)
      .Because("every row marked unhealable counts, exhausted or not");
    await Assert.That(summary.UnhealedBuckets).IsEqualTo(6L)
      .Because("the existing headline still counts every ledger row, in the same read");
  }

  [Test]
  public async Task Summary_ABucketWhoseDeficitIsUnknown_AddsNothing_AndAnEmptyLedgerReadsZeroAsync() {
    var coordinator = _coordinator();

    var empty = await coordinator.GetIntegrityLedgerSummaryAsync(MAX_ATTEMPTS);

    await Assert.That(empty.UnhealedDeficitEvents).IsEqualTo(0L).Because("an empty sum reads zero, never null");
    await Assert.That(empty.UnhealableBuckets).IsEqualTo(0L);

    var origin = TrackedGuid.New().Value;
    var unknown = TrackedGuid.New().Value;
    await _reportAsync(coordinator, origin, unknown, isDeficit: true, deficit: 3);
    await using var conn = await _openAsync();
    await _execAsync(conn, "UPDATE wh_integrity_ledger SET deficit_events = NULL WHERE stream_id = ANY($1)", new[] { unknown });

    var summary = await coordinator.GetIntegrityLedgerSummaryAsync(MAX_ATTEMPTS);

    await Assert.That(summary.UnhealedDeficitEvents).IsEqualTo(0L)
      .Because("a deficit the last comparison did not size is unknown, and the only rows have none known");
    await Assert.That(summary.UnhealedBuckets).IsEqualTo(1L);
  }

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  private static async Task _reportAsync(
      EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, Guid origin, Guid stream, bool isDeficit, long deficit) {
    var key = new IntegrityRepairLedger.DivergenceKey(origin, null, "Contracts.HealRemainingProbe", stream);
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [new IntegrityReportObservation(key, 11, 21, 0, 0, IsDeficit: isDeficit, DeficitEvents: deficit)],
      _t0, _cooldown) ?? throw new InvalidOperationException("the report batch failed");
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
