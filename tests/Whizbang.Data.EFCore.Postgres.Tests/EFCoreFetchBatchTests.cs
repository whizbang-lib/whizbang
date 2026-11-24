// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for the EFCore-side wrapper around <c>fetch_outbox_batch</c> / <c>fetch_inbox_batch</c>.
/// SQL-level invariants are pinned in <see cref="FetchOutboxBatchSqlTests"/> and
/// <see cref="FetchInboxBatchSqlTests"/>; this suite verifies the C# parameter wiring,
/// row mapping, and empty-input early-return.
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
[Category("Shard2")]
public class EFCoreFetchBatchTests : EFCoreTestBase {

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> Coord(WorkCoordinationDbContext ctx) =>
    new(ctx, JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task FetchOutboxBatchAsync_EmptyStreamIds_ReturnsEmptyList_WithNoSqlAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);

    var result = await coord.FetchOutboxBatchAsync([], instanceId: Guid.NewGuid(), maxPerStream: 100);

    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchOutboxBatchAsync_RoundTripsAllColumnsAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
        ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
      ins.Parameters.AddWithValue("id", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = """

        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope, status, attempts,
           created_at, stream_id, partition_number, instance_id, lease_expiry, is_event)
        VALUES (@msg, 'topic-x', 'MyType', 'MyEnvelope', '{"p":1}', '{"h":1}', '{"t":"tenant"}',
                3, 0, NOW(), @stream, 7, @inst, NOW() + INTERVAL '5 minutes', true)
""";
      ins.Parameters.AddWithValue("msg", messageId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("inst", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    var rows = await coord.FetchOutboxBatchAsync([streamId], instanceId, maxPerStream: 100);

    await Assert.That(rows.Count).IsEqualTo(1);
    var row = rows[0];
    await Assert.That(row.MessageId).IsEqualTo(messageId);
    await Assert.That(row.StreamId).IsEqualTo(streamId);
    await Assert.That(row.Destination).IsEqualTo("topic-x");
    await Assert.That(row.MessageType).IsEqualTo("MyType");
    await Assert.That(row.EnvelopeType).IsEqualTo("MyEnvelope");
    await Assert.That(row.EventData).Contains("\"p\"");
    await Assert.That(row.Metadata).Contains("\"h\"");
    await Assert.That(row.Scope).IsNotNull();
    await Assert.That(row.Status).IsEqualTo(3);
    await Assert.That(row.Attempts).IsEqualTo(0);
    await Assert.That(row.PartitionNumber).IsEqualTo(7);
    await Assert.That(row.IsEvent).IsTrue();
  }

  [Test]
  public async Task FetchInboxBatchAsync_EmptyStreamIds_ReturnsEmptyList_WithNoSqlAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);

    var result = await coord.FetchInboxBatchAsync([], instanceId: Guid.NewGuid(), maxPerStream: 100);

    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchInboxBatchAsync_RoundTripsAllColumnsAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
        ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
      ins.Parameters.AddWithValue("id", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = """

        WITH m AS (
          INSERT INTO wh_inbox
            (message_id, handler_name, message_type, event_data, metadata, scope, received_at,
             stream_id, is_event)
          VALUES (@msg, 'MyHandler', 'MyType', '{"p":1}', '{"h":1}', '{"t":"tenant"}',
                  NOW(), @stream, true)
          RETURNING message_id, stream_id, received_at, priority, is_event
        )
        INSERT INTO wh_inbox_state
          (message_id, stream_id, received_at, priority, is_event,
           status, attempts, instance_id, lease_expiry, partition_number, chain_emitted_at)
        SELECT message_id, stream_id, received_at, priority, is_event,
               3, 0, @inst, NOW() + INTERVAL '5 minutes', 9, NOW() FROM m
""";
      ins.Parameters.AddWithValue("msg", messageId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("inst", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    var rows = await coord.FetchInboxBatchAsync([streamId], instanceId, maxPerStream: 100);

    await Assert.That(rows.Count).IsEqualTo(1);
    var row = rows[0];
    await Assert.That(row.MessageId).IsEqualTo(messageId);
    await Assert.That(row.StreamId).IsEqualTo(streamId);
    await Assert.That(row.HandlerName).IsEqualTo("MyHandler");
    await Assert.That(row.MessageType).IsEqualTo("MyType");
    await Assert.That(row.EventData).Contains("\"p\"");
    await Assert.That(row.Metadata).Contains("\"h\"");
    await Assert.That(row.Scope).IsNotNull();
    await Assert.That(row.Status).IsEqualTo(3);
    await Assert.That(row.Attempts).IsEqualTo(0);
    await Assert.That(row.PartitionNumber).IsEqualTo(9);
    await Assert.That(row.IsEvent).IsTrue();
  }

  [Test]
  public async Task FetchOutboxBatchAsync_ARowWithItsOptionalColumnsUnset_ReadsThemAsNullAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);
    var conn = await _openAsync(dbContext);
    var instanceId = Guid.NewGuid();
    var messageId = Guid.NewGuid();
    await _registerInstanceAsync(conn, instanceId);

    // A message with no stream is its own stream, keyed by its id; destination, envelope type, scope and partition
    // are all optional on the row.
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope, status, attempts,
           created_at, stream_id, partition_number, instance_id, lease_expiry, is_event)
        VALUES (@msg, NULL, 'MyType', NULL, '{"p":1}', '{"h":1}', NULL,
                3, 0, NOW(), NULL, NULL, @inst, NOW() + INTERVAL '5 minutes', false)
        """;
      ins.Parameters.AddWithValue("msg", messageId);
      ins.Parameters.AddWithValue("inst", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    var rows = await coord.FetchOutboxBatchAsync([messageId], instanceId, maxPerStream: 100);

    await Assert.That(rows.Count).IsEqualTo(1);
    var row = rows[0];
    await Assert.That(row.MessageId).IsEqualTo(messageId);
    await Assert.That(row.StreamId.HasValue).IsFalse();
    await Assert.That(row.Destination).IsNull();
    await Assert.That(row.EnvelopeType).IsNull();
    await Assert.That(row.Scope).IsNull();
    await Assert.That(row.PartitionNumber.HasValue).IsFalse();
  }

  [Test]
  public async Task FetchInboxBatchAsync_ARowWithItsOptionalColumnsUnset_ReadsThemAsNullAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);
    var conn = await _openAsync(dbContext);
    var instanceId = Guid.NewGuid();
    var messageId = Guid.NewGuid();
    await _registerInstanceAsync(conn, instanceId);

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = """
        WITH m AS (
          INSERT INTO wh_inbox
            (message_id, handler_name, message_type, event_data, metadata, scope, received_at,
             stream_id, is_event)
          VALUES (@msg, 'MyHandler', 'MyType', '{"p":1}', '{"h":1}', NULL, NOW(), NULL, false)
          RETURNING message_id, stream_id, received_at, priority, is_event
        )
        INSERT INTO wh_inbox_state
          (message_id, stream_id, received_at, priority, is_event,
           status, attempts, instance_id, lease_expiry, partition_number, chain_emitted_at)
        SELECT message_id, stream_id, received_at, priority, is_event,
               3, 0, @inst, NOW() + INTERVAL '5 minutes', NULL, NOW() FROM m
        """;
      ins.Parameters.AddWithValue("msg", messageId);
      ins.Parameters.AddWithValue("inst", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    var rows = await coord.FetchInboxBatchAsync([messageId], instanceId, maxPerStream: 100);

    await Assert.That(rows.Count).IsEqualTo(1);
    var row = rows[0];
    await Assert.That(row.MessageId).IsEqualTo(messageId);
    await Assert.That(row.StreamId.HasValue).IsFalse();
    await Assert.That(row.Scope).IsNull();
    await Assert.That(row.PartitionNumber.HasValue).IsFalse();
  }

  [Test]
  public async Task FetchOutboxBatchAsync_AReceivedEventWithAnError_CarriesItsSequencesOriginAndErrorAsync() {
    // An outbox event row joins its event-store row for the commit sequence and the origin a received event keeps;
    // the row's own error rides along. A second row with neither reads them as unset.
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);
    var conn = await _openAsync(dbContext);
    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var received = Guid.NewGuid();
    var plain = Guid.NewGuid();
    var origin = Guid.NewGuid();
    await _registerInstanceAsync(conn, instanceId);
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, created_at,
           commit_sequence, origin_service_id, origin_commit_sequence)
        VALUES (@received, @stream, @stream, 'TestAgg', 1, 'MyType', NOW(), 4100, @origin, 77);
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
           created_at, stream_id, partition_number, instance_id, lease_expiry, is_event, error)
        VALUES
          (@received, 'topic', 'MyType', 'MyEnvelope', '{}', '{}', 1, 2, NOW(), @stream, 0, @inst,
           NOW() + INTERVAL '5 minutes', true, 'boom'),
          (@plain, 'topic', 'MyType', 'MyEnvelope', '{}', '{}', 1, 0, NOW(), @stream, 0, @inst,
           NOW() + INTERVAL '5 minutes', false, NULL)
        """;
      ins.Parameters.AddWithValue("received", received);
      ins.Parameters.AddWithValue("plain", plain);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("origin", origin);
      ins.Parameters.AddWithValue("inst", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    var rows = await coord.FetchOutboxBatchAsync([streamId], instanceId, maxPerStream: 100);

    var withAll = rows.Single(r => r.MessageId == received);
    await Assert.That(withAll.CommitSequence).IsEqualTo(4100L);
    await Assert.That(withAll.OriginServiceId).IsEqualTo(origin);
    await Assert.That(withAll.OriginCommitSequence).IsEqualTo(77L);
    await Assert.That(withAll.Error).IsEqualTo("boom");
    var withNone = rows.Single(r => r.MessageId == plain);
    await Assert.That(withNone.CommitSequence.HasValue).IsFalse();
    await Assert.That(withNone.OriginServiceId.HasValue).IsFalse();
    await Assert.That(withNone.OriginCommitSequence.HasValue).IsFalse();
    await Assert.That(withNone.Error).IsNull();
  }

  [Test]
  public async Task FetchInboxBatchAsync_ARowWithAnError_CarriesItAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);
    var conn = await _openAsync(dbContext);
    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var failing = Guid.NewGuid();
    var clean = Guid.NewGuid();
    await _registerInstanceAsync(conn, instanceId);
    foreach (var (messageId, error) in new[] { (failing, "boom"), (clean, (string?)null) }) {
      await using var ins = conn.CreateCommand();
      ins.CommandText = """
        WITH m AS (
          INSERT INTO wh_inbox
            (message_id, handler_name, message_type, event_data, metadata, scope, received_at, stream_id, is_event)
          VALUES (@msg, 'MyHandler', 'MyType', '{}', '{}', NULL, NOW(), @stream, false)
          RETURNING message_id, stream_id, received_at, priority, is_event
        )
        INSERT INTO wh_inbox_state
          (message_id, stream_id, received_at, priority, is_event,
           status, attempts, instance_id, lease_expiry, partition_number, chain_emitted_at, error)
        SELECT message_id, stream_id, received_at, priority, is_event,
               1, 1, @inst, NOW() + INTERVAL '5 minutes', 0, NOW(), @error FROM m
        """;
      ins.Parameters.AddWithValue("msg", messageId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("inst", instanceId);
      ins.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
      await ins.ExecuteNonQueryAsync();
    }

    var rows = await coord.FetchInboxBatchAsync([streamId], instanceId, maxPerStream: 100);

    await Assert.That(rows.Single(r => r.MessageId == failing).Error).IsEqualTo("boom");
    await Assert.That(rows.Single(r => r.MessageId == clean).Error).IsNull();
  }

