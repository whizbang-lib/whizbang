// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Phantom heal: one statement forgets every ledger row of the proven (origin, tenant, type) triples, the type lane and
/// every per-stream row, and returns each forgotten row's age. A row of another origin, tenant or type is kept; an
/// absent tenant keys as the ledger keys it (the empty scope).
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/384_AProvenTypeForgetsItsStreamRows.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#a-matching-type-heals-its-type-lane</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityTypeHealSqlTests : EFCoreTestBase {
  private static readonly DateTimeOffset _t0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
  private readonly List<WorkCoordinationDbContext> _contexts = [];

  [Test]
  public async Task MarkTypesHealed_ForgetsTheTypeLaneAndEveryStreamRow_OfTheProvenTypesOnly_WithTheirAgesAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var lane = new IntegrityRepairLedger.DivergenceKey(origin, null, "Contracts.TypeX", Guid.Empty);
    var streams = Enumerable.Range(0, 3).Select(_ => lane with { StreamId = TrackedGuid.New().Value }).ToArray();
    var kept = new[] {
      streams[0] with { TenantScope = "tenant-b" },
      streams[0] with { EventType = "Contracts.TypeY" },
      streams[0] with { OriginServiceId = TrackedGuid.New().Value },
    };
    foreach (var key in streams.Append(lane).Concat(kept)) {
      await _reportAsync(coordinator, key);
    }

    var ages = await ((IWorkCoordinator)coordinator).IntegrityMarkTypesHealedWithAgesAsync(origin, [streams[2]]);

    await Assert.That(ages!.Count).IsEqualTo(4).Because("the type lane and its three stream rows");
    await Assert.That(ages.All(a => a >= 0)).IsTrue();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var count = conn.CreateCommand();
    count.CommandText = "SELECT count(*) FROM wh_integrity_ledger";
    await Assert.That((long)(await count.ExecuteScalarAsync())!).IsEqualTo(3L)
      .Because("another tenant's, type's and origin's rows are not proven by this type's match");
  }

  [Test]
  public async Task MarkTypesHealed_NothingTracked_IsAnEmptyAnswerAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;

    var ages = await ((IWorkCoordinator)coordinator).IntegrityMarkTypesHealedWithAgesAsync(
      origin, [new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", "Contracts.TypeX", Guid.Empty)]);

    await Assert.That(ages).IsNotNull();
    await Assert.That(ages).IsEmpty();
  }

  [Test]
  public async Task MarkTypesHealed_UnreachableStore_AnswersUnsupportedAsync() {
    var coordinator = _coordinator();
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await using var drop = conn.CreateCommand();
      drop.CommandText = "DROP FUNCTION wh_integrity_heal_types_counted";   // 392: the function the coordinator calls
      await drop.ExecuteNonQueryAsync();
    }
    var origin = TrackedGuid.New().Value;

    var ages = await ((IWorkCoordinator)coordinator).IntegrityMarkTypesHealedWithAgesAsync(
      origin, [new IntegrityRepairLedger.DivergenceKey(origin, null, "Contracts.TypeX", Guid.Empty)]);

    await Assert.That(ages).IsNull().Because("a failed heal is logged, and the ledger falls back to the type lanes");
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

  private static async Task _reportAsync(EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, IntegrityRepairLedger.DivergenceKey key) =>
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      key.OriginServiceId, [new IntegrityReportObservation(key, 11, 21, 0, 0, IsDeficit: true, DeficitEvents: 1)], _t0, TimeSpan.FromMinutes(60))
      ?? throw new InvalidOperationException("the report batch failed");
}
