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
public class EFCoreFetchBatchTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> Coord(WorkCoordinationDbContext ctx) =>
    new(ctx, JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task FetchOutboxBatchAsync_EmptyStreamIds_ReturnsEmptyList_WithNoSqlAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);

    var result = await coord.FetchOutboxBatchAsync(Array.Empty<Guid>(), instanceId: Guid.NewGuid(), maxPerStream: 100);

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
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope, status, attempts,
           created_at, stream_id, partition_number, instance_id, lease_expiry, is_event)
        VALUES (@msg, 'topic-x', 'MyType', 'MyEnvelope', '{""p"":1}', '{""h"":1}', '{""t"":""tenant""}',
                3, 0, NOW(), @stream, 7, @inst, NOW() + INTERVAL '5 minutes', true)";
      ins.Parameters.AddWithValue("msg", messageId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("inst", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    var rows = await coord.FetchOutboxBatchAsync(new[] { streamId }, instanceId, maxPerStream: 100);

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

    var result = await coord.FetchInboxBatchAsync(Array.Empty<Guid>(), instanceId: Guid.NewGuid(), maxPerStream: 100);

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
      ins.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, scope, status, attempts, received_at,
           instance_id, lease_expiry, stream_id, partition_number, is_event)
        VALUES (@msg, 'MyHandler', 'MyType', '{""p"":1}', '{""h"":1}', '{""t"":""tenant""}',
                3, 0, NOW(), @inst, NOW() + INTERVAL '5 minutes', @stream, 9, true)";
      ins.Parameters.AddWithValue("msg", messageId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("inst", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    var rows = await coord.FetchInboxBatchAsync(new[] { streamId }, instanceId, maxPerStream: 100);

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
}
