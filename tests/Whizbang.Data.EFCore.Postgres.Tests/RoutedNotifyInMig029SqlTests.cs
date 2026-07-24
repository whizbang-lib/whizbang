using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 27 step 3 — RED-first locks for the conversion of <c>commit_handler_result</c>
/// and <c>complete_perspective</c> from the global <c>pg_notify('wh_work', category)</c>
/// pattern to routed <c>notify_instance_owners</c> calls.
///
/// <para>Before slice 27, both SQL functions emitted on a single global <c>wh_work</c> channel
/// — every listening C# instance woke regardless of ownership. After slice 27, the NOTIFY
/// fires only on <c>wh_work_i_&lt;owner_instance_id&gt;</c> for the streams that received
/// new work / had cursors advanced.</para>
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class RoutedNotifyInMig029SqlTests : EFCoreTestBase {

  // ----- commit_handler_result -----

  [Test]
  public async Task CommitHandlerResult_NewOutbox_NotifiesOwnerOfStreamAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var owner = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, owner);
    await _upsertActiveStreamAsync(conn, streamId, partitionNumber: 0, owner);

    var msgId = (Guid)TrackedGuid.NewMedo();
    var requestJson = _commitHandlerRequest(
      instanceId: owner,
      newOutboxMessages: $$"""
        [{
          "MessageId": "{{msgId}}",
          "Destination": "topic",
          "MessageType": "TestEvent",
          "EnvelopeType": "MessageEnvelope",
          "Envelope": {},
          "Metadata": {},
          "Scope": null,
          "StreamId": "{{streamId}}",
          "IsEvent": false
        }]
        """);

    // Listen on owner's channel + global wh_work as a regression sentinel —
    // after slice 27 the global channel must NOT receive these payloads.
    var received = await _captureNotificationsAsync(conn,
      ownerChannels: [owner],
      alsoListenGlobal: true,
      emit: async () => await _commitHandlerResultAsync(conn, requestJson));

    var ownerMessages = received.Where(r => r.Channel == $"wh_work_i_{owner}").ToList();
    await Assert.That(ownerMessages).Count().IsGreaterThanOrEqualTo(1)
      .Because("the new outbox row's stream is owned by this instance → NOTIFY must land on its routed channel");
    var ownerPayloads = ownerMessages.Select(m => m.Payload).ToList();
    await Assert.That(ownerPayloads).Contains("outbox")
      .Because("the outbox-store branch must emit 'outbox' payload");

    var globalMessages = received.Where(r => r.Channel == "wh_work").ToList();
    await Assert.That(globalMessages).Count().IsEqualTo(0)
      .Because("post-slice-27, the global wh_work channel must no longer receive work signals");
  }

  [Test]
  public async Task CommitHandlerResult_NewOutbox_DoesNotNotifyNonOwnerAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var owner = (Guid)TrackedGuid.NewMedo();
    var nonOwner = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, owner);
    await _registerInstanceAsync(conn, nonOwner);
    await _upsertActiveStreamAsync(conn, streamId, partitionNumber: 0, owner);

    var msgId = (Guid)TrackedGuid.NewMedo();
    var requestJson = _commitHandlerRequest(
      instanceId: owner,
      newOutboxMessages: $$"""
        [{
          "MessageId": "{{msgId}}",
          "Destination": "topic",
          "MessageType": "TestEvent",
          "EnvelopeType": "MessageEnvelope",
          "Envelope": {},
          "Metadata": {},
          "Scope": null,
          "StreamId": "{{streamId}}",
          "IsEvent": false
        }]
        """);

    var received = await _captureNotificationsAsync(conn,
      ownerChannels: [owner, nonOwner],
      alsoListenGlobal: false,
      emit: async () => await _commitHandlerResultAsync(conn, requestJson));

    var nonOwnerMessages = received.Where(r => r.Channel == $"wh_work_i_{nonOwner}").ToList();
    await Assert.That(nonOwnerMessages).Count().IsEqualTo(0)
      .Because("a non-owner instance must not receive any work signal for this stream");
  }

  // ----- complete_perspective -----

  [Test]
  public async Task CompletePerspective_CursorAdvance_NotifiesOnlyStreamOwnerAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var owner = (Guid)TrackedGuid.NewMedo();
    var nonOwner = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, owner);
    await _registerInstanceAsync(conn, nonOwner);
    await _upsertActiveStreamAsync(conn, streamId, partitionNumber: 0, owner);

    var cursorEventId = (Guid)TrackedGuid.NewMedo();
    var cursorsJson = $$"""
      [{
        "StreamId": "{{streamId}}",
        "PerspectiveName": "Projection.Test",
        "CursorEventId": "{{cursorEventId}}",
        "Metadata": {}
      }]
      """;

    var received = await _captureNotificationsAsync(conn,
      ownerChannels: [owner, nonOwner],
      alsoListenGlobal: true,
      emit: async () => await _completePerspectiveAsync(conn, cursorsJson));

    var ownerMessages = received.Where(r => r.Channel == $"wh_work_i_{owner}").ToList();
    await Assert.That(ownerMessages).Count().IsGreaterThanOrEqualTo(1)
      .Because("cursor advance on owned stream → NOTIFY on owner's routed channel");
    await Assert.That(ownerMessages.Select(m => m.Payload)).Contains("perspective");

    var nonOwnerMessages = received.Where(r => r.Channel == $"wh_work_i_{nonOwner}").ToList();
    await Assert.That(nonOwnerMessages).Count().IsEqualTo(0);

    var globalMessages = received.Where(r => r.Channel == "wh_work").ToList();
    await Assert.That(globalMessages).Count().IsEqualTo(0);
  }

  [Test]
  public async Task CompletePerspective_UnknownStream_EmitsZeroNotifiesAsync() {
    // Locks the "polling backstop is the safety net" semantic: if wh_active_streams
    // doesn't know who owns a stream, no NOTIFY fires — but the work IS persisted and
    // polling will catch it.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var observer = (Guid)TrackedGuid.NewMedo();
    var unknownStream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, observer);

    var cursorEventId = (Guid)TrackedGuid.NewMedo();
    var cursorsJson = $$"""
      [{
        "StreamId": "{{unknownStream}}",
        "PerspectiveName": "Projection.Test",
        "CursorEventId": "{{cursorEventId}}",
        "Metadata": {}
      }]
      """;

    var received = await _captureNotificationsAsync(conn,
      ownerChannels: [observer],
      alsoListenGlobal: true,
      emit: async () => await _completePerspectiveAsync(conn, cursorsJson));

    await Assert.That(received).Count().IsEqualTo(0)
      .Because("stream not in wh_active_streams → no NOTIFY, no global fallback (polling catches it)");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static string _commitHandlerRequest(
      Guid instanceId,
      string newOutboxMessages = "[]",
      string newInboxMessages = "[]") {
    return $$"""
      {
        "instance_id": "{{instanceId}}",
        "partition_count": 10000,
        "debug_mode": false,
        "inbox_completion": null,
        "new_outbox_messages": {{newOutboxMessages}},
        "new_inbox_messages": {{newInboxMessages}}
      }
      """;
  }

  private static async Task _commitHandlerResultAsync(NpgsqlConnection conn, string requestJson) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    cmd.Parameters.AddWithValue("req", requestJson);
    _ = await cmd.ExecuteScalarAsync();
  }

  private static async Task _completePerspectiveAsync(NpgsqlConnection conn, string cursorsJson) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT complete_perspective(@cursors::jsonb, NULL::uuid[], false)";
    cmd.Parameters.AddWithValue("cursors", cursorsJson);
    _ = await cmd.ExecuteScalarAsync();
  }

  private static async Task<List<(string Channel, string Payload)>> _captureNotificationsAsync(
      NpgsqlConnection conn,
      Guid[] ownerChannels,
      bool alsoListenGlobal,
      Func<Task> emit) {
    var received = new List<(string, string)>();
    void handler(object sender, NpgsqlNotificationEventArgs args) {
      received.Add((args.Channel, args.Payload));
    }
    conn.Notification += handler;
    try {
      foreach (var owner in ownerChannels) {
        await using var listen = conn.CreateCommand();
        listen.CommandText = $"LISTEN \"wh_work_i_{owner}\"";
        await listen.ExecuteNonQueryAsync();
      }
      if (alsoListenGlobal) {
        await using var listen = conn.CreateCommand();
        listen.CommandText = "LISTEN wh_work";
        await listen.ExecuteNonQueryAsync();
      }

      await emit();

      // Force a roundtrip to flush the NOTIFY queue to the Notification event.
      await using var ping = conn.CreateCommand();
      ping.CommandText = "SELECT 1";
      _ = await ping.ExecuteScalarAsync();
    } finally {
      conn.Notification -= handler;
      foreach (var owner in ownerChannels) {
        await using var unlisten = conn.CreateCommand();
        unlisten.CommandText = $"UNLISTEN \"wh_work_i_{owner}\"";
        await unlisten.ExecuteNonQueryAsync();
      }
      if (alsoListenGlobal) {
        await using var unlisten = conn.CreateCommand();
        unlisten.CommandText = "UNLISTEN wh_work";
        await unlisten.ExecuteNonQueryAsync();
      }
    }
    return received;
  }

  private static async Task _upsertActiveStreamAsync(
      NpgsqlConnection conn, Guid streamId, int partitionNumber, Guid ownerInstanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at)
      VALUES (@sid, @part, @inst, NOW())
      ON CONFLICT (stream_id) DO UPDATE
        SET assigned_instance_id = EXCLUDED.assigned_instance_id,
            last_activity_at = EXCLUDED.last_activity_at";
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("part", partitionNumber);
    cmd.Parameters.AddWithValue("inst", ownerInstanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = EXCLUDED.last_heartbeat_at";
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }
}
