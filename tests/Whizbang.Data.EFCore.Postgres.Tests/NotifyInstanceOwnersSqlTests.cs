using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 27 step 2 — RED-first locks for <c>notify_instance_owners(p_payload, p_stream_ids)</c>.
///
/// <para>The helper resolves stream → owner from <c>wh_active_streams</c>, then emits one
/// <c>pg_notify('wh_work_i_&lt;owner&gt;', p_payload)</c> per UNIQUE owner across the stream set.
/// Deduplication on owner means a saga that writes 5 events across 5 streams owned by 2
/// instances produces exactly 2 NOTIFYs, not 5.</para>
///
/// <para><strong>Locked invariants:</strong></para>
/// <list type="bullet">
/// <item><description>Channel name format is <c>wh_work_i_&lt;assigned_instance_id&gt;</c>.</description></item>
/// <item><description>Payload is the <c>p_payload</c> argument (e.g., <c>'outbox'</c>).</description></item>
/// <item><description>One NOTIFY per unique <c>assigned_instance_id</c> in the stream set.</description></item>
/// <item><description>Streams missing from <c>wh_active_streams</c> contribute zero NOTIFYs.</description></item>
/// <item><description>Streams with NULL <c>assigned_instance_id</c> contribute zero NOTIFYs.</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class NotifyInstanceOwnersSqlTests : EFCoreTestBase {

  [Test]
  public async Task NotifyInstanceOwners_OneOwnedStream_EmitsOnOwnerChannelAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var owner = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, owner);
    await _upsertActiveStreamAsync(conn, streamId, partitionNumber: 0, owner);

    var received = await _captureNotificationsAsync(conn, new[] { owner }, async () => {
      await _callNotifyInstanceOwnersAsync(conn, "outbox", streamId);
    });

    await Assert.That(received).Count().IsEqualTo(1);
    await Assert.That(received[0].Channel).IsEqualTo($"wh_work_i_{owner}");
    await Assert.That(received[0].Payload).IsEqualTo("outbox");
  }

  [Test]
  public async Task NotifyInstanceOwners_TwoStreamsSameOwner_EmitsOnceAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var owner = (Guid)TrackedGuid.NewMedo();
    var stream1 = (Guid)TrackedGuid.NewMedo();
    var stream2 = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, owner);
    await _upsertActiveStreamAsync(conn, stream1, partitionNumber: 0, owner);
    await _upsertActiveStreamAsync(conn, stream2, partitionNumber: 0, owner);

    var received = await _captureNotificationsAsync(conn, new[] { owner }, async () => {
      await _callNotifyInstanceOwnersAsync(conn, "outbox", stream1, stream2);
    });

    await Assert.That(received).Count().IsEqualTo(1)
      .Because("multiple streams owned by the same instance must collapse to one NOTIFY");
    await Assert.That(received[0].Channel).IsEqualTo($"wh_work_i_{owner}");
  }

  [Test]
  public async Task NotifyInstanceOwners_ThreeStreamsTwoOwners_EmitsOncePerOwnerAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var ownerA = (Guid)TrackedGuid.NewMedo();
    var ownerB = (Guid)TrackedGuid.NewMedo();
    var streamA1 = (Guid)TrackedGuid.NewMedo();
    var streamA2 = (Guid)TrackedGuid.NewMedo();
    var streamB1 = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, ownerA);
    await _registerInstanceAsync(conn, ownerB);
    await _upsertActiveStreamAsync(conn, streamA1, partitionNumber: 0, ownerA);
    await _upsertActiveStreamAsync(conn, streamA2, partitionNumber: 0, ownerA);
    await _upsertActiveStreamAsync(conn, streamB1, partitionNumber: 0, ownerB);

    var received = await _captureNotificationsAsync(conn, new[] { ownerA, ownerB }, async () => {
      await _callNotifyInstanceOwnersAsync(conn, "perspective", streamA1, streamA2, streamB1);
    });

    await Assert.That(received).Count().IsEqualTo(2)
      .Because("3 streams × 2 unique owners must produce exactly 2 NOTIFYs (deduped per owner)");
    var channels = received.Select(r => r.Channel).OrderBy(c => c).ToList();
    var expected = new[] { $"wh_work_i_{ownerA}", $"wh_work_i_{ownerB}" }.OrderBy(c => c).ToList();
    await Assert.That(channels).IsEquivalentTo(expected);
  }

  [Test]
  public async Task NotifyInstanceOwners_UnknownStream_EmitsZeroNotifiesAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var owner = (Guid)TrackedGuid.NewMedo();
    var unknownStream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, owner);
    // unknownStream is NOT in wh_active_streams.

    var received = await _captureNotificationsAsync(conn, new[] { owner }, async () => {
      await _callNotifyInstanceOwnersAsync(conn, "inbox", unknownStream);
    });

    await Assert.That(received).Count().IsEqualTo(0)
      .Because("a stream missing from wh_active_streams has no known owner → no NOTIFY");
  }

  [Test]
  public async Task NotifyInstanceOwners_StreamWithNullOwner_EmitsZeroNotifiesAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var owner = (Guid)TrackedGuid.NewMedo();
    var orphanStream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, owner);
    // orphanStream exists in wh_active_streams but assigned_instance_id is NULL
    // (post-cleanup_stale_instances state — pre-slice-6-fix a consumer baseline).
    await _upsertActiveStreamAsync(conn, orphanStream, partitionNumber: 0, ownerInstanceId: null);

    var received = await _captureNotificationsAsync(conn, new[] { owner }, async () => {
      await _callNotifyInstanceOwnersAsync(conn, "outbox", orphanStream);
    });

    await Assert.That(received).Count().IsEqualTo(0)
      .Because("a stream with NULL owner has no routing target — polling backstop must catch it");
  }

  [Test]
  public async Task NotifyInstanceOwners_PayloadCarriesPurposeAsync() {
    // Locks the payload contract: the listener still uses the payload to route to
    // WorkSignalCategory (outbox / inbox / perspective). Channel routes by owner,
    // payload routes by category within the owner.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var owner = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, owner);
    await _upsertActiveStreamAsync(conn, streamId, partitionNumber: 0, owner);

    var received = await _captureNotificationsAsync(conn, new[] { owner }, async () => {
      await _callNotifyInstanceOwnersAsync(conn, "inbox", streamId);
    });

    await Assert.That(received).Count().IsEqualTo(1);
    await Assert.That(received[0].Payload).IsEqualTo("inbox");
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

  /// <summary>
  /// Captures NOTIFY messages delivered to the connection while <paramref name="emit"/> runs.
  /// LISTENs on each of <paramref name="ownersToListen"/>'s instance channels first, calls
  /// the emit closure, then issues a no-op query to flush the notification queue. Returns
  /// the list of (Channel, Payload) tuples received.
  /// </summary>
  private static async Task<List<(string Channel, string Payload)>> _captureNotificationsAsync(
      NpgsqlConnection conn,
      Guid[] ownersToListen,
      Func<Task> emit) {
    var received = new List<(string, string)>();
    NotificationEventHandler handler = (sender, args) => {
      received.Add((args.Channel, args.Payload));
    };
    conn.Notification += handler;
    try {
      foreach (var owner in ownersToListen) {
        await using var listen = conn.CreateCommand();
        listen.CommandText = $"LISTEN \"wh_work_i_{owner}\"";
        await listen.ExecuteNonQueryAsync();
      }

      await emit();

      // Force a roundtrip — NOTIFY messages buffered after the function's COMMIT are
      // dispatched to the Notification event on the next request/response cycle.
      await using var ping = conn.CreateCommand();
      ping.CommandText = "SELECT 1";
      _ = await ping.ExecuteScalarAsync();
    } finally {
      conn.Notification -= handler;
      foreach (var owner in ownersToListen) {
        await using var unlisten = conn.CreateCommand();
        unlisten.CommandText = $"UNLISTEN \"wh_work_i_{owner}\"";
        await unlisten.ExecuteNonQueryAsync();
      }
    }
    return received;
  }

  private static async Task _callNotifyInstanceOwnersAsync(
      NpgsqlConnection conn, string payload, params Guid[] streamIds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT notify_instance_owners(@payload, @ids)";
    cmd.Parameters.AddWithValue("payload", payload);
    cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) {
      Value = streamIds
    });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _upsertActiveStreamAsync(
      NpgsqlConnection conn, Guid streamId, int partitionNumber, Guid? ownerInstanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at)
      VALUES (@sid, @part, @inst, NOW())
      ON CONFLICT (stream_id) DO UPDATE
        SET assigned_instance_id = EXCLUDED.assigned_instance_id,
            last_activity_at = EXCLUDED.last_activity_at";
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("part", partitionNumber);
    cmd.Parameters.Add(new NpgsqlParameter("inst", NpgsqlDbType.Uuid) {
      Value = (object?)ownerInstanceId ?? DBNull.Value
    });
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
