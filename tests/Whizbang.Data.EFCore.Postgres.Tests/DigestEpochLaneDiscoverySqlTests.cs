// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What <c>close_digest_epochs</c> pays to find its lanes, and that it still finds every one of them.
/// </summary>
/// <remarks>
/// <para>
/// The closure used to discover lanes with <c>SELECT DISTINCT COALESCE(origin_service_id, zero)</c> over the whole
/// event store on every call. No index covers that expression, so every maintenance tick read the store end to end
/// to return a handful of values, one per origin service. On a multi-million-row store that was seconds per call,
/// and it was the inner cost of the closure even when there was nothing left to close.
/// </para>
/// <para>
/// A lane is now read from what already records it: the frontier row the closure pins on first contact, the
/// digest buckets the emit chain keeps per origin, the integrity ledger (#515: an origin with ledger history and no
/// local events is still a lane), and the foreign origins of the event store itself through the lane index. Each
/// is a walk of one index entry per lane, so the cost follows the number of lanes and never the number of events.
/// </para>
/// <para>
/// The measure is the whole call's shared buffers, which <c>EXPLAIN (ANALYZE, BUFFERS)</c> reports including the
/// statements the function runs. The store is sized so that one pass over it costs several times the ceiling; a
/// call that reads it in any form fails, whatever the planner chose to read it with.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
[Category("Integration")]
[Category("Shard2")]
[NotInParallel("EFCorePostgresTests")]
public class DigestEpochLaneDiscoverySqlTests : EFCoreTestBase {
  private const string ZERO = "00000000-0000-0000-0000-000000000000";
  private const string LANE_A = "0a000000-0000-0000-0000-00000000000a";
  private const string LANE_B = "0b000000-0000-0000-0000-00000000000b";

  /// <summary>Streams seeded; half local, a quarter from each foreign lane.</summary>
  private const int STREAMS = 2000;
  /// <summary>Events per stream.</summary>
  private const int DEPTH = 30;

  /// <summary>
  /// Shared buffers one closure call may visit when there is nothing to close. The answer is three lanes, each
  /// found by one index probe and asked for its settled maximum by another. A sequential pass over the seeded store
  /// is about 1,500 pages before any other work, so a call that reads the store fails by a wide margin.
  /// </summary>
  private const long NOTHING_TO_CLOSE_CEILING = 300;

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 300;
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// A settled, fully stamped store: half the streams local, the rest received from two origins. The digest
  /// buckets are folded exactly as the emit chain folds them, because the emit chain is what writes a store.
  /// </summary>
  private static async Task _seedStoreAsync(NpgsqlConnection conn) {
    await _execAsync(conn, $"""
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('integrity_epoch_width', '1000', 'integer', 'test epoch width')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value;

      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence)
      SELECT gen_random_uuid(), s.stream_id, s.stream_id, 'Tests.Lanes.LaneAggregate',
             'Tests.Lanes.SomethingHappened' || (g.seq % 7) || ', Tests.Lanes',
             jsonb_build_object('t', 'tenant-lanes'), v.version, g.seq, 0, NOW() - INTERVAL '2 hours',
             s.origin, CASE WHEN s.origin IS NULL THEN NULL ELSE g.seq END
      FROM (
        SELECT n, gen_random_uuid() AS stream_id,
               CASE n % 4 WHEN 0 THEN '{LANE_A}'::uuid WHEN 1 THEN '{LANE_B}'::uuid ELSE NULL END AS origin
        FROM generate_series(1, {STREAMS}) n
      ) s
      CROSS JOIN LATERAL generate_series(1, {DEPTH}) v(version)
      CROSS JOIN LATERAL (SELECT (v.version - 1) * {STREAMS} + s.n AS seq) g;

      INSERT INTO wh_stream_digests
        (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
      SELECT COALESCE(es.origin_service_id, '{ZERO}'::uuid), COALESCE(es.scope ->> 't', ''), es.event_type, es.stream_id,
             bit_xor(hashtextextended(es.event_id::text, 0)), bit_xor(hashtextextended(es.event_id::text, 1)),
             COUNT(*)::int, NOW()
      FROM wh_event_store es
      GROUP BY 1, 2, 3, 4;

      ANALYZE wh_event_store;
      ANALYZE wh_stream_digests;
      """);
  }

