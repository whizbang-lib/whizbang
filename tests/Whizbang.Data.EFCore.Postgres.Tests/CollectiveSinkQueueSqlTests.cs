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