  // A schema whose fetch functions predate the commit-sequence, origin, error and priority columns: the reader leaves
  // those fields unset rather than failing. Each test has a database of its own, so replacing the function here is local.
  [Test]
  public async Task FetchOutboxBatchAsync_AnOlderFunctionWithoutTheLaterColumns_LeavesThemUnsetAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);
    var conn = await _openAsync(dbContext);
    await using (var replace = conn.CreateCommand()) {
      replace.CommandText = """
        SELECT drop_all_overloads('fetch_outbox_batch');
        CREATE FUNCTION fetch_outbox_batch(p_stream_ids UUID[], p_instance_id UUID, p_max_per_stream INTEGER, p_max_bytes BIGINT)
        RETURNS TABLE(message_id UUID, stream_id UUID, destination TEXT, message_type TEXT, envelope_type TEXT,
                      event_data TEXT, metadata TEXT, scope TEXT, status INTEGER, attempts INTEGER,
                      partition_number INTEGER, is_event BOOLEAN)
        LANGUAGE sql AS $$
          SELECT p_stream_ids[1], p_stream_ids[1], 'topic', 'MyType', 'MyEnvelope', '{}', '{}', NULL, 1, 0, 0, false
        $$;
        """;
      await replace.ExecuteNonQueryAsync();
    }
    var streamId = Guid.NewGuid();

    var rows = await coord.FetchOutboxBatchAsync([streamId], Guid.NewGuid(), maxPerStream: 100);

    await Assert.That(rows.Count).IsEqualTo(1);
    var row = rows[0];
    await Assert.That(row.CommitSequence.HasValue).IsFalse();
    await Assert.That(row.OriginServiceId.HasValue).IsFalse();
    await Assert.That(row.OriginCommitSequence.HasValue).IsFalse();
    await Assert.That(row.Error).IsNull();
    await Assert.That(row.Priority).IsEqualTo(Whizbang.Core.Priority.WorkPriority.UNDECLARED);
  }

  [Test]
  public async Task FetchInboxBatchAsync_AnOlderFunctionWithoutTheLaterColumns_LeavesThemUnsetAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);
    var conn = await _openAsync(dbContext);
    await using (var replace = conn.CreateCommand()) {
      replace.CommandText = """
        -- 576 (#2551): the drivers call the stream-ordered fetch, which always returns stream_position.
        SELECT drop_all_overloads('fetch_inbox_batch_in_stream_order');
        CREATE FUNCTION fetch_inbox_batch_in_stream_order(p_stream_ids UUID[], p_instance_id UUID, p_max_per_stream INTEGER, p_max_bytes BIGINT)
        RETURNS TABLE(message_id UUID, stream_id UUID, handler_name TEXT, message_type TEXT, event_data TEXT,
                      metadata TEXT, scope TEXT, status INTEGER, attempts INTEGER, partition_number INTEGER,
                      is_event BOOLEAN, stream_position BIGINT)
        LANGUAGE sql AS $$
          SELECT p_stream_ids[1], p_stream_ids[1], 'MyHandler', 'MyType', '{}', '{}', NULL, 1, 0, 0, false, 1::bigint
        $$;
        """;
      await replace.ExecuteNonQueryAsync();
    }
    var streamId = Guid.NewGuid();

    var rows = await coord.FetchInboxBatchAsync([streamId], Guid.NewGuid(), maxPerStream: 100);

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].Error).IsNull();
    await Assert.That(rows[0].Priority).IsEqualTo(0);
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    ins.Parameters.AddWithValue("id", instanceId);
    await ins.ExecuteNonQueryAsync();
  }
}