  private static async Task<int> _closeAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT close_digest_epochs(NOW(), 3600, 1000)";
    cmd.CommandTimeout = 300;
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<List<string>> _frontierLanesAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT origin_service_id::text FROM wh_digest_epoch_frontiers ORDER BY 1";
    var lanes = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      lanes.Add(reader.GetString(0));
    }
    return lanes;
  }

  /// <summary>
  /// With every epoch already closed, a closure call costs what its lanes cost: it may not read the event store to
  /// learn which lanes exist.
  /// </summary>
  [Test]
  public async Task CloseDigestEpochs_NothingLeftToClose_CostFollowsTheLanesNotTheStoreAsync() {
    await using var conn = await _openAsync();
    await _seedStoreAsync(conn);

    var closed = await _closeAsync(conn);
    await Assert.That(closed).IsGreaterThan(0)
      .Because("the first call has to fold the settled epochs, or the measured call below has nothing it skips");

    var plan = await QueryPlan.CaptureAsync(conn, "SELECT close_digest_epochs(NOW(), 3600, 1000)");

    plan.MustVisitAtMostSharedBuffers(NOTHING_TO_CLOSE_CEILING);
  }

  /// <summary>Every lane of the store is still found: the local lane and each origin it has received from.</summary>
  [Test]
  public async Task CloseDigestEpochs_FindsTheLocalLaneAndEveryReceivedLaneAsync() {
    await using var conn = await _openAsync();
    await _seedStoreAsync(conn);

    await _closeAsync(conn);

    var lanes = await _frontierLanesAsync(conn);
    await Assert.That(lanes).IsEquivalentTo(new[] { ZERO, LANE_A, LANE_B })
      .Because("lane discovery changed how it reads, not what it finds: one frontier per origin the store holds");
  }

  /// <summary>
  /// A received lane whose events carry no digest bucket (written by a path that does not fold one) is still a lane:
  /// the event store's own foreign origins are read through the lane index.
  /// </summary>
  [Test]
  public async Task CloseDigestEpochs_ReceivedLaneWithoutDigestBuckets_IsStillALaneAsync() {
    await using var conn = await _openAsync();
    await _execAsync(conn, $"""
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence)
      VALUES (gen_random_uuid(), '{LANE_A}'::uuid, '{LANE_A}'::uuid, 'Tests.Lanes.LaneAggregate',
              'Tests.Lanes.Arrived, Tests.Lanes', 'null'::jsonb, 1, nextval('wh_commit_seq'), 0,
              NOW() - INTERVAL '2 hours', '{LANE_B}'::uuid, 5)
      """);

    await _closeAsync(conn);

    var lanes = await _frontierLanesAsync(conn);
    await Assert.That(lanes).Contains(LANE_B);
  }

  /// <summary>
  /// The decision 193 records, pinned so it stays one: local events written by a path that folds no digest bucket
  /// (the integrity sweep reports such a path as drift and heals the bucket) do not make a lane until a bucket
  /// exists. Proving that no local row exists without one would need an index over every local row.
  /// </summary>
  [Test]
  public async Task CloseDigestEpochs_LocalEventsWithoutABucket_BecomeALaneOnceFoldedAsync() {
    await using var conn = await _openAsync();
    var stream = Guid.NewGuid();
    var first = Guid.NewGuid();
    await _execAsync(conn, $"""
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, created_at)
      VALUES ('{first}'::uuid, '{stream}'::uuid, '{stream}'::uuid, 'Tests.Lanes.LaneAggregate',
              'Tests.Lanes.Local, Tests.Lanes', 'null'::jsonb, 1, 1, 0, NOW() - INTERVAL '2 hours')
      """);

    await _closeAsync(conn);
    var beforeFold = await _frontierLanesAsync(conn);
    await EmitChainDigestFold.FoldAsync(conn, first);
    await _closeAsync(conn);
    var afterFold = await _frontierLanesAsync(conn);

    await Assert.That(beforeFold).DoesNotContain(ZERO)
      .Because("nothing records the local lane yet: no frontier, no bucket, and the lane index holds only "
        + "received events");
    await Assert.That(afterFold).Contains(ZERO)
      .Because("the bucket the emit chain (or the sweep's heal) folds is what names the local lane");
  }

  /// <summary>
  /// A lane already registered by an earlier closure keeps being visited even when nothing else names it any more,
  /// so a registered lane can never silently fall out of the closure.
  /// </summary>
  [Test]
  public async Task CloseDigestEpochs_ARegisteredLane_IsVisitedFromItsFrontierAloneAsync() {
    await using var conn = await _openAsync();
    await _execAsync(conn, $"""
      INSERT INTO wh_digest_epoch_frontiers (origin_service_id, closed_through_epoch, epoch_width, updated_at)
      VALUES ('{ZERO}'::uuid, -1, 10, NOW() - INTERVAL '1 day');
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, created_at)
      SELECT gen_random_uuid(), '{LANE_B}'::uuid, '{LANE_B}'::uuid, 'Tests.Lanes.LaneAggregate',
             'Tests.Lanes.Local, Tests.Lanes', 'null'::jsonb, v, v, 0, NOW() - INTERVAL '2 hours'
      FROM generate_series(1, 25) v;
      """);

    var closed = await _closeAsync(conn);

    await Assert.That(closed).IsEqualTo(2)
      .Because("the local lane is registered at width 10 with a settled maximum of 25, so epochs 0 and 1 lie "
        + "below the open epoch. No digest bucket names the lane, so only its frontier row can have led the "
        + "closure to it.");
  }
}
