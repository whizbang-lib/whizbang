// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What the outbox acquisition (<c>claim_orphaned_outbox</c>, inside <c>claim_work</c>) pays when the pending rows
/// ahead of it belong to streams a live peer owns.
/// </summary>
/// <remarks>
/// <para>
/// The acquisition picked its heads by walking every pending outbox row in arrival order and testing each one for
/// ownership, stopping only once it had found its batch. Under a bulk load the oldest pending rows are the tails of
/// streams a peer already owns: the peer leases a run at a time and the rest of each stream waits, unowned, in the
/// outbox. Every other instance's poll then walked all of them, tested each, and found nothing. A measured call
/// returned 13 rows after visiting 79,590,235 shared buffers, almost all of them cache hits: the same pages walked
/// again on every poll, by every instance, several times a second.
/// </para>
/// <para>
/// The inbox acquisition stopped doing this in 159 with a window: it examines a bounded number of the oldest
/// claimable rows, unowned ones by arrival and expired leases by expiry, and tests ownership against sets built once
/// per call. The outbox acquisition now does the same, with a window of eight times the heads it chooses, so a poll
/// costs a multiple of its batch whatever the backlog holds. These tests pin that bound, and that the window still takes what it should.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[Category("Integration")]
[Category("Shard3")]
[NotInParallel("EFCorePostgresTests")]
public class OutboxAcquisitionWindowSqlTests : EFCoreTestBase {
  private const string POLLER = "aaaaaaaa-0000-0000-0000-000000000001";
  private const string PEER = "bbbbbbbb-0000-0000-0000-000000000002";
  private const string BYSTANDER = "cccccccc-0000-0000-0000-000000000003";
  private const string VANISHED = "dddddddd-0000-0000-0000-000000000004";

  /// <summary>Streams the peer owns, and the pending rows behind each one's leased run.</summary>
  private const int PEER_STREAMS = 200;
  private const int PEER_DEPTH = 200;

  /// <summary>The outbox row bound a claim passes, and the stream window that chooses its heads.</summary>
  private const int MAX_ROWS = 100;
  private const int MAX_HEADS = 25;

  /// <summary>
  /// Shared buffers one acquisition may visit. The window is eight times the heads a call chooses, so a poll that
  /// respects it reads about 200 index entries plus the two ownership sets: a little over 200 buffers. The 40,000
  /// rows behind the peer's streams fill about 2,000 pages, so any read of the backlog, by index or by scan, fails.
  /// </summary>
  private const long ACQUISITION_CEILING = 750;

  /// <summary>
  /// Where arrival order starts. Fixed rather than read from the clock, so a row a test adds after the seed lands
  /// exactly where the test puts it in arrival order, however long the seed took.
  /// </summary>
  private const string ARRIVAL_BASE = "TIMESTAMPTZ '2026-01-01 00:00:00+00'";

  /// <summary>Just after the peer's <paramref name="k"/>-th row in arrival order (row k arrived k ms after the base).</summary>
  private static string _afterPeerRow(int k) =>
    $"({ARRIVAL_BASE} + INTERVAL '{k} milliseconds' + INTERVAL '1 microsecond')";

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
  /// Three live instances. The peer owns every seeded stream under a live stream lease, and the stream's pending
  /// rows are unowned, as they are behind a run the peer has leased and is draining.
  /// </summary>
  private static async Task _seedPeerBacklogAsync(NpgsqlConnection conn) {
    await _execAsync(conn, $"""
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES ('{POLLER}', 'outbox-window', 'host-a', 1, NOW(), NOW(), jsonb_build_object()),
             ('{PEER}', 'outbox-window', 'host-b', 2, NOW(), NOW(), jsonb_build_object()),
             ('{BYSTANDER}', 'outbox-window', 'host-c', 3, NOW(), NOW(), jsonb_build_object());

      CREATE TEMP TABLE peer_streams AS
        SELECT n, gen_random_uuid() AS stream_id FROM generate_series(1, {PEER_STREAMS}) n;

      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry, last_activity_at)
      SELECT ps.stream_id, compute_partition(ps.stream_id, 10000), '{PEER}'::uuid, NOW() + INTERVAL '1 hour', NOW()
      FROM peer_streams ps;

      INSERT INTO wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
                             stream_id, partition_number, is_event, status, attempts, created_at)
      SELECT gen_random_uuid(), 'topic-window', 'Tests.Window.SomethingHappened, Tests.Window', 'MessageEnvelope',
             jsonb_build_object('payload', repeat('x', 200)), jsonb_build_object('hops', jsonb_build_array()), jsonb_build_object('t', 'tenant-window'),
             ps.stream_id, compute_partition(ps.stream_id, 10000), true, 1, 0,
             {ARRIVAL_BASE} + (((r - 1) * {PEER_STREAMS} + ps.n) * INTERVAL '1 millisecond')
      FROM peer_streams ps CROSS JOIN generate_series(1, {PEER_DEPTH}) r;

      ANALYZE wh_outbox;
      ANALYZE wh_active_streams;
      ANALYZE wh_service_instances;
      """);
  }

  private static readonly string _acquire = $"""
    SELECT count(*) FROM claim_orphaned_outbox(
      '{POLLER}'::uuid, 0, 3, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '2 minutes',
      {MAX_ROWS}, 50, {MAX_HEADS})
    """;

