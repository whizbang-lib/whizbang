// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The EF Core coordinator's batch reconcile (#2099): every bundle goes to <c>wh_reconcile_redelivered_bundles</c> in one
/// call, and each bundle's buckets come back at its own position, with the origins it filled told apart from the
/// sequences it filled.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#redelivery-reconciles-run-as-a-batch</docs>
[Category("Integration")]
[Category("Shard5")]
public class RedeliveryBatchReconcileCoordinatorTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Contracts.OrderPlaced, Contracts";
  private readonly List<WorkCoordinationDbContext> _contexts = [];

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  [Test]
  public async Task Reconcile_ReturnsEachBundlesBuckets_AtItsOwnPositionAsync() {
    var origin = TrackedGuid.New().Value;
    var (withoutOrigin, withoutSequence) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var (streamA, streamB) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    await _storeAsync(withoutOrigin, streamA, origin: null, tenant: "tenant-a");
    await _storeAsync(withoutSequence, streamB, origin, tenant: null);

    var result = await _coordinator().ReconcileRedeliveredBundlesAsync([
      new RedeliveredBundle(origin, "orders-service", [withoutOrigin], [3]),
      new RedeliveredBundle(origin, null, [TrackedGuid.New().Value], [4]),
      new RedeliveredBundle(origin, null, [withoutSequence], [5]),
    ]);

    await Assert.That(result!.Count).IsEqualTo(3);
    await Assert.That(result[0]).IsEquivalentTo([new RedeliveredBucketPresence(streamA, "tenant-a", EVENT_TYPE, 1, 1, 0, 0, 1)]);
    await Assert.That(result[1]).IsEmpty().Because("a bundle of events the store does not hold has no buckets, but keeps its place");
    await Assert.That(result[2]).IsEquivalentTo([new RedeliveredBucketPresence(streamB, null, EVENT_TYPE, 1, 1, 0, 0, 0)])
      .Because("the event already carried its origin, so the bundle filled only its sequence");
  }

  [Test]
  public async Task Reconcile_NoBundles_AnswersEmpty_WithoutAStoreCallAsync() {
    var result = await _coordinator().ReconcileRedeliveredBundlesAsync([]);

    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task Reconcile_ABundleWhoseSequencesDoNotPairWithItsEvents_IsRefusedAsync() {
    await Assert.That(async () => await _coordinator().ReconcileRedeliveredBundlesAsync([
        new RedeliveredBundle(TrackedGuid.New().Value, null, [TrackedGuid.New().Value], []),
      ]))
      .Throws<ArgumentException>().Because("the sequences pair with the event ids by position");
  }

  [Test]
  public async Task Reconcile_ThroughAThroughputGatedCoordinator_HoldsAGateSlotAndReconcilesAsync() {
    var origin = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    await _storeAsync(eventId, TrackedGuid.New().Value, origin: null, tenant: null);
    await using var ctx = CreateDbContext();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorGate>.Instance);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), gate: gate);

    var result = await coordinator.ReconcileRedeliveredBundlesAsync([new RedeliveredBundle(origin, null, [eventId], [9])]);

    await Assert.That(result![0].Single().Adopted).IsEqualTo(1);
  }

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
  }

  private async Task _storeAsync(Guid eventId, Guid stream, Guid? origin, string? tenant) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, created_at,
         origin_service_id)
      VALUES ($1, $2, $2, 'Order', $3, CASE WHEN $4::text IS NULL THEN NULL ELSE jsonb_build_object('t', $4::text) END, 1,
              nextval('wh_commit_seq'), NOW(), $5)
      """;
    cmd.Parameters.AddWithValue(eventId);
    cmd.Parameters.AddWithValue(stream);
    cmd.Parameters.AddWithValue(EVENT_TYPE);
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)tenant ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)origin ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
    await cmd.ExecuteNonQueryAsync();
  }
}
