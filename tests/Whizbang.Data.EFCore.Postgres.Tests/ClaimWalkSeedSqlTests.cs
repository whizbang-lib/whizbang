// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Where the claim's re-offer walk starts (#2580). In production each walk starts at a stream drawn at random, so a bucket
/// larger than the batch is not re-offered from the same streams on every poll (fairness between streams). A measurement
/// can fix the start for its own session with the <c>whizbang.claim_walk_seed</c> setting, so two runs of one workload walk
/// the same streams and read the same pages; nothing in the framework sets it.
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop#the-re-offer-walk-start</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/620_TheReOfferWalkStartCanBeSeededForAMeasurement.sql</code-under-test>
[Category("Shard5")]
[Category("Integration")]
public class ClaimWalkSeedSqlTests : EFCoreTestBase {
  private const string SEED_SETTING = "whizbang.claim_walk_seed";
  private static readonly Guid _instance = new("01a20000-0000-7000-8000-00000000a580");

  [Test]
  public async Task TheWalkStart_Unseeded_IsDrawnAtRandomOnEveryCallAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);

    var first = await _walkStartAsync(conn, cancellationToken);
    var second = await _walkStartAsync(conn, cancellationToken);

    await Assert.That(second).IsNotEqualTo(first)
      .Because("with no seed set, every walk starts at a stream drawn at random, which is the fairness between streams "
        + "a poll larger than its batch depends on; a fixed default start would re-offer the same streams on every poll");
  }

  [Test]
  public async Task TheWalkStart_Seeded_IsTheSeedAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var seed = new Guid("01a20000-0000-7000-8000-00000000b580");
    await _setSeedAsync(conn, seed.ToString(), cancellationToken);

    await Assert.That(await _walkStartAsync(conn, cancellationToken)).IsEqualTo(seed);
    await Assert.That(await _walkStartAsync(conn, cancellationToken)).IsEqualTo(seed)
      .Because("a seeded session starts every walk at the seed, so a measurement walks the same streams on every poll");
  }

  [Test]
  public async Task TheWalkStart_SeedReset_IsDrawnAtRandomAgainAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await _setSeedAsync(conn, "01a20000-0000-7000-8000-00000000c580", cancellationToken);
    // A setting reset in a session that once set it reads as the empty string, not as unset.
    await _executeAsync(conn, $"RESET {SEED_SETTING}", cancellationToken);

    var first = await _walkStartAsync(conn, cancellationToken);
    var second = await _walkStartAsync(conn, cancellationToken);

    await Assert.That(second).IsNotEqualTo(first)
      .Because("a reset seed is no seed: the walk is drawn at random again");
  }

  [Test]
  public async Task ClaimWork_AsInstalled_DrawsEveryWalkStartThroughTheSeedableStartAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT pg_get_functiondef(p.oid)
      FROM pg_proc p
      WHERE p.proname = 'claim_work' AND p.pronamespace = to_regnamespace(current_schema())
      """;
    var definition = (string)(await cmd.ExecuteScalarAsync(cancellationToken))!;

    await Assert.That(definition).DoesNotContain("gen_random_uuid()")
      .Because("a walk start drawn directly cannot be fixed for a measurement (#2580)");
    var draws = definition.Split("_claim_walk_start()").Length - 1;
    await Assert.That(draws).IsEqualTo(2)
      .Because("the inbox walks and the perspective walks each start at the seedable draw");
  }

  [Test]
  public async Task ClaimWork_Seeded_ReOffersTheHeldStreamsThatFollowTheSeed_InStreamOrderAsync(
      CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await _fillHeldPerspectiveStreamsAsync(conn, streams: 20, cancellationToken);
    var seed = await _scalarGuidAsync(conn,
      "SELECT stream_id FROM wh_perspective_events ORDER BY stream_id OFFSET 6 LIMIT 1", cancellationToken);
    var expected = await _streamsAsync(conn,
      $"SELECT stream_id FROM wh_perspective_events WHERE stream_id > '{seed}' ORDER BY stream_id LIMIT 5", cancellationToken);
    await _setSeedAsync(conn, seed.ToString(), cancellationToken);

    var offered = await _streamsAsync(conn, """
      SELECT work_stream_id FROM claim_work(
        p_instance_id => @instance, p_service_name => 'seed', p_host_name => 'seed-host', p_process_id => 1,
        p_max_streams => 5, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => 5)
      WHERE source = 'perspective_stream'
      ORDER BY work_stream_id
      """, cancellationToken);

    await Assert.That(offered).IsEquivalentTo(expected)
      .Because("a seeded walk starts at the seed and takes the batch of held streams after it, in stream order");
  }

  /// <summary>
  /// Twenty streams, one stored event each, whose perspective work this instance holds under a live lease, all in one
  /// priority bucket, so a claim's re-offer walk is the only thing that hands them back.
  /// </summary>
  private static async Task _fillHeldPerspectiveStreamsAsync(NpgsqlConnection conn, int streams, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      TRUNCATE wh_perspective_events, wh_active_streams, wh_event_store, wh_event_body CASCADE;
      DELETE FROM wh_service_instances;
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@instance, 'seed', 'seed-host', 1, NOW(), NOW(), '{}'::jsonb);

      WITH s AS MATERIALIZED (
        SELECT md5('claim-walk-seed-stream-' || n)::uuid AS stream_id, md5('claim-walk-seed-event-' || n)::uuid AS event_id, n
        FROM generate_series(1, @streams) n
      ),
      es AS (
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
                                    created_at, flags, commit_sequence)
        SELECT event_id, stream_id, stream_id, 'seed', 'Seed.OrderCreated, Seed', NULL, 1, NOW(), 0, n FROM s
        RETURNING 1
      ),
      eb AS (
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        SELECT event_id, '{"Amount":1}'::jsonb, '{"Hops":[]}'::jsonb FROM s
        RETURNING 1
      ),
      pe AS (
        INSERT INTO wh_perspective_events (stream_id, perspective_name, event_id, instance_id, lease_expiry,
                                           partition_number, status, priority, created_at)
        SELECT stream_id, 'Seed.OrderPerspective', event_id, @instance, NOW() + INTERVAL '1 hour',
               compute_partition(stream_id, 10000), 1, 150, NOW()
        FROM s
        RETURNING 1
      )
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
      SELECT stream_id, compute_partition(stream_id, 10000), @instance, NOW(), NOW() FROM s
      """;
    cmd.Parameters.AddWithValue("instance", _instance);
    cmd.Parameters.AddWithValue(nameof(streams), streams);
    _ = await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task<NpgsqlConnection> _openAsync(CancellationToken cancellationToken) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    return conn;
  }

  private static async Task<Guid> _walkStartAsync(NpgsqlConnection conn, CancellationToken cancellationToken) =>
    await _scalarGuidAsync(conn, "SELECT _claim_walk_start()", cancellationToken);

  private static Task _setSeedAsync(NpgsqlConnection conn, string seed, CancellationToken cancellationToken) =>
    _executeAsync(conn, $"SET {SEED_SETTING} = '{seed}'", cancellationToken);

  private static async Task<Guid> _scalarGuidAsync(NpgsqlConnection conn, string sql, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (Guid)(await cmd.ExecuteScalarAsync(cancellationToken))!;
  }

  private static async Task<List<Guid>> _streamsAsync(NpgsqlConnection conn, string sql, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("instance", _instance);
    var streams = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      streams.Add(reader.GetGuid(0));
    }
    return streams;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    _ = await cmd.ExecuteNonQueryAsync(cancellationToken);
  }
}
