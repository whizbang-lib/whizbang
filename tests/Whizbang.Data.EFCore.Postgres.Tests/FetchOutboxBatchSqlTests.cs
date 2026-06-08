using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <c>fetch_outbox_batch</c> — the per-stream-id drainer's payload-fetch SQL function.
/// Replaces the regressed "claim_work returns full bodies for outbox/inbox" pattern with the
/// archive-specified split: claim_work returns stream_ids, drainer fetches per-stream rows.
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public class FetchOutboxBatchSqlTests : EFCoreTestBase {

  [Test]
  public async Task FetchOutboxBatch_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname='fetch_outbox_batch' AND pronamespace='public'::regnamespace);";
    var exists = (bool)(await command.ExecuteScalarAsync())!;
    await Assert.That(exists).IsTrue();
  }

  [Test]
  public async Task FetchOutboxBatch_ReturnsRowsForOwnedStreams_InCreatedAtOrderAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _registerInstanceAsync(connection, instanceId);

    // Slice 26.9: fetch_outbox_batch orders by (stream_id, commit_sequence NULLS LAST,
    // message_id). For non-event rows (no event_store entry), commit_sequence is NULL
    // and the fall-back is message_id ASC. Use TrackedGuid (UUIDv7, monotonic-at-generation)
    // so message_ids match insertion order — that's the contract the function relies on
    // per feedback_use_trackedguid.
    var ids = new[] {
      (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo(),
      (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo(),
      (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo()
    };
    var times = new[] {
      DateTimeOffset.UtcNow.AddSeconds(-30),
      DateTimeOffset.UtcNow.AddSeconds(-20),
      DateTimeOffset.UtcNow.AddSeconds(-10)
    };
    for (var i = 0; i < ids.Length; i++) {
      await _insertOutboxRowAsync(connection, ids[i], streamId, instanceId, createdAt: times[i]);
    }

    var fetched = await _fetchOutboxBatchAsync(connection, [streamId], instanceId, maxPerStream: 100);

    await Assert.That(fetched.Count).IsEqualTo(3);
    await Assert.That(fetched[0].MessageId).IsEqualTo(ids[0]);
    await Assert.That(fetched[1].MessageId).IsEqualTo(ids[1]);
    await Assert.That(fetched[2].MessageId).IsEqualTo(ids[2]);
  }

  [Test]
  public async Task FetchOutboxBatch_FiltersOutOtherInstancesRowsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var meId = Guid.NewGuid();
    var otherId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _registerInstanceAsync(connection, meId);
    await _registerInstanceAsync(connection, otherId);

    var mineMessage = Guid.NewGuid();
    var theirMessage = Guid.NewGuid();
    await _insertOutboxRowAsync(connection, mineMessage, streamId, meId);
    await _insertOutboxRowAsync(connection, theirMessage, streamId, otherId);

    var fetched = await _fetchOutboxBatchAsync(connection, [streamId], meId, maxPerStream: 100);

    await Assert.That(fetched.Count).IsEqualTo(1);
    await Assert.That(fetched[0].MessageId).IsEqualTo(mineMessage);
  }

  [Test]
  public async Task FetchOutboxBatch_FiltersRowsWithPublishedAtSet_DebugModeRetainedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _registerInstanceAsync(connection, instanceId);

    var unpublished = Guid.NewGuid();
    var alreadyPublished = Guid.NewGuid();
    await _insertOutboxRowAsync(connection, unpublished, streamId, instanceId);
    await _insertOutboxRowAsync(connection, alreadyPublished, streamId, instanceId, publishedAt: DateTimeOffset.UtcNow);

    var fetched = await _fetchOutboxBatchAsync(connection, [streamId], instanceId, maxPerStream: 100);

    await Assert.That(fetched.Count).IsEqualTo(1);
    await Assert.That(fetched[0].MessageId).IsEqualTo(unpublished);
  }

  [Test]
  public async Task FetchOutboxBatch_RespectsMaxPerStreamCapAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _registerInstanceAsync(connection, instanceId);

    for (var i = 0; i < 10; i++) {
      await _insertOutboxRowAsync(connection, Guid.NewGuid(), streamId, instanceId);
    }

    var fetched = await _fetchOutboxBatchAsync(connection, [streamId], instanceId, maxPerStream: 3);

    await Assert.That(fetched.Count).IsEqualTo(3);
  }

  [Test]
  public async Task FetchOutboxBatch_FetchesAcrossMultipleStreams_ReturnsAllAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();
    await _registerInstanceAsync(connection, instanceId);

    await _insertOutboxRowAsync(connection, Guid.NewGuid(), streamA, instanceId);
    await _insertOutboxRowAsync(connection, Guid.NewGuid(), streamA, instanceId);
    await _insertOutboxRowAsync(connection, Guid.NewGuid(), streamB, instanceId);

    var fetched = await _fetchOutboxBatchAsync(connection, [streamA, streamB], instanceId, maxPerStream: 100);

    await Assert.That(fetched.Count).IsEqualTo(3);
  }

  /// <summary>
  /// v0.658 slot-3 fix: a row with <c>stream_id = Guid.Empty</c> (the producer-side
  /// bug) MUST be drainable via the <c>message_id</c>-as-sentinel pattern that the
  /// coordinator emits (v0.657 slice 3's Empty→WorkId fallback).
  /// </summary>
  /// <remarks>
  /// The slot-3 forensic loop: v0.657 made the bug visible (Warning fires, drainer
  /// enters drain for the WorkId sentinel) but <c>fetch_outbox_batch</c> queried
  /// <c>WHERE stream_id = ANY(p_stream_ids)</c>, which can't match a row whose
  /// <c>stream_id</c> is Empty against a sentinel value of <c>message_id</c>. The
  /// row was observable but un-drainable. This test locks the structural fix:
  /// when the SQL filter is extended to also match
  /// <c>message_id = ANY(p_stream_ids) AND (stream_id IS NULL OR stream_id = Empty)</c>,
  /// the v0.657 WorkId-as-sentinel path actually retrieves the row.
  /// </remarks>
  [Test]
  public async Task FetchOutboxBatch_RowWithEmptyStreamId_FoundViaMessageIdSentinelAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var messageId = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    await _registerInstanceAsync(connection, instanceId);
    await _insertOutboxRowAsync(connection, messageId, streamId: Guid.Empty, instanceId);

    // v0.657 slice 3 emits message_id as the sentinel for Empty-stream rows.
    var fetched = await _fetchOutboxBatchAsync(connection, [messageId], instanceId, maxPerStream: 100);

    await Assert.That(fetched.Count).IsEqualTo(1)
      .Because("The slot-3 stuck row had stream_id = Empty; the coordinator's WorkId fallback emits message_id as the sentinel. fetch_outbox_batch MUST match the row via message_id when its stream_id is the non-routable Empty marker, otherwise the row stays stuck forever despite the v0.657 instrumentation surfacing it.");
    await Assert.That(fetched[0].MessageId).IsEqualTo(messageId);
  }

  /// <summary>
  /// Mirror invariant for NULL stream_id (the documented singleton-stream marker).
  /// Pre-v0.658, NULL-stream rows could be stored but never drained — fetch_outbox_batch
  /// silently filtered them out because <c>NULL = ANY(...)</c> is NULL/false.
  /// </summary>
  [Test]
  public async Task FetchOutboxBatch_RowWithNullStreamId_FoundViaMessageIdSentinelAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var messageId = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    await _registerInstanceAsync(connection, instanceId);
    await _insertOutboxRowWithNullStreamAsync(connection, messageId, instanceId);

    var fetched = await _fetchOutboxBatchAsync(connection, [messageId], instanceId, maxPerStream: 100);

    await Assert.That(fetched.Count).IsEqualTo(1)
      .Because("NULL stream_id is the documented singleton-stream marker; the message_id-as-sentinel pattern that v0.657 emits MUST resolve to the actual row so singleton-stream messages can drain.");
    await Assert.That(fetched[0].MessageId).IsEqualTo(messageId);
  }

  /// <summary>
  /// Negative: a real-stream row MUST still match via its actual stream_id —
  /// the message-id-as-sentinel branch is additive, not a replacement.
  /// </summary>
  [Test]
  public async Task FetchOutboxBatch_RealStreamRow_StillMatchesByStreamIdAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    var messageId = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    await _registerInstanceAsync(connection, instanceId);
    await _insertOutboxRowAsync(connection, messageId, streamId, instanceId);

    // Real-stream rows match via stream_id (the original behavior, not message_id).
    var fetched = await _fetchOutboxBatchAsync(connection, [streamId], instanceId, maxPerStream: 100);

    await Assert.That(fetched.Count).IsEqualTo(1);
    await Assert.That(fetched[0].MessageId).IsEqualTo(messageId);
  }

  /// <summary>
  /// Negative: passing a real stream_id MUST NOT also match unrelated rows
  /// whose message_id happens to equal that GUID. The message-id branch only
  /// activates when the row's stream_id is NULL or Empty.
  /// </summary>
  [Test]
  public async Task FetchOutboxBatch_RealStreamRow_NotMatchedByOtherRowsMessageIdAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var realStreamA = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    var realStreamB = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    var rowAMessage = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    var rowBMessage = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    await _registerInstanceAsync(connection, instanceId);
    await _insertOutboxRowAsync(connection, rowAMessage, realStreamA, instanceId);
    await _insertOutboxRowAsync(connection, rowBMessage, realStreamB, instanceId);

    // Sentinels [realStreamA, rowBMessage] — A's stream_id matches (real-stream branch),
    // but B should NOT match: its stream_id is real (B's actual stream_id), not NULL/Empty.
    var fetched = await _fetchOutboxBatchAsync(connection, [realStreamA, rowBMessage], instanceId, maxPerStream: 100);

    await Assert.That(fetched.Count).IsEqualTo(1)
      .Because("The message-id-as-sentinel branch must only activate for NULL/Empty stream rows. A real-stream row's message_id appearing in p_stream_ids is just an unrelated GUID and must not produce a phantom match.");
    await Assert.That(fetched[0].MessageId).IsEqualTo(rowAMessage);
  }

  private static async Task _insertOutboxRowWithNullStreamAsync(
      NpgsqlConnection connection, Guid messageId, Guid instanceId) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, instance_id, lease_expiry)
      VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{""payload"":1}', '{""hop"":1}', 1, 0,
              NOW(), NULL, NULL, @inst, NOW() + INTERVAL '5 minutes')";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("inst", instanceId);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task<List<OutboxBatchRow>> _fetchOutboxBatchAsync(
      NpgsqlConnection connection, Guid[] streamIds, Guid instanceId, int maxPerStream) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT * FROM fetch_outbox_batch(@p_stream_ids, @p_instance_id, @p_max_per_stream)";
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = streamIds });
    cmd.Parameters.AddWithValue("p_instance_id", instanceId);
    cmd.Parameters.AddWithValue("p_max_per_stream", maxPerStream);

    var rows = new List<OutboxBatchRow>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new OutboxBatchRow {
        MessageId = reader.GetGuid(0),
        StreamId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
        Destination = reader.IsDBNull(2) ? null : reader.GetString(2),
        MessageType = reader.GetString(3),
        EnvelopeType = reader.IsDBNull(4) ? null : reader.GetString(4)
      });
    }
    return rows;
  }

  private sealed class OutboxBatchRow {
    public Guid MessageId { get; init; }
    public Guid? StreamId { get; init; }
    public string? Destination { get; init; }
    public string MessageType { get; init; } = string.Empty;
    public string? EnvelopeType { get; init; }
  }

  private static async Task _insertOutboxRowAsync(
      NpgsqlConnection connection, Guid messageId, Guid streamId, Guid instanceId,
      DateTimeOffset? createdAt = null, DateTimeOffset? publishedAt = null) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, instance_id, lease_expiry, published_at)
      VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{""payload"":1}', '{""hop"":1}', 1, 0,
              @created, @stream, 0, @inst, NOW() + INTERVAL '5 minutes', @pub)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("inst", instanceId);
    ins.Parameters.Add(new NpgsqlParameter("created", NpgsqlDbType.TimestampTz) { Value = createdAt ?? DateTimeOffset.UtcNow });
    ins.Parameters.Add(new NpgsqlParameter("pub", NpgsqlDbType.TimestampTz) { Value = (object?)publishedAt ?? DBNull.Value });
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }
}
