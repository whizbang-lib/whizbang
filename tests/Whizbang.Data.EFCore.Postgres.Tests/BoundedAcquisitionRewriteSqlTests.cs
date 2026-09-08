using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 145: inbox acquisition costs the batch, not the backlog; commands have a lane; an idle rank
/// may take unowned rows of another residue when allowed. The result order is the 138 total order
/// (commands first, then per-stream sequence, arrival time, message id), so the drain sees the same
/// breadth-first interleave it always has.
/// </summary>
/// <remarks>
/// Measured live before this migration: 2.3 seconds of database CPU per claim cycle to lease about 25
/// rows from a 220k-row backlog, because the selection ranked every eligible row and probed ownership
/// per row (#714); one interactive command waited 17 minutes behind 42,000 fan-in rows because nothing
/// distinguished it from an event (#721); and 141,000 unowned rows had no eligible acquirer while a
/// healthy instance of another rank sat idle beside them (#725).
/// </remarks>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
[Category("Shard1")]
public class BoundedAcquisitionRewriteSqlTests : EFCoreTestBase {

  private static async Task<NpgsqlConnection> _openAsync(Microsoft.EntityFrameworkCore.DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES (@inst, 'test', 'test-host', 1, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Seeds <paramref name="streams"/> streams of <paramref name="rowsPerStream"/> pending rows each.</summary>
  /// <remarks>
  /// Rows are interleaved across streams in arrival order (row 1 of every stream, then row 2, ...) with
  /// <paramref name="ageSeconds"/> as the age of the oldest row, so the breadth-first order is well defined.
  /// </remarks>
  private static async Task<List<Guid>> _seedStreamsAsync(
      NpgsqlConnection conn, int streams, int rowsPerStream, bool isEvent, int partition, int ageSeconds) {
    var streamIds = Enumerable.Range(0, streams).Select(_ => Guid.CreateVersion7()).ToList();
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         stream_id, partition_number, is_event, instance_id, lease_expiry, error, failure_reason)
      SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{""p"": {}}', '{}', 1, 0,
             NOW() - (@age || ' seconds')::INTERVAL + ((r.seq - 1) * @streams + s.ordinality) * INTERVAL '1 millisecond',
             s.stream_id, @partition, @isEvent, NULL, NULL, NULL, 99
      FROM unnest(@ids::uuid[]) WITH ORDINALITY AS s(stream_id, ordinality)
      CROSS JOIN generate_series(1, @rows) AS r(seq)";
    ins.Parameters.AddWithValue("ids", streamIds.ToArray());
    ins.Parameters.AddWithValue("rows", rowsPerStream);
    ins.Parameters.AddWithValue("streams", streams);
    ins.Parameters.AddWithValue("partition", partition);
    ins.Parameters.AddWithValue("isEvent", isEvent);
    ins.Parameters.AddWithValue("age", ageSeconds);
    await ins.ExecuteNonQueryAsync();
    return streamIds;
  }

  private static async Task<List<Guid>> _acquireAsync(
      NpgsqlConnection conn, Guid instance, int rank, int count, int limit, bool allowSteal) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT message_id FROM claim_orphaned_inbox(
        @inst, @rank, @count, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '10 minutes', @lim, @steal)";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue("rank", rank);
    cmd.Parameters.AddWithValue("count", count);
    cmd.Parameters.AddWithValue("lim", limit);
    cmd.Parameters.AddWithValue("steal", allowSteal);
    var claimed = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      claimed.Add(reader.GetGuid(0));
    }
    return claimed;
  }

  /// <summary>The 138 reference order over the rows an instance holds: per-stream sequence, arrival, id.</summary>
  private static async Task<List<Guid>> _referenceOrderAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT message_id FROM (
        SELECT message_id, is_event, received_at,
               ROW_NUMBER() OVER (PARTITION BY stream_id ORDER BY received_at, message_id) AS stream_seq
        FROM wh_inbox WHERE instance_id = @inst AND processed_at IS NULL
      ) r
      ORDER BY CASE WHEN is_event THEN 1 ELSE 0 END, stream_seq, received_at, message_id";
    cmd.Parameters.AddWithValue("inst", instance);
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  [Test]
  public async Task ClaimOrphanedInbox_PicksAPendingCommandBeforeAnyEventWhateverTheBacklogAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);

    // 200 event streams, one row each, all older than the command: 200 heads ahead of it in arrival order.
    await _seedStreamsAsync(conn, streams: 200, rowsPerStream: 1, isEvent: true, partition: 0, ageSeconds: 600);
    var commandStream = await _seedStreamsAsync(conn, streams: 1, rowsPerStream: 1, isEvent: false, partition: 0, ageSeconds: 1);

    var claimed = await _acquireAsync(conn, instance, rank: 0, count: 1, limit: 50, allowSteal: false);

    await using var find = conn.CreateCommand();
    find.CommandText = "SELECT message_id FROM wh_inbox WHERE stream_id = @sid";
    find.Parameters.AddWithValue("sid", commandStream[0]);
    var commandId = (Guid)(await find.ExecuteScalarAsync())!;

    await Assert.That(claimed).Contains(commandId)
      .Because("a command is a request someone is waiting on; it must be in the first batch regardless of how many events arrived before it");
    await Assert.That(claimed[0]).IsEqualTo(commandId)
      .Because("the command lane comes first in the result order");
    await Assert.That(claimed.Count).IsEqualTo(50)
      .Because("the lane takes what it needs and events fill the rest of the batch");
  }

  [Test]
  public async Task ClaimOrphanedInbox_ManyStreams_KeepsTheBreadthFirstOrderAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);

    // More streams than the batch holds: the early-stop head walk must yield exactly the oldest heads.
    await _seedStreamsAsync(conn, streams: 120, rowsPerStream: 3, isEvent: true, partition: 0, ageSeconds: 600);

    var claimed = await _acquireAsync(conn, instance, rank: 0, count: 1, limit: 80, allowSteal: false);
    var reference = await _referenceOrderAsync(conn, instance);

    await Assert.That(claimed.Count).IsEqualTo(80);
    await Assert.That(claimed).IsEquivalentTo(reference)
      .Because("the rows leased are exactly the rows returned");
    await using var seq = conn.CreateCommand();
    seq.CommandText = @"
      SELECT count(*) FROM (
        SELECT stream_id, ROW_NUMBER() OVER (PARTITION BY stream_id ORDER BY received_at, message_id) AS n
        FROM wh_inbox WHERE processed_at IS NULL
      ) x JOIN wh_inbox i ON i.stream_id = x.stream_id AND i.instance_id = @inst
      WHERE x.n = 1 AND i.message_id IN (SELECT message_id FROM wh_inbox WHERE instance_id = @inst)";
    seq.Parameters.AddWithValue("inst", instance);
    // Every leased row is the head of its stream: 80 heads out of 120 streams, no second row taken.
    await using var heads = conn.CreateCommand();
    heads.CommandText = @"
      SELECT count(*) FROM wh_inbox i
      WHERE i.instance_id = @inst AND NOT EXISTS (
        SELECT 1 FROM wh_inbox j WHERE j.stream_id = i.stream_id AND j.processed_at IS NULL
          AND (j.received_at, j.message_id) < (i.received_at, i.message_id))";
    heads.Parameters.AddWithValue("inst", instance);
    var headCount = Convert.ToInt32(await heads.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(headCount).IsEqualTo(80)
      .Because("with more streams than the batch, breadth-first means heads only; no stream's second row is taken before another stream's first");
  }

  [Test]
  public async Task ClaimOrphanedInbox_FewFatStreams_TakesTheSameRowsPerStreamAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);

    // Fewer streams than the batch: k = ceil(50 / 5) = 10 rows from each stream, in arrival order within the stream.
    var streams = await _seedStreamsAsync(conn, streams: 5, rowsPerStream: 100, isEvent: true, partition: 0, ageSeconds: 600);

    var claimed = await _acquireAsync(conn, instance, rank: 0, count: 1, limit: 50, allowSteal: false);

    await Assert.That(claimed.Count).IsEqualTo(50);
    foreach (var sid in streams) {
      await using var per = conn.CreateCommand();
      per.CommandText = @"
        SELECT count(*), bool_and(rn <= 10) FROM (
          SELECT instance_id, ROW_NUMBER() OVER (ORDER BY received_at, message_id) AS rn
          FROM wh_inbox WHERE stream_id = @sid AND processed_at IS NULL
        ) x WHERE instance_id = @inst";
      per.Parameters.AddWithValue("sid", sid);
      per.Parameters.AddWithValue("inst", instance);
      await using var reader = await per.ExecuteReaderAsync();
      await reader.ReadAsync();
      await Assert.That(reader.GetInt64(0)).IsEqualTo(10L).Because("the batch is split evenly across the few streams");
      await Assert.That(reader.GetBoolean(1)).IsTrue().Because("within a stream the oldest rows go first; per-stream FIFO is untouched");
    }
  }

  [Test]
  public async Task ClaimOrphanedInbox_IdleRank_TakesAnotherResiduesRowsOnlyWhenStealingIsAllowedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var idle = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, idle);

    // Two instances; every pending row belongs to residue 0; the idle instance is rank 1.
    await _seedStreamsAsync(conn, streams: 30, rowsPerStream: 2, isEvent: true, partition: 0, ageSeconds: 600);

    var withoutSteal = await _acquireAsync(conn, idle, rank: 1, count: 2, limit: 20, allowSteal: false);
    await Assert.That(withoutSteal).IsEmpty()
      .Because("under normal load a rank takes only its own residue, so ownership stays stable");

    var withSteal = await _acquireAsync(conn, idle, rank: 1, count: 2, limit: 20, allowSteal: true);
    await Assert.That(withSteal.Count).IsEqualTo(20)
      .Because("an instance whose own residue is empty may take unowned rows of any residue instead of sitting idle next to a backlog");

    // Stealing is per cycle. The remaining rows of the stolen streams still belong to their residue, so
    // without permission the idle rank takes nothing more; it keeps stealing only while its own residue
    // stays empty (the caller re-grants permission each cycle on that evidence).
    var again = await _acquireAsync(conn, idle, rank: 1, count: 2, limit: 20, allowSteal: false);
    await Assert.That(again).IsEmpty()
      .Because("permission to steal is decided per cycle by the caller; the residue arithmetic is untouched");
  }

  [Test]
  public async Task ClaimOrphanedInbox_Steal_SkipsAStreamAnotherInstanceIsMidDrainAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var owner = Guid.CreateVersion7();
    var idle = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, owner);
    await _registerInstanceAsync(conn, idle);

    // Two residue-0 streams, two rows each. The rank-0 owner is mid-drain on the first stream: its head
    // row is leased and live. The second stream is untouched.
    var streams = await _seedStreamsAsync(conn, streams: 2, rowsPerStream: 2, isEvent: true, partition: 0, ageSeconds: 600);
    await using var lease = conn.CreateCommand();
    lease.CommandText = @"
      UPDATE wh_inbox SET instance_id = @owner, lease_expiry = NOW() + INTERVAL '5 minutes', attempts = 1
      WHERE message_id = (SELECT message_id FROM wh_inbox WHERE stream_id = @sid ORDER BY received_at, message_id LIMIT 1)";
    lease.Parameters.AddWithValue("owner", owner);
    lease.Parameters.AddWithValue("sid", streams[0]);
    await lease.ExecuteNonQueryAsync();

    var stolen = await _acquireAsync(conn, idle, rank: 1, count: 2, limit: 10, allowSteal: true);

    await using var where = conn.CreateCommand();
    where.CommandText = "SELECT DISTINCT stream_id FROM wh_inbox WHERE instance_id = @idle";
    where.Parameters.AddWithValue("idle", idle);
    var stolenStreams = new List<Guid>();
    await using (var reader = await where.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        stolenStreams.Add(reader.GetGuid(0));
      }
    }

    await Assert.That(stolen.Count).IsEqualTo(2)
      .Because("the untouched stream's two rows are fair game for an idle instance");
    await Assert.That(stolenStreams).IsEquivalentTo([streams[1]])
      .Because("a stream another live instance is mid-drain on is never stolen; taking its next row would interleave one stream across two instances and break per-stream order");
  }

  [Test]
  public async Task ClaimWork_BoundsAcquisitionByRowsNotByStreamsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();

    // Ten fat streams. A stream bound of 3 used as a row cap acquired 3 rows per cycle; the row bound acquires 50.
    await _seedStreamsAsync(conn, streams: 10, rowsPerStream: 20, isEvent: true, partition: 0, ageSeconds: 600);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT count(*) FROM claim_work(@inst, 'test', 'test-host', 1, 3, 10000, 300, 0.5, 50, FALSE)
      WHERE source = 'inbox'";
    cmd.Parameters.AddWithValue("inst", instance);
    _ = await cmd.ExecuteScalarAsync();

    await using var leased = conn.CreateCommand();
    leased.CommandText = "SELECT count(*) FROM wh_inbox WHERE instance_id = @inst AND processed_at IS NULL AND lease_expiry > NOW()";
    leased.Parameters.AddWithValue("inst", instance);
    var count = Convert.ToInt32(await leased.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

    await Assert.That(count).IsEqualTo(50)
      .Because("acquisition has its own row bound; the stream bound governs only the re-emission");
  }

  [Test]
  public async Task ClaimWork_ReemitsCommandsAheadOfEventsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();

    await _seedStreamsAsync(conn, streams: 20, rowsPerStream: 1, isEvent: true, partition: 0, ageSeconds: 600);
    var commandStream = await _seedStreamsAsync(conn, streams: 1, rowsPerStream: 1, isEvent: false, partition: 0, ageSeconds: 1);

    // First call acquires and re-emits; the command must be the first inbox row re-emitted.
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT work_stream_id FROM claim_work(@inst, 'test', 'test-host', 1, 100, 10000, 300, 0.5, 100, FALSE)
      WHERE source = 'inbox' LIMIT 1";
    cmd.Parameters.AddWithValue("inst", instance);
    var first = await cmd.ExecuteScalarAsync();

    await Assert.That(first).IsEqualTo(commandStream[0])
      .Because("the command lane holds in the re-emission too, not only in acquisition");
  }

  [Test]
  public async Task ClaimWork_PerspectiveBoundOfZero_LeasesNoNewPerspectiveWorkButKeepsReemittingHeldWorkAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();

    // One perspective stream already leased to this instance (held), one unleased (new).
    var held = Guid.CreateVersion7();
    var fresh = Guid.CreateVersion7();
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry, partition_number, status, attempts, created_at)
      VALUES (gen_random_uuid(), @held, 'TestPerspective', gen_random_uuid(), @inst, NOW() + INTERVAL '5 minutes', 0, 0, 1, NOW()),
             (gen_random_uuid(), @fresh, 'TestPerspective', gen_random_uuid(), NULL, NULL, 0, 0, 0, NOW())";
    ins.Parameters.AddWithValue("held", held);
    ins.Parameters.AddWithValue("fresh", fresh);
    ins.Parameters.AddWithValue("inst", instance);
    await ins.ExecuteNonQueryAsync();

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT work_stream_id FROM claim_work(@inst, 'test', 'test-host', 1, 100, 10000, 300, 0.5, 100, FALSE, 0)
      WHERE source = 'perspective_stream'";
    cmd.Parameters.AddWithValue("inst", instance);
    var emitted = new List<Guid>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        emitted.Add(reader.GetGuid(0));
      }
    }

    await Assert.That(emitted).Contains(held)
      .Because("work already held is re-emitted so the drain keeps moving");
    await Assert.That(emitted).DoesNotContain(fresh)
      .Because("with a perspective bound of zero, no new perspective stream is leased this cycle");
    await using var leased = conn.CreateCommand();
    leased.CommandText = "SELECT instance_id FROM wh_perspective_events WHERE stream_id = @sid";
    leased.Parameters.AddWithValue("sid", fresh);
    await Assert.That(await leased.ExecuteScalarAsync()).IsEqualTo(DBNull.Value)
      .Because("the fresh stream stays unleased for a sibling or a later cycle");
  }

  [Test]
  public async Task ReleaseUnstartedLeases_ReturnsOnlyTheNamedStreamsRefundsTheAttemptAndOpensThemToSiblingsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stuck = Guid.CreateVersion7();
    var sibling = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, stuck);
    await _registerInstanceAsync(conn, sibling);

    // The stuck instance acquires three streams (two rows each): attempts go to 1, ownership pins to it.
    var streams = await _seedStreamsAsync(conn, streams: 3, rowsPerStream: 2, isEvent: true, partition: 0, ageSeconds: 600);
    var claimed = await _acquireAsync(conn, stuck, rank: 0, count: 1, limit: 100, allowSteal: false);
    await Assert.That(claimed.Count).IsEqualTo(6);

    // It releases two of the three (the third is mid-drain and stays).
    var keep = streams[2];
    await using var rel = conn.CreateCommand();
    rel.CommandText = "SELECT inbox_released, perspective_released FROM release_unstarted_leases(@inst, @inbox, @persp)";
    rel.Parameters.AddWithValue("inst", stuck);
    rel.Parameters.AddWithValue("inbox", new[] { streams[0], streams[1] });
    rel.Parameters.AddWithValue("persp", Array.Empty<Guid>());
    await using (var reader = await rel.ExecuteReaderAsync()) {
      await reader.ReadAsync();
      await Assert.That(reader.GetInt32(0)).IsEqualTo(4).Because("two streams of two rows each were given back");
      await Assert.That(reader.GetInt32(1)).IsEqualTo(0);
    }

    await using var state = conn.CreateCommand();
    state.CommandText = @"
      SELECT stream_id, count(*) FILTER (WHERE instance_id IS NULL AND lease_expiry IS NULL) AS unleased,
             count(*) FILTER (WHERE attempts = 0) AS refunded
      FROM wh_inbox WHERE processed_at IS NULL GROUP BY stream_id";
    var unleasedByStream = new Dictionary<Guid, (long unleased, long refunded)>();
    await using (var reader = await state.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        unleasedByStream[reader.GetGuid(0)] = (reader.GetInt64(1), reader.GetInt64(2));
      }
    }
    await Assert.That(unleasedByStream[streams[0]]).IsEqualTo((2L, 2L))
      .Because("released rows are unassigned again with the claim's optimistic attempt refunded");
    await Assert.That(unleasedByStream[streams[1]]).IsEqualTo((2L, 2L));
    await Assert.That(unleasedByStream[keep]).IsEqualTo((0L, 0L))
      .Because("the stream not named stays leased to the stuck instance with its attempt standing");

    // A live sibling on the same rank can now take the released streams, but not the kept one.
    var taken = await _acquireAsync(conn, sibling, rank: 0, count: 1, limit: 100, allowSteal: false);
    await Assert.That(taken.Count).IsEqualTo(4)
      .Because("the release ended the stuck instance's ownership of the named streams, so the unowned path opens to a sibling");
    await using var owner = conn.CreateCommand();
    owner.CommandText = "SELECT count(DISTINCT stream_id) FROM wh_inbox WHERE instance_id = @sib";
    owner.Parameters.AddWithValue("sib", sibling);
    await Assert.That(Convert.ToInt32(await owner.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(2);
  }

  [Test]
  public async Task Coordinator_ReleaseUnstartedLeases_WithNothingToRelease_ReturnsZerosWithoutARoundTripAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var released = await coordinator.ReleaseUnstartedLeasesAsync(Guid.CreateVersion7(), [], []);

    await Assert.That(released).IsEqualTo(new UnstartedLeaseRelease(0, 0))
      .Because("two empty lists have nothing to release; the coordinator answers without opening a connection");
  }

  [Test]
  public async Task Coordinator_ReleaseUnstartedLeases_ReturnsWhatTheFunctionReleasedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stuck = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, stuck);
    var streams = await _seedStreamsAsync(conn, streams: 2, rowsPerStream: 3, isEvent: true, partition: 0, ageSeconds: 600);
    _ = await _acquireAsync(conn, stuck, rank: 0, count: 1, limit: 100, allowSteal: false);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var released = await coordinator.ReleaseUnstartedLeasesAsync(stuck, [streams[0]], []);

    await Assert.That(released).IsEqualTo(new UnstartedLeaseRelease(3, 0))
      .Because("the real call path reads the function's one result row: three inbox rows of the named stream, no perspective rows");
  }

  [Test]
  public async Task ReleaseUnstartedLeases_NeverTouchesAnotherInstancesLeasesAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var owner = Guid.CreateVersion7();
    var other = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, owner);

    var streams = await _seedStreamsAsync(conn, streams: 2, rowsPerStream: 1, isEvent: true, partition: 0, ageSeconds: 600);
    _ = await _acquireAsync(conn, owner, rank: 0, count: 1, limit: 100, allowSteal: false);

    await using var rel = conn.CreateCommand();
    rel.CommandText = "SELECT inbox_released FROM release_unstarted_leases(@inst, @inbox, @persp)";
    rel.Parameters.AddWithValue("inst", other);
    rel.Parameters.AddWithValue("inbox", streams.ToArray());
    rel.Parameters.AddWithValue("persp", Array.Empty<Guid>());
    var released = Convert.ToInt32(await rel.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

    await Assert.That(released).IsEqualTo(0)
      .Because("the release is scoped to the caller's own leases; nothing is ever taken from a heartbeating instance by another");
  }
}
