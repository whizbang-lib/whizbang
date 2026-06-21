using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// RED confirmation that <c>get_stream_events</c> lets a second instance claim a stream's events
/// while the stream is owned by a different, <b>live</b> instance — the cross-pod concurrency that
/// enables the lost-update stranding (production saga <c>019ee73d</c>, 2026-06-20).
///
/// <para>The per-item-stream design assumes single-writer per stream: <c>wh_active_streams</c> pins
/// a stream to one instance, and that owner processes all of its events sequentially. But the actual
/// row claim in <c>get_stream_events</c> is purely row-lease based
/// (<c>instance_id IS NULL OR lease_expiry &lt; now</c>) and does <b>not consult stream ownership or
/// owner liveness</b>. So when the owning instance is merely slow/throttled and a row's lease lapses
/// (e.g. under the stamper/ASB backpressure during a 350-item import), a <i>different live</i>
/// instance re-claims that row and applies it concurrently — two writers on one stream.</para>
///
/// <para>This is RED until the row claim is gated by stream ownership: a row whose stream is owned by
/// a live instance must not be re-claimed by another instance on lease-expiry alone (only on owner
/// death / clean handoff).</para>
/// </summary>
public class GetStreamEventsCrossPodOwnershipTests : EFCoreTestBase {

  [Test]
  public async Task LiveOwnersExpiredLeaseRow_NotStolenByAnotherInstance() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var ownerA = (Guid)TrackedGuid.NewMedo();
    var instanceB = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";

    // A and B are both alive (recent heartbeat).
    await _registerInstanceAsync(conn, ownerA);
    await _registerInstanceAsync(conn, instanceB);

    // The stream is pinned to A with a VALID stream lease — A is the unambiguous live owner.
    await _pinStreamAsync(conn, streamId, ownerA, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5));

    // A stamped event + its perspective row, leased to A but with an EXPIRED row lease (A is slow /
    // throttled — its row lease lapsed even though A is alive and owns the stream).
    await _insertStampedEventAsync(conn, eventId, streamId);
    await _insertPerspectiveEventAsync(conn, workId, streamId, perspectiveName, eventId,
      instanceId: ownerA, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-1));

    // B drains the stream. It MUST NOT take A's row — A is the live owner.
    var rows = await _getStreamEventsAsync(conn, instanceB, [streamId]);

    await Assert.That(rows).IsEqualTo(0)
      .Because("a row whose stream is owned by a live instance (A) must not be re-claimed by another "
        + "instance (B) on row-lease expiry alone — concurrent apply on one stream is the cross-pod "
        + "lost-update that stranded production saga 019ee73d");

    var leasedTo = await _getRowInstanceAsync(conn, workId);
    await Assert.That(leasedTo).IsEqualTo(ownerA)
      .Because("the row must remain leased to its live owner A, not be poached by B");
  }

  // ── helpers ──

  private static async Task<int> _getStreamEventsAsync(NpgsqlConnection conn, Guid instanceId, Guid[] streamIds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM get_stream_events(@inst, @streams, NOW(), 300)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.Add(new NpgsqlParameter("streams", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = streamIds });
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<Guid?> _getRowInstanceAsync(NpgsqlConnection conn, Guid workId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id FROM wh_perspective_events WHERE event_work_id = @id";
    cmd.Parameters.AddWithValue("id", workId);
    var result = await cmd.ExecuteScalarAsync();
    return result is null or DBNull ? null : (Guid)result;
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _pinStreamAsync(NpgsqlConnection conn, Guid streamId, Guid ownerId, DateTimeOffset leaseExpiry) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry, last_activity_at)
      VALUES (@stream, 0, @owner, @lease, NOW())
      ON CONFLICT (stream_id) DO UPDATE SET assigned_instance_id = EXCLUDED.assigned_instance_id, lease_expiry = EXCLUDED.lease_expiry";
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("owner", ownerId);
    cmd.Parameters.AddWithValue("lease", leaseExpiry);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _insertStampedEventAsync(NpgsqlConnection conn, Guid eventId, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, scope, version, commit_sequence)
      VALUES (@event, @stream, @stream, 'TestAggregate', 'TestEvent', '{}'::jsonb, '{}'::jsonb, NULL,
              (SELECT COALESCE(MAX(version),0)+1 FROM wh_event_store WHERE stream_id=@stream), nextval('wh_commit_seq'))";
    cmd.Parameters.AddWithValue("event", eventId);
    cmd.Parameters.AddWithValue("stream", streamId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _insertPerspectiveEventAsync(
      NpgsqlConnection conn, Guid workId, Guid streamId, string perspectiveName, Guid eventId,
      Guid instanceId, DateTimeOffset leaseExpiry) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry, partition_number, status, attempts, created_at)
      VALUES (@work, @stream, @persp, @event, @inst, @lease, 0, 0, 0, NOW())";
    cmd.Parameters.AddWithValue("work", workId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("persp", perspectiveName);
    cmd.Parameters.AddWithValue("event", eventId);
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.AddWithValue("lease", leaseExpiry);
    await cmd.ExecuteNonQueryAsync();
  }
}
