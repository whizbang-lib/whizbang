using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Phase H step 6 slice 1 regression locks for stream-ownership pinning.
///
/// These tests pin the architectural invariants the original archive plan called out
/// but the Phase H step 3 decomposition silently dropped:
///
/// 1. <c>store_outbox_messages</c> / <c>store_inbox_messages</c> UPSERT into
///    <c>wh_active_streams</c> on first event for a stream (producer-instance pinning,
///    ON CONFLICT DO NOTHING — first-write-wins).
/// 2. <c>cleanup_completed_streams(UUID[])</c> evicts streams with no pending work
///    across outbox / inbox / perspective_events.
/// 3. <c>register_instance_heartbeat</c> opportunistically calls
///    <c>cleanup_stale_instances</c> when a peer has gone silent past the stale cutoff,
///    releasing leases held by the dead instance.
/// </summary>
/// <docs>fundamentals/work-coordinator/stream-ownership</docs>
[Category("Shard2")]
public class ActiveStreamsOwnershipSqlTests : EFCoreTestBase {

  // ----- store_outbox_messages UPSERT -----

  [Test]
  public async Task StoreOutboxMessages_NewStream_UpsertsActiveStreamsRowAsync() {
    // Slice 1 invariant: storing the first event for a brand-new stream creates an
    // ownership pin in wh_active_streams. Without this, claim_orphaned_outbox treats
    // every stream as "ownerless" and N instances race on every claim cycle.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceA = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var msgId = Guid.NewGuid();
    await _registerInstanceAsync(conn, instanceA, "test-svc", "host-A");

    var messages = $$"""
      [{
        "MessageId": "{{msgId}}",
        "Destination": "test-topic",
        "MessageType": "TestMessage",
        "EnvelopeType": "MessageEnvelope",
        "Envelope": {},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": false
      }]
      """;

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT * FROM store_outbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      cmd.Parameters.AddWithValue("p", messages);
      cmd.Parameters.AddWithValue("inst", instanceA);
      _ = await cmd.ExecuteScalarAsync();
    }

    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT assigned_instance_id FROM wh_active_streams WHERE stream_id = @sid";
    verify.Parameters.AddWithValue("sid", streamId);
    var owner = await verify.ExecuteScalarAsync();
    await Assert.That(owner).IsNotNull()
      .Because("store_outbox_messages must UPSERT into wh_active_streams on first event for the stream");
    await Assert.That((Guid)owner!).IsEqualTo(instanceA);
  }

  [Test]
  public async Task StoreOutboxMessages_ExistingActiveStreamsRow_DoesNotOverrideOwnerAsync() {
    // ON CONFLICT DO NOTHING semantics: producer that already has an owner does NOT
    // get to steal ownership on every event store. First-write-wins.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceA = Guid.NewGuid();
    var instanceB = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _registerInstanceAsync(conn, instanceA, "test-svc", "host-A");
    await _registerInstanceAsync(conn, instanceB, "test-svc", "host-B");

    // Pre-pin ownership to A.
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at)
                          VALUES (@sid, 0, @inst, NOW())";
      cmd.Parameters.AddWithValue("sid", streamId);
      cmd.Parameters.AddWithValue("inst", instanceA);
      await cmd.ExecuteNonQueryAsync();
    }

    // B stores an event for the same stream — must NOT override A's ownership.
    var msgId = Guid.NewGuid();
    var messages = $$"""
      [{
        "MessageId": "{{msgId}}",
        "Destination": "test-topic",
        "MessageType": "TestMessage",
        "EnvelopeType": "MessageEnvelope",
        "Envelope": {},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": false
      }]
      """;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT * FROM store_outbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      cmd.Parameters.AddWithValue("p", messages);
      cmd.Parameters.AddWithValue("inst", instanceB);
      _ = await cmd.ExecuteScalarAsync();
    }

    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT assigned_instance_id FROM wh_active_streams WHERE stream_id = @sid";
    verify.Parameters.AddWithValue("sid", streamId);
    var owner = (Guid?)await verify.ExecuteScalarAsync();
    await Assert.That(owner).IsEqualTo(instanceA)
      .Because("ON CONFLICT DO NOTHING must preserve the existing owner; B's store should not steal ownership");
  }

  [Test]
  public async Task StoreInboxMessages_NewStream_UpsertsActiveStreamsRowAsync() {
    // Mirror of outbox UPSERT for inbox.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceA = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var msgId = Guid.NewGuid();
    await _registerInstanceAsync(conn, instanceA, "test-svc", "host-A");

    var messages = $$"""
      [{
        "MessageId": "{{msgId}}",
        "HandlerName": "TestHandler",
        "MessageType": "TestMessage",
        "Envelope": {},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": false
      }]
      """;

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT * FROM store_inbox_messages(@p::jsonb, @inst, NULL, NOW(), 4)";
      cmd.Parameters.AddWithValue("p", messages);
      cmd.Parameters.AddWithValue("inst", instanceA);
      _ = await cmd.ExecuteScalarAsync();
    }

    // Inbox stores without a lease (NULL lease_expiry) — claim_orphaned_inbox will
    // pick it up next tick. But ownership pinning still applies: the storing instance
    // becomes the owner so claims preferentially route back to it.
    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT assigned_instance_id FROM wh_active_streams WHERE stream_id = @sid";
    verify.Parameters.AddWithValue("sid", streamId);
    var owner = (Guid?)await verify.ExecuteScalarAsync();
    await Assert.That(owner).IsEqualTo(instanceA)
      .Because("store_inbox_messages must populate wh_active_streams pinned to the storing instance");
  }

  // ----- cleanup_completed_streams(UUID[]) -----

  [Test]
  public async Task CleanupCompletedStreams_NoPendingWork_RemovesActiveStreamsRowAsync() {
    // Streams with no pending outbox / inbox / perspective_events get evicted from
    // wh_active_streams so the next event for that stream rebinds via UPSERT.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceA = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _registerInstanceAsync(conn, instanceA, "test-svc", "host-A");
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at)
                          VALUES (@sid, 0, @inst, NOW())";
      cmd.Parameters.AddWithValue("sid", streamId);
      cmd.Parameters.AddWithValue("inst", instanceA);
      await cmd.ExecuteNonQueryAsync();
    }

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT cleanup_completed_streams(@ids)";
      cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { streamId } });
      _ = await cmd.ExecuteScalarAsync();
    }

    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_active_streams WHERE stream_id = @sid";
    verify.Parameters.AddWithValue("sid", streamId);
    var remaining = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(remaining).IsEqualTo(0L);
  }

  [Test]
  public async Task CleanupCompletedStreams_PendingPerspectiveEvent_KeepsActiveStreamsRowAsync() {
    // Streams with pending wh_perspective_events MUST NOT be evicted — the drainer is
    // not done with them. Eviction would un-pin ownership mid-drain.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceA = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventWorkId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    await _registerInstanceAsync(conn, instanceA, "test-svc", "host-A");
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at)
                          VALUES (@sid, 0, @inst, NOW())";
      cmd.Parameters.AddWithValue("sid", streamId);
      cmd.Parameters.AddWithValue("inst", instanceA);
      await cmd.ExecuteNonQueryAsync();
    }
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"INSERT INTO wh_perspective_events
                          (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
                          VALUES (@work, @sid, 'TestPerspective', @eid, 0, 0, NOW())";
      cmd.Parameters.AddWithValue("work", eventWorkId);
      cmd.Parameters.AddWithValue("sid", streamId);
      cmd.Parameters.AddWithValue("eid", eventId);
      await cmd.ExecuteNonQueryAsync();
    }

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT cleanup_completed_streams(@ids)";
      cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { streamId } });
      _ = await cmd.ExecuteScalarAsync();
    }

    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_active_streams WHERE stream_id = @sid";
    verify.Parameters.AddWithValue("sid", streamId);
    var remaining = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(remaining).IsEqualTo(1L)
      .Because("active_streams row must persist while wh_perspective_events has pending work for the stream");
  }

  // ----- register_instance_heartbeat → cleanup_stale_instances -----

  [Test]
  public async Task RegisterInstanceHeartbeat_StaleInstanceExists_ReleasesItsLeasesAsync() {
    // When the heartbeat path runs and a peer has gone past the stale cutoff,
    // cleanup_stale_instances fires opportunistically: dead instance row is deleted,
    // and any leases held by the dead instance are released so other instances can
    // claim them on the next claim_work tick.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var deadInstance = Guid.NewGuid();
    var liveInstance = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var msgId = Guid.NewGuid();

    // Insert a stale instance row (last_heartbeat 5 min ago, well past 30s cutoff).
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"INSERT INTO wh_service_instances
                          (instance_id, service_name, host_name, process_id, last_heartbeat_at, metadata)
                          VALUES (@id, 'test-svc', 'host-DEAD', 1, NOW() - INTERVAL '5 minutes', '{}'::jsonb)";
      cmd.Parameters.AddWithValue("id", deadInstance);
      await cmd.ExecuteNonQueryAsync();
    }

    // Insert an outbox row leased to the dead instance.
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"INSERT INTO wh_outbox
                          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, instance_id, lease_expiry, stream_id, partition_number)
                          VALUES (@msg, 'topic', 'T', '{}', '{}', 1, 0, NOW(), @inst, NOW() + INTERVAL '5 minutes', @sid, 0)";
      cmd.Parameters.AddWithValue("msg", msgId);
      cmd.Parameters.AddWithValue("inst", deadInstance);
      cmd.Parameters.AddWithValue("sid", streamId);
      await cmd.ExecuteNonQueryAsync();
    }

    // Live instance heartbeats — should detect the stale peer and clean it up.
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT record_heartbeat(@id, 'test-svc', 'host-LIVE', 2, '{}'::jsonb)";
      cmd.Parameters.AddWithValue("id", liveInstance);
      _ = await cmd.ExecuteScalarAsync();
    }

    // Stale instance row should be gone.
    await using (var v = conn.CreateCommand()) {
      v.CommandText = "SELECT count(*) FROM wh_service_instances WHERE instance_id = @id";
      v.Parameters.AddWithValue("id", deadInstance);
      var count = (long)(await v.ExecuteScalarAsync())!;
      await Assert.That(count).IsEqualTo(0L)
        .Because("heartbeat must clean up the stale instance row when it sees one past the cutoff");
    }

    // Outbox lease should be released (instance_id NULL'd).
    await using (var v = conn.CreateCommand()) {
      v.CommandText = "SELECT instance_id FROM wh_outbox WHERE message_id = @msg";
      v.Parameters.AddWithValue("msg", msgId);
      var owner = await v.ExecuteScalarAsync();
      await Assert.That(owner).IsEqualTo(DBNull.Value)
        .Because("outbox lease held by the dead instance must be released so live instances can claim");
    }
  }

  // ----- helpers -----

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId, string serviceName, string hostName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT record_heartbeat(@id, @svc, @host, 1, '{}'::jsonb)";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("svc", serviceName);
    cmd.Parameters.AddWithValue("host", hostName);
    _ = await cmd.ExecuteScalarAsync();
  }
}
