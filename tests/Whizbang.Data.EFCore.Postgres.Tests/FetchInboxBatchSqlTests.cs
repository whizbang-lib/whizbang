using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <c>fetch_inbox_batch</c> — the per-stream-id drainer's payload-fetch SQL function
/// for inbox messages. Mirror of fetch_outbox_batch with inbox table semantics
/// (handler_name instead of destination, received_at instead of created_at, no published_at column).
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public class FetchInboxBatchSqlTests : EFCoreTestBase {

  [Test]
  public async Task FetchInboxBatch_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname='fetch_inbox_batch' AND pronamespace='public'::regnamespace);";
    var exists = (bool)(await command.ExecuteScalarAsync())!;
    await Assert.That(exists).IsTrue();
  }

  [Test]
  public async Task FetchInboxBatch_ReturnsRowsForOwnedStreams_InReceivedAtOrderAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _registerInstanceAsync(connection, instanceId);

    // Use TrackedGuid (UUIDv7) so message_ids are monotonic — fetch_inbox_batch falls
    // back to message_id ordering when commit_sequence is unset (non-event rows).
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
      await _insertInboxRowAsync(connection, ids[i], streamId, instanceId, receivedAt: times[i]);
    }

    var fetched = await _fetchInboxBatchAsync(connection, new[] { streamId }, instanceId, maxPerStream: 100);

    await Assert.That(fetched.Count).IsEqualTo(3);
    await Assert.That(fetched[0].MessageId).IsEqualTo(ids[0]);
    await Assert.That(fetched[1].MessageId).IsEqualTo(ids[1]);
    await Assert.That(fetched[2].MessageId).IsEqualTo(ids[2]);
  }

  [Test]
  public async Task FetchInboxBatch_FiltersOutOtherInstancesRowsAsync() {
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
    await _insertInboxRowAsync(connection, mineMessage, streamId, meId);
    await _insertInboxRowAsync(connection, theirMessage, streamId, otherId);

    var fetched = await _fetchInboxBatchAsync(connection, new[] { streamId }, meId, maxPerStream: 100);

    await Assert.That(fetched.Count).IsEqualTo(1);
    await Assert.That(fetched[0].MessageId).IsEqualTo(mineMessage);
  }

  [Test]
  public async Task FetchInboxBatch_FiltersRowsWithProcessedAtSet_DebugModeRetainedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _registerInstanceAsync(connection, instanceId);

    var unprocessed = Guid.NewGuid();
    var alreadyProcessed = Guid.NewGuid();
    await _insertInboxRowAsync(connection, unprocessed, streamId, instanceId);
    await _insertInboxRowAsync(connection, alreadyProcessed, streamId, instanceId, processedAt: DateTimeOffset.UtcNow);

    var fetched = await _fetchInboxBatchAsync(connection, new[] { streamId }, instanceId, maxPerStream: 100);

    await Assert.That(fetched.Count).IsEqualTo(1);
    await Assert.That(fetched[0].MessageId).IsEqualTo(unprocessed);
  }

  [Test]
  public async Task FetchInboxBatch_RespectsMaxPerStreamCapAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _registerInstanceAsync(connection, instanceId);

    for (var i = 0; i < 10; i++) {
      await _insertInboxRowAsync(connection, Guid.NewGuid(), streamId, instanceId);
    }

    var fetched = await _fetchInboxBatchAsync(connection, new[] { streamId }, instanceId, maxPerStream: 3);

    await Assert.That(fetched.Count).IsEqualTo(3);
  }

  private static async Task<List<InboxBatchRow>> _fetchInboxBatchAsync(
      NpgsqlConnection connection, Guid[] streamIds, Guid instanceId, int maxPerStream) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT * FROM fetch_inbox_batch(@p_stream_ids, @p_instance_id, @p_max_per_stream)";
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = streamIds });
    cmd.Parameters.AddWithValue("p_instance_id", instanceId);
    cmd.Parameters.AddWithValue("p_max_per_stream", maxPerStream);

    var rows = new List<InboxBatchRow>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new InboxBatchRow {
        MessageId = reader.GetGuid(0),
        StreamId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
        HandlerName = reader.GetString(2),
        MessageType = reader.GetString(3)
      });
    }
    return rows;
  }

  private sealed class InboxBatchRow {
    public Guid MessageId { get; init; }
    public Guid? StreamId { get; init; }
    public string HandlerName { get; init; } = string.Empty;
    public string MessageType { get; init; } = string.Empty;
  }

  private static async Task _insertInboxRowAsync(
      NpgsqlConnection connection, Guid messageId, Guid streamId, Guid instanceId,
      DateTimeOffset? receivedAt = null, DateTimeOffset? processedAt = null) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         instance_id, lease_expiry, stream_id, partition_number, processed_at)
      VALUES (@msg, 'TestHandler', 'TestEvent', '{""payload"":1}', '{""hop"":1}', 1, 0, @received,
              @inst, NOW() + INTERVAL '5 minutes', @stream, 0, @processed)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("inst", instanceId);
    ins.Parameters.Add(new NpgsqlParameter("received", NpgsqlDbType.TimestampTz) { Value = receivedAt ?? DateTimeOffset.UtcNow });
    ins.Parameters.Add(new NpgsqlParameter("processed", NpgsqlDbType.TimestampTz) { Value = (object?)processedAt ?? DBNull.Value });
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
