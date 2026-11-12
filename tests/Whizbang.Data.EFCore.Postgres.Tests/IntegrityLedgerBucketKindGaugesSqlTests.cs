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
/// #2260: the ledger gauges' one read splits the unhealed buckets by kind. A comparison records each divergence's kind
/// (a deficit, the only kind repair heals) and its size (origin events minus local events): equal counts under different
/// ids are a digest mismatch, more events here than at the origin a surplus, and a row whose kind was never stated
/// (written before kinds were recorded, or by the single-key report) is unknown. The kinds add up to the total.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/400_LedgerGaugesSplitBucketsByKind.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#unhealed-buckets-by-kind</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityLedgerBucketKindGaugesSqlTests : EFCoreTestBase {
  private const int MAX_ATTEMPTS = 8;
  private const string EVENT_TYPE = "Contracts.BucketKindProbe, Contracts";

  // Fixed instants, not the host clock: the report takes the caller's clock.
  private static readonly DateTimeOffset _t0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(60);

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  [Test]
  public async Task Summary_SplitsTheUnhealedBucketsByKind_AndTheKindsAddUpToTheTotalAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    await _reportAsync(coordinator, origin, isDeficit: true, deficit: 4);
    await _reportAsync(coordinator, origin, isDeficit: true, deficit: 1);
    await _reportAsync(coordinator, origin, isDeficit: false, deficit: 0);
    await _reportAsync(coordinator, origin, isDeficit: false, deficit: 0);
    await _reportAsync(coordinator, origin, isDeficit: false, deficit: 0);
    await _reportAsync(coordinator, origin, isDeficit: false, deficit: -2);
    // The single-key report states no kind.
    _ = await coordinator.IntegrityTryBeginReportAsync(
      new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, TrackedGuid.New().Value), 11, 21, 0, 0, _t0, _cooldown);
    // A row that says it is not a deficit, but not by how much, cannot be told apart.
    var unsized = await _reportAsync(coordinator, origin, isDeficit: false, deficit: 0);
    await using var conn = await _openAsync();
    await _execAsync(conn, "UPDATE wh_integrity_ledger SET deficit_events = NULL WHERE stream_id = $1", unsized);

    var summary = await coordinator.GetIntegrityLedgerSummaryAsync(MAX_ATTEMPTS);

    await Assert.That(summary.DeficitBuckets).IsEqualTo(2L).Because("the only kind a redelivery can heal");
    await Assert.That(summary.DigestMismatchBuckets).IsEqualTo(3L)
      .Because("both sides hold the same number of events, under different ids");
    await Assert.That(summary.SurplusBuckets).IsEqualTo(1L).Because("this store holds more events than the origin");
    await Assert.That(summary.UnknownKindBuckets).IsEqualTo(2L);
    await Assert.That(summary.UnhealedBuckets).IsEqualTo(8L).Because("the total is unchanged, for its existing readers");
    await Assert.That(summary.DeficitBuckets + summary.DigestMismatchBuckets + summary.SurplusBuckets + summary.UnknownKindBuckets)
      .IsEqualTo(summary.UnhealedBuckets);
    await Assert.That(summary.UnhealedDeficitEvents).IsEqualTo(5L)
      .Because("the missing events are the deficits' only; a surplus's negative size subtracts nothing");
  }

  [Test]
  public async Task Summary_OfAnEmptyLedger_ReadsZeroForEveryKindAsync() {
    var summary = await _coordinator().GetIntegrityLedgerSummaryAsync(MAX_ATTEMPTS);

    await Assert.That(summary.DeficitBuckets + summary.DigestMismatchBuckets + summary.SurplusBuckets + summary.UnknownKindBuckets)
      .IsEqualTo(0L);
  }

  /// <summary>
  /// 400's reverse restores 373's definition: a rollback leaves an older release the nine columns it reads.
  /// </summary>
  [Test]
  public async Task TheReverse_RestoresTheGaugesWithoutTheKindsAsync() {
    await using var conn = await _openAsync();
    var down = typeof(Whizbang.Data.Postgres.PostgresMigrationProvider).Assembly.GetManifestResourceStream(
      "Whizbang.Data.Postgres.MigrationReversals.400_LedgerGaugesSplitBucketsByKind.down.sql")
      ?? throw new InvalidOperationException("400 ships its reverse");
    using var reader = new StreamReader(down);
    await _execAsync(conn, Whizbang.Data.Postgres.MigrationConstants.Apply(
      (await reader.ReadToEndAsync()).Replace("__SCHEMA__", "public", StringComparison.Ordinal)));

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM wh_integrity_ledger_gauges(8)";
    await using var rows = await cmd.ExecuteReaderAsync();

    await Assert.That(rows.FieldCount).IsEqualTo(9).Because("373's columns, and none of 400's");
    await Assert.That(rows.GetName(8)).IsEqualTo("unhealable_buckets");
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

  private static async Task<Guid> _reportAsync(
      EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, Guid origin, bool isDeficit, long deficit) {
    var stream = TrackedGuid.New().Value;
    var key = new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, stream);
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [new IntegrityReportObservation(key, 11, 21, 0, 0, IsDeficit: isDeficit, DeficitEvents: deficit)],
      _t0, _cooldown) ?? throw new InvalidOperationException("the report batch failed");
    return stream;
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