  /// <summary>
  /// A poll whose oldest pending rows all belong to a live peer's streams costs a multiple of its batch, not a walk of
  /// the peer's backlog.
  /// </summary>
  [Test]
  public async Task ClaimOrphanedOutbox_BacklogOwnedByALivePeer_CostFollowsTheBatchNotTheBacklogAsync() {
    await using var conn = await _openAsync();
    await _seedPeerBacklogAsync(conn);

    // The first call on a session compiles the function and reads the catalog to do it, which is a cost of the
    // session rather than of the poll; a poller pays it once.
    await _execAsync(conn, "BEGIN");
    await _execAsync(conn, _acquire);
    await _execAsync(conn, "ROLLBACK");

    await _execAsync(conn, "BEGIN");
    var plan = await QueryPlan.CaptureAsync(conn, _acquire);
    await _execAsync(conn, "ROLLBACK");

    await Assert.That(plan.Nodes[0].ActualRows).IsEqualTo(1L);
    plan.MustVisitAtMostSharedBuffers(ACQUISITION_CEILING);
  }

  /// <summary>
  /// A row whose lease expired on an instance that is gone is reclaimed even when every unowned row ahead of it in
  /// arrival order belongs to a live peer: expired leases are their own lane, ordered by expiry.
  /// </summary>
  [Test]
  public async Task ClaimOrphanedOutbox_AnExpiredLeaseBehindAPeerBacklog_IsStillReclaimedAsync() {
    await using var conn = await _openAsync();
    await _seedPeerBacklogAsync(conn);
    var orphan = Guid.NewGuid();
    await _execAsync(conn, $"""
      INSERT INTO wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
                             stream_id, partition_number, is_event, status, attempts, created_at,
                             instance_id, lease_expiry)
      VALUES ('{orphan}', 'topic-window', 'Tests.Window.Orphaned, Tests.Window', 'MessageEnvelope', jsonb_build_object(),
              jsonb_build_object('hops', jsonb_build_array()), NULL, gen_random_uuid(), NULL, true, 1, 1, NOW(),
              '{VANISHED}'::uuid, NOW() - INTERVAL '1 minute');
      ANALYZE wh_outbox;
      """);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      SELECT message_id FROM claim_orphaned_outbox(
        '{POLLER}'::uuid, 0, 3, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '2 minutes',
        {MAX_ROWS}, 50, {MAX_HEADS})
      """;
    var claimed = new List<Guid>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        claimed.Add(reader.GetGuid(0));
      }
    }

    await Assert.That(claimed).IsEquivalentTo(new[] { orphan })
      .Because("the peer's rows are not this instance's to take, and the orphaned row is, wherever it sits in "
        + "arrival order: a lease that lapsed on a vanished instance must be reclaimed on the next poll");
  }

  /// <summary>Adds a stream nobody holds, of <paramref name="rows"/> rows, arriving at <paramref name="arrival"/>.</summary>
  private static async Task<Guid> _addFreeStreamAsync(NpgsqlConnection conn, string arrival, int rows) {
    var stream = Guid.NewGuid();
    await _execAsync(conn, $"""
      INSERT INTO wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
                             stream_id, partition_number, is_event, status, attempts, created_at)
      SELECT gen_random_uuid(), 'topic-window', 'Tests.Window.Free, Tests.Window', 'MessageEnvelope',
             jsonb_build_object(), jsonb_build_object('hops', jsonb_build_array()), NULL, '{stream}'::uuid, NULL,
             true, 1, 0, {arrival} + (r * INTERVAL '1 microsecond')
      FROM generate_series(1, {rows}) r;
      ANALYZE wh_outbox;
      """);
    return stream;
  }

  private static async Task<int> _claimedFromAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      SELECT count(*) FROM claim_orphaned_outbox(
        '{POLLER}'::uuid, 0, 3, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '2 minutes',
        {MAX_ROWS}, 50, {MAX_HEADS}) c
      WHERE c.stream_id = '{stream}'::uuid
      """;
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// A stream nobody holds, whose head lies inside the window among a live peer's rows, is taken from its head with
  /// its run.
  /// </summary>
  [Test]
  public async Task ClaimOrphanedOutbox_UnownedStreamInsideTheWindow_IsTakenFromItsHeadAsync() {
    await using var conn = await _openAsync();
    await _seedPeerBacklogAsync(conn);
    var stream = await _addFreeStreamAsync(conn, _afterPeerRow(50), rows: 5);

    var taken = await _claimedFromAsync(conn, stream);

    await Assert.That(taken).IsEqualTo(5)
      .Because("fifty of the peer's rows arrived first, well inside the window, so the free stream's head is "
        + "among the rows the poll examines; it is chosen and its run follows it");
  }

  /// <summary>
  /// The trade the window makes, pinned so it stays a decision: a takeable stream whose head arrived behind more of a
  /// live peer's rows than the window holds is not reached by this poll. It is reached once the rows ahead of it
  /// drain, which their owner is doing.
  /// </summary>
  [Test]
  public async Task ClaimOrphanedOutbox_UnownedStreamBehindTheWindow_IsReachedOnceTheRowsAheadDrainAsync() {
    await using var conn = await _openAsync();
    await _seedPeerBacklogAsync(conn);
    var behind = _afterPeerRow(MAX_HEADS * 8 * 3);
    var stream = await _addFreeStreamAsync(conn, behind, rows: 5);

    var beforeDrain = await _claimedFromAsync(conn, stream);
    await _execAsync(conn, $"""
      DELETE FROM wh_outbox WHERE stream_id <> '{stream}'::uuid AND created_at < {behind};
      ANALYZE wh_outbox;
      """);
    var afterDrain = await _claimedFromAsync(conn, stream);

    await Assert.That(beforeDrain).IsEqualTo(0)
      .Because("three windows of the peer's rows arrived ahead of the free stream, and a poll examines one");
    await Assert.That(afterDrain).IsEqualTo(5)
      .Because("with the rows ahead of it published, the free stream's head is the oldest claimable row");
  }
}
