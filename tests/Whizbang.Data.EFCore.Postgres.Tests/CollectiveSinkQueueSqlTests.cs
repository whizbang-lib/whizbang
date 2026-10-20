// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <c>wh_collective_sink_queue</c> (migration 175, #963), against the real emit chain and the real commit-order
/// stamper: two collectives sharing an ordering key share a stream, and the queue returns their sink rows in the order
/// they committed, even where their event ids run the other way. It is the order the sink applies them in.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Integration")]
[Category("CollectiveEvents")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class CollectiveSinkQueueSqlTests : EFCoreTestBase {
  private const string COLLECTIVE_EVENT_TYPE = "Whizbang.Tests.FlipCollectiveEvent, Whizbang.Tests";

  // The later-minted id commits first: the ids run backward against the commits.
  private static readonly Guid _firstCommitted = Guid.Parse("00000000-0000-7000-8000-0000000000f2");
  private static readonly Guid _secondCommitted = Guid.Parse("00000000-0000-7000-8000-0000000000f1");

  [Test]
  public async Task SinkQueue_TwoCollectivesSharingAKey_AreInCommitOrder_WhenIdsRunBackwardAsync() {
    var keyStream = CollectiveOrdering.StreamIdFor(new TenantCollectiveScope("t-1"), "family-7");
    await using var conn = await _openAsync();
    await _emitCollectiveAsync(conn, _firstCommitted, keyStream);
    await _emitCollectiveAsync(conn, _secondCommitted, keyStream);
    await _stampAsync(conn);

    var queue = await _queueAsync(conn, keyStream);

    await Assert.That(queue.Select(q => q.EventId)).IsEquivalentTo(
      [_firstCommitted, _secondCommitted], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("Both collectives landed on the key's stream, and the queue is in commit order, not id order.");
    await Assert.That(queue[0].CommitSequence < queue[1].CommitSequence).IsTrue();
  }

  [Test]
  public async Task SinkQueue_UnstampedRowsComeLastInEventIdOrderAsync() {
    var keyStream = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _emitCollectiveAsync(conn, _firstCommitted, keyStream);
    await _stampAsync(conn);
    var laterA = Guid.Parse("00000000-0000-7000-8000-0000000000a2");
    var laterB = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    await _emitCollectiveAsync(conn, laterA, keyStream);
    await _emitCollectiveAsync(conn, laterB, keyStream);

    var queue = await _queueAsync(conn, keyStream);

    await Assert.That(queue.Select(q => q.EventId)).IsEquivalentTo(
      [_firstCommitted, laterB, laterA], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("A row the stamper has not reached yet sorts after every stamped one, and event_id breaks the tie.");
    await Assert.That(queue[1].CommitSequence).IsNull();
  }

  [Test]
  public async Task SinkQueue_LeavesOutProcessedRowsOtherPerspectivesAndOtherStreamsAsync() {
    var keyStream = Guid.CreateVersion7();
    var otherStream = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _emitCollectiveAsync(conn, _firstCommitted, keyStream);
    await _emitCollectiveAsync(conn, _secondCommitted, keyStream);
    await _emitCollectiveAsync(conn, Guid.CreateVersion7(), otherStream);
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "UPDATE wh_perspective_events SET processed_at = now() WHERE event_id = @id";
      cmd.Parameters.AddWithValue("id", _firstCommitted);
      await cmd.ExecuteNonQueryAsync();
    }
    await using (var cmd = conn.CreateCommand()) {
      // A non-sink row on the same stream is not a collective to apply.
      cmd.CommandText = """
        INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
        VALUES (gen_random_uuid(), @sid, 'Some.Perspective', @eid, 0, 1, 0, now())
        """;
      cmd.Parameters.AddWithValue("sid", keyStream);
      cmd.Parameters.AddWithValue("eid", _secondCommitted);
      await cmd.ExecuteNonQueryAsync();
    }

    var queue = await _queueAsync(conn, keyStream);

    await Assert.That(queue.Select(q => q.EventId)).IsEquivalentTo([_secondCommitted]);
  }

  [Test]
  public async Task FetchCollectiveSinkQueueAsync_ReturnsTheQueueInCommitOrderAsync() {
    var keyStream = Guid.CreateVersion7();
    await using (var conn = await _openAsync()) {
      await _emitCollectiveAsync(conn, _firstCommitted, keyStream);
      await _emitCollectiveAsync(conn, _secondCommitted, keyStream);
      await _stampAsync(conn);
    }
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var queue = await coordinator.FetchCollectiveSinkQueueAsync(keyStream);

    await Assert.That(queue).IsNotNull();
    await Assert.That(queue!.Select(q => q.EventId)).IsEquivalentTo(
      [_firstCommitted, _secondCommitted], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(queue.All(q => q.EventWorkId != Guid.Empty && q.CommitSequence is not null)).IsTrue();
  }

  // ── #1524: the head and the membership, each reading only what it returns ─────────────────────────

  [Test]
  public async Task SinkQueueHead_CollectiveRowsCarryTheirEventsVersionAsync() {
    var keyStream = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _emitCollectiveAsync(conn, _firstCommitted, keyStream);
    await _emitCollectiveAsync(conn, _secondCommitted, keyStream);
    await using (var cmd = conn.CreateCommand()) {
      // A row for any other perspective is not part of a sink queue and carries no version.
      cmd.CommandText = """
        INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
        VALUES (gen_random_uuid(), @sid, 'Some.Perspective', @eid, 0, 1, 0, now())
        """;
      cmd.Parameters.AddWithValue("sid", keyStream);
      cmd.Parameters.AddWithValue("eid", _secondCommitted);
      await cmd.ExecuteNonQueryAsync();
    }

    var rows = new List<(string Perspective, long? RowVersion, long EventVersion)>();
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = """
        SELECT pe.perspective_name, pe.stream_version, es.version
        FROM wh_perspective_events pe
        JOIN wh_event_store es ON es.stream_id = pe.stream_id AND es.event_id = pe.event_id
        WHERE pe.stream_id = @sid
        """;
      cmd.Parameters.AddWithValue("sid", keyStream);
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        rows.Add((reader.GetString(0), await reader.IsDBNullAsync(1) ? null : reader.GetInt64(1), reader.GetInt64(2)));
      }
    }

    var sink = rows.Where(r => r.Perspective == CollectiveRouting.SINK_PERSPECTIVE_NAME).ToList();
    await Assert.That(sink.Count).IsEqualTo(2);
    await Assert.That(sink.All(r => r.RowVersion == r.EventVersion)).IsTrue()
      .Because("Each sink row carries the version of the event it applies, set as the row is written.");
    await Assert.That(rows.Single(r => r.Perspective == "Some.Perspective").RowVersion).IsNull();
  }

  [Test]
  public async Task SinkQueueHead_StopsAtTheFirstRowTheCallerDoesNotNameAsync() {
    var keyStream = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var ids = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).ToArray();
    foreach (var id in ids) {
      await _emitCollectiveAsync(conn, id, keyStream);
    }
    await _stampAsync(conn);
    var work = (await _queueAsync(conn, keyStream)).Select(q => q.EventWorkId).ToArray();

    var throughTwo = await _headAsync(conn, keyStream, [work[0], work[1]]);
    var throughNone = await _headAsync(conn, keyStream, []);
    var secondOnly = await _headAsync(conn, keyStream, [work[1]]);
    var throughAll = await _headAsync(conn, keyStream, work);

    await Assert.That(throughTwo.Select(q => q.EventWorkId)).IsEquivalentTo(
      [work[0], work[1], work[2]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("The walk goes past the named rows and stops after the first row it was not given.");
    await Assert.That(throughNone.Select(q => q.EventWorkId)).IsEquivalentTo([work[0]]);
    await Assert.That(secondOnly.Select(q => q.EventWorkId)).IsEquivalentTo([work[0]])
      .Because("A named row behind one that is not named is not reached.");
    await Assert.That(throughAll.Select(q => q.EventId)).IsEquivalentTo(ids, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(throughAll.All(q => q.CommitSequence is not null)).IsTrue();
  }

  [Test]
  public async Task SinkQueueHead_IsInVersionOrder_WhenIdsRunBackwardAsync() {
    var keyStream = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _emitCollectiveAsync(conn, _firstCommitted, keyStream);
    await _emitCollectiveAsync(conn, _secondCommitted, keyStream);
    await _stampAsync(conn);
    var laterA = Guid.Parse("00000000-0000-7000-8000-0000000000a2");
    var laterB = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    await _emitCollectiveAsync(conn, laterA, keyStream);
    await _emitCollectiveAsync(conn, laterB, keyStream);
    var work = (await _queueAsync(conn, keyStream)).Select(q => q.EventWorkId).ToArray();

    var head = await _headAsync(conn, keyStream, work);

    await Assert.That(head.Select(q => q.EventId)).IsEquivalentTo(
      [_firstCommitted, _secondCommitted, laterA, laterB], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("Stamped rows follow their commits, and unstamped rows follow the order they committed in, which is "
        + "the order they will be stamped and replayed in, not the order of their ids.");
    await Assert.That(head[2].CommitSequence).IsNull();
  }

  [Test]
  public async Task SinkQueueHead_ReadsOnlyTheHead_NotTheWholeQueueAsync() {
    const int queued = 200;
    var keyStream = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    for (var i = 0; i < queued; i++) {
      await _emitCollectiveAsync(conn, Guid.CreateVersion7(), keyStream);
    }
    await _stampAsync(conn);
    var work = (await _queueAsync(conn, keyStream)).Select(q => q.EventWorkId).ToArray();
    await using (var analyze = conn.CreateCommand()) {
      analyze.CommandText = "ANALYZE wh_perspective_events";
      await analyze.ExecuteNonQueryAsync();
    }

    await using var tx = await conn.BeginTransactionAsync();
    var head = await _headAsync(conn, keyStream, [work[0], work[1]]);
    long rowsRead;
    long seqScans;
    await using (var stats = conn.CreateCommand()) {
      // This transaction's own reads of the work table: deterministic, and nothing else's.
      stats.CommandText = """
        SELECT COALESCE(t.idx_tup_fetch, 0) + COALESCE(t.seq_tup_read, 0), COALESCE(t.seq_scan, 0)
        FROM pg_stat_xact_user_tables t
        WHERE t.relid = to_regclass('wh_perspective_events')
        """;
      await using var reader = await stats.ExecuteReaderAsync();
      await reader.ReadAsync();
      rowsRead = reader.GetInt64(0);
      seqScans = reader.GetInt64(1);
    }
    await tx.RollbackAsync();

    await Assert.That(head.Count).IsEqualTo(3);
    await Assert.That(seqScans).IsEqualTo(0)
      .Because("The queue is walked on its index, in order, never scanned.");
    await Assert.That(rowsRead).IsLessThan(queued / 4)
      .Because($"A run holding the head of a {queued}-row queue reads a handful of rows, not the queue.");
  }

  [Test]
  public async Task SinkQueueMembers_AnswersForNamedRowsAndEventsOnlyAsync() {
    var keyStream = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var ids = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).ToArray();
    foreach (var id in ids) {
      await _emitCollectiveAsync(conn, id, keyStream);
    }
    var work = (await _queueAsync(conn, keyStream)).ToDictionary(q => q.EventId, q => q.EventWorkId);
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "UPDATE wh_perspective_events SET processed_at = now() WHERE event_id = @id";
      cmd.Parameters.AddWithValue("id", ids[1]);
      await cmd.ExecuteNonQueryAsync();
    }

    var members = await _membersAsync(conn, keyStream,
      workIds: [work[ids[0]], work[ids[1]], Guid.CreateVersion7()], eventIds: [ids[2]]);

    await Assert.That(members.Select(m => m.EventId)).IsEquivalentTo([ids[0], ids[2]])
      .Because("A named row still queued is a member, by its work id or its event; a processed row and an unknown id are not.");
  }

  [Test]
  public async Task FetchCollectiveSinkQueueHeadAsync_ReturnsTheHeadAsync() {
    var keyStream = Guid.CreateVersion7();
    await using (var conn = await _openAsync()) {
      await _emitCollectiveAsync(conn, _firstCommitted, keyStream);
      await _emitCollectiveAsync(conn, _secondCommitted, keyStream);
      await _stampAsync(conn);
    }
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var queue = await coordinator.FetchCollectiveSinkQueueAsync(keyStream);

    var head = await coordinator.FetchCollectiveSinkQueueHeadAsync(keyStream, []);

    await Assert.That(head).IsNotNull();
    await Assert.That(head!).IsEquivalentTo([queue![0]])
      .Because("Naming no rows, the head is the first row of the queue and nothing after it.");
  }

  [Test]
  public async Task FetchCollectiveSinkQueueMembersAsync_ReturnsTheNamedRowsStillQueuedAsync() {
    var keyStream = Guid.CreateVersion7();
    await using (var conn = await _openAsync()) {
      await _emitCollectiveAsync(conn, _firstCommitted, keyStream);
      await _emitCollectiveAsync(conn, _secondCommitted, keyStream);
      await _stampAsync(conn);
    }
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var queue = await coordinator.FetchCollectiveSinkQueueAsync(keyStream);

    var members = await coordinator.FetchCollectiveSinkQueueMembersAsync(
      keyStream, workIds: [queue![1].EventWorkId], eventIds: []);

    await Assert.That(members).IsEquivalentTo([queue[1]]);
  }

  // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  // One collective through the real outbox emit chain, in a transaction of its own, so each commits separately.
  private static async Task _emitCollectiveAsync(NpgsqlConnection conn, Guid messageId, Guid streamId) {
    var instanceId = Guid.CreateVersion7();
    await using (var insert = conn.CreateCommand()) {
      insert.CommandText = """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event,
           flags, created_at)
        VALUES
          (@id, 'test-dest', @type, 'env', '{"p":{}}'::jsonb, '{}'::jsonb,
           1, 0, @inst, NOW() + INTERVAL '5 minutes', 0, @sid, true, 1, NOW())
        """;
      insert.Parameters.AddWithValue("id", messageId);
      insert.Parameters.AddWithValue("type", COLLECTIVE_EVENT_TYPE);
      insert.Parameters.AddWithValue("inst", instanceId);
      insert.Parameters.AddWithValue("sid", streamId);
      await insert.ExecuteNonQueryAsync();
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    emit.Parameters.AddWithValue("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, new[] { messageId });
    emit.Parameters.AddWithValue("inst", instanceId);
    await emit.ExecuteNonQueryAsync();
  }

  private static async Task _stampAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stamp_pending_commit_sequences()";
    await cmd.ExecuteScalarAsync();
  }

  private static Task<List<CollectiveSinkQueueEntry>> _headAsync(NpgsqlConnection conn, Guid streamId, Guid[] through) =>
    _readAsync(conn, "SELECT * FROM wh_collective_sink_queue_head(@sid, @through)", cmd => {
      cmd.Parameters.AddWithValue("sid", streamId);
      cmd.Parameters.AddWithValue("through", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, through);
    });

  private static Task<List<CollectiveSinkQueueEntry>> _membersAsync(NpgsqlConnection conn, Guid streamId, Guid[] workIds, Guid[] eventIds) =>
    _readAsync(conn, "SELECT * FROM wh_collective_sink_queue_members(@sid, @work, @events)", cmd => {
      cmd.Parameters.AddWithValue("sid", streamId);
      cmd.Parameters.AddWithValue("work", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, workIds);
      cmd.Parameters.AddWithValue("events", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, eventIds);
    });

  private static async Task<List<CollectiveSinkQueueEntry>> _readAsync(NpgsqlConnection conn, string sql, Action<NpgsqlCommand> bind) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    bind(cmd);
    var rows = new List<CollectiveSinkQueueEntry>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new CollectiveSinkQueueEntry(reader.GetGuid(0), reader.GetGuid(1), await reader.IsDBNullAsync(2) ? null : reader.GetInt64(2)));
    }
    return rows;
  }

  private static async Task<List<CollectiveSinkQueueEntry>> _queueAsync(NpgsqlConnection conn, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM wh_collective_sink_queue(@sid)";
    cmd.Parameters.AddWithValue("sid", streamId);
    var rows = new List<CollectiveSinkQueueEntry>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new CollectiveSinkQueueEntry(reader.GetGuid(0), reader.GetGuid(1), await reader.IsDBNullAsync(2) ? null : reader.GetInt64(2)));
    }
    return rows;
  }
}
