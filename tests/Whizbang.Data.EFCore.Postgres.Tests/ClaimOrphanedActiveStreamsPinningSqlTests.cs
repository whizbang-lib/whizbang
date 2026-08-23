using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 6 fix — re-pinning regression locks for <c>claim_orphaned_perspective_events</c>,
/// <c>claim_orphaned_outbox</c>, and <c>claim_orphaned_inbox</c>.
///
/// <para>
/// Original slice 6 design said <c>wh_active_streams.assigned_instance_id</c> is the
/// authoritative ownership ledger, with producer-side pinning at first event store. A
/// production run revealed that after every instance death/redeploy, ownership rows persist
/// with <c>assigned_instance_id = NULL</c> (because <c>cleanup_stale_instances</c> /
/// <c>deregister_instance</c> nullify them) and never recover — <c>claim_orphaned_*</c>
/// claims via the UNOWNED partition-modulo path but does NOT re-pin. Result: every row
/// on the consumer's appservice-db sat with NULL ownership after that run completed.
/// Slice 27's routed NOTIFY infrastructure depends on this column being populated to
/// deliver actual benefit.
/// </para>
///
/// <para>
/// <strong>Locked invariants:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>A successful claim via UNOWNED path UPSERTs <c>wh_active_streams</c>:
/// missing row → INSERT with caller as owner; existing row with NULL owner → UPDATE to
/// caller; existing row with dead-instance owner → UPDATE to caller; existing row with live
/// different owner → no-op (claim wouldn't happen anyway).</description></item>
/// <item><description>A successful claim via OWNER path refreshes <c>last_activity_at</c>
/// but never changes <c>assigned_instance_id</c> (the owner-path semantic).</description></item>
/// <item><description><c>last_activity_at</c> updates on every successful claim so
/// <c>cleanup_completed_streams</c> doesn't evict actively-claimed streams.</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/stream-ownership</docs>
[Category("Shard3")]
public class ClaimOrphanedActiveStreamsPinningSqlTests : EFCoreTestBase {

  // ============================================================================
  // PERSPECTIVE EVENTS
  // ============================================================================

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_NoActiveStreamsRow_CreatesAndPinsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    // Insert perspective-event row but NO wh_active_streams entry (mirrors a consumer's
    // strategy-flush path that calls store_outbox_messages with NULL p_instance_id).
    await _insertPerspectiveEventAsync(conn, workId, streamId, "Projection.Test", eventId,
      instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedPerspectiveEventsAsync(conn, meId);

    var owner = await _readActiveStreamsOwnerAsync(conn, streamId);
    await Assert.That(owner).IsNotNull()
      .Because("claim_orphaned_perspective_events must create the wh_active_streams row when none exists");
    await Assert.That(owner!.Value).IsEqualTo(meId)
      .Because("the claiming instance must become the pinned owner");
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_RowWithNullOwner_RebindsToCallerAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    // The "post-restart" scenario: row exists, but cleanup_stale_instances has nulled
    // assigned_instance_id. The stale-ownership state observed in production.
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0, ownerInstanceId: null);
    await _insertPerspectiveEventAsync(conn, workId, streamId, "Projection.Test", eventId,
      instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedPerspectiveEventsAsync(conn, meId);

    var owner = await _readActiveStreamsOwnerAsync(conn, streamId);
    await Assert.That(owner).IsEqualTo(meId)
      .Because("a NULL-owner row must rebind to the claiming instance");
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_RowOwnedByDeadInstance_RebindsToCallerAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var deadId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    // claim_orphaned_perspective_events treats "dead" as "no row in wh_service_instances"
    // (cleanup_stale_instances has DELETEd the row). Don't register deadId — its lease
    // is on wh_active_streams but no live or zombie row in wh_service_instances.
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0, ownerInstanceId: deadId);
    await _insertPerspectiveEventAsync(conn, workId, streamId, "Projection.Test", eventId,
      instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedPerspectiveEventsAsync(conn, meId);

    var owner = await _readActiveStreamsOwnerAsync(conn, streamId);
    await Assert.That(owner).IsEqualTo(meId)
      .Because("ownership of a dead instance must transfer to the live claimant");
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_RowOwnedByLiveDifferentInstance_PreservesOwnershipAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var liveOtherId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    // liveOtherId is alive and currently owns the stream.
    await _registerInstanceAsync(conn, liveOtherId);
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0, ownerInstanceId: liveOtherId);
    // perspective-event row with NULL instance_id — claim would normally try to pick it up.
    await _insertPerspectiveEventAsync(conn, workId, streamId, "Projection.Test", eventId,
      instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedPerspectiveEventsAsync(conn, meId);

    var owner = await _readActiveStreamsOwnerAsync(conn, streamId);
    await Assert.That(owner).IsEqualTo(liveOtherId)
      .Because("a live different owner must not be stolen — claim filter excluded the row");
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_OwnerPathClaim_RefreshesLastActivityAtAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    // I already own the stream. last_activity_at is stale (1 hour ago).
    var staleActivity = DateTimeOffset.UtcNow.AddHours(-1);
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0,
      ownerInstanceId: meId, lastActivityAt: staleActivity);
    await _insertPerspectiveEventAsync(conn, workId, streamId, "Projection.Test", eventId,
      instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedPerspectiveEventsAsync(conn, meId);

    var (ownerAfter, lastActivityAfter) = await _readActiveStreamsRowAsync(conn, streamId);
    await Assert.That(ownerAfter).IsEqualTo(meId)
      .Because("OWNER-path claim must not change assigned_instance_id");
    await Assert.That(lastActivityAfter).IsGreaterThan(staleActivity)
      .Because("a successful claim must refresh last_activity_at so cleanup_completed_streams treats the stream as active");
  }

  // ============================================================================
  // OUTBOX
  // ============================================================================

  [Test]
  public async Task ClaimOrphanedOutbox_NoActiveStreamsRow_CreatesAndPinsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    await _insertOutboxRowAsync(conn, msgId, streamId, partitionNumber: 0,
      instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedOutboxAsync(conn, meId);

    var owner = await _readActiveStreamsOwnerAsync(conn, streamId);
    await Assert.That(owner).IsEqualTo(meId);
  }

  [Test]
  public async Task ClaimOrphanedOutbox_RowWithNullOwner_RebindsToCallerAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0, ownerInstanceId: null);
    await _insertOutboxRowAsync(conn, msgId, streamId, partitionNumber: 0,
      instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedOutboxAsync(conn, meId);

    var owner = await _readActiveStreamsOwnerAsync(conn, streamId);
    await Assert.That(owner).IsEqualTo(meId);
  }

  // ============================================================================
  // INBOX
  // ============================================================================

  [Test]
  public async Task ClaimOrphanedInbox_NoActiveStreamsRow_CreatesAndPinsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    await _insertInboxRowAsync(conn, msgId, streamId, partitionNumber: 0,
      instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedInboxAsync(conn, meId);

    var owner = await _readActiveStreamsOwnerAsync(conn, streamId);
    await Assert.That(owner).IsEqualTo(meId);
  }

  [Test]
  public async Task ClaimOrphanedInbox_RowWithNullOwner_RebindsToCallerAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0, ownerInstanceId: null);
    await _insertInboxRowAsync(conn, msgId, streamId, partitionNumber: 0,
      instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedInboxAsync(conn, meId);

    var owner = await _readActiveStreamsOwnerAsync(conn, streamId);
    await Assert.That(owner).IsEqualTo(meId);
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static readonly DateTimeOffset _staleCutoff =
    DateTimeOffset.UtcNow.AddMinutes(-2);

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _callClaimOrphanedPerspectiveEventsAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_orphaned_perspective_events(@inst, NOW() + INTERVAL '5 minutes', NOW(), 500, 0, 1)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* drain */ }
  }

  private static async Task _callClaimOrphanedOutboxAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_orphaned_outbox(@inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000, @stale)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.Add(new NpgsqlParameter("stale", NpgsqlDbType.TimestampTz) { Value = _staleCutoff });
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* drain */ }
  }

  private static async Task _callClaimOrphanedInboxAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_orphaned_inbox(@inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000, @stale)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.Add(new NpgsqlParameter("stale", NpgsqlDbType.TimestampTz) { Value = _staleCutoff });
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* drain */ }
  }

  private static async Task<Guid?> _readActiveStreamsOwnerAsync(NpgsqlConnection conn, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT assigned_instance_id FROM wh_active_streams WHERE stream_id = @sid";
    cmd.Parameters.AddWithValue("sid", streamId);
    var result = await cmd.ExecuteScalarAsync();
    return result switch {
      null => null,
      DBNull => null,
      _ => (Guid)result
    };
  }

  private static async Task<(Guid? Owner, DateTimeOffset LastActivityAt)> _readActiveStreamsRowAsync(
      NpgsqlConnection conn, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT assigned_instance_id, last_activity_at FROM wh_active_streams WHERE stream_id = @sid";
    cmd.Parameters.AddWithValue("sid", streamId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"No wh_active_streams row for {streamId}");
    }
    var owner = reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0);
    var lastActivity = reader.GetFieldValue<DateTimeOffset>(1);
    return (owner, lastActivity);
  }

  private static async Task _upsertActiveStreamRowAsync(
      NpgsqlConnection conn, Guid streamId, int partitionNumber,
      Guid? ownerInstanceId, DateTimeOffset? lastActivityAt = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at)
      VALUES (@sid, @part, @inst, @la)
      ON CONFLICT (stream_id) DO UPDATE
        SET partition_number = EXCLUDED.partition_number,
            assigned_instance_id = EXCLUDED.assigned_instance_id,
            last_activity_at = EXCLUDED.last_activity_at";
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("part", partitionNumber);
    cmd.Parameters.Add(new NpgsqlParameter("inst", NpgsqlDbType.Uuid) { Value = (object?)ownerInstanceId ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("la", NpgsqlDbType.TimestampTz) {
      Value = (object?)(lastActivityAt ?? DateTimeOffset.UtcNow) ?? DBNull.Value
    });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _insertPerspectiveEventAsync(
      NpgsqlConnection conn, Guid eventWorkId, Guid streamId, string perspectiveName, Guid eventId,
      Guid? instanceId, DateTimeOffset? leaseExpiry, int attempts) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
         partition_number, status, attempts, created_at)
      VALUES (@work, @stream, @persp, @event, @inst, @lease, 0, 0, @att, NOW())";
    ins.Parameters.AddWithValue("work", eventWorkId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("persp", perspectiveName);
    ins.Parameters.AddWithValue("event", eventId);
    ins.Parameters.AddWithValue("att", attempts);
    ins.Parameters.Add(new NpgsqlParameter("inst", NpgsqlDbType.Uuid) { Value = (object?)instanceId ?? DBNull.Value });
    ins.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) { Value = (object?)leaseExpiry ?? DBNull.Value });
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertOutboxRowAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, int partitionNumber,
      Guid? instanceId, DateTimeOffset? leaseExpiry, int attempts) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, instance_id, lease_expiry)
      VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{}', '{}', 1, @att,
              NOW(), @stream, @part, @inst, @lease)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("part", partitionNumber);
    ins.Parameters.AddWithValue("att", attempts);
    ins.Parameters.Add(new NpgsqlParameter("inst", NpgsqlDbType.Uuid) { Value = (object?)instanceId ?? DBNull.Value });
    ins.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) { Value = (object?)leaseExpiry ?? DBNull.Value });
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertInboxRowAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, int partitionNumber,
      Guid? instanceId, DateTimeOffset? leaseExpiry, int attempts) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         instance_id, lease_expiry, stream_id, partition_number)
      VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, @att, NOW(),
              @inst, @lease, @stream, @part)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("part", partitionNumber);
    ins.Parameters.AddWithValue("att", attempts);
    ins.Parameters.Add(new NpgsqlParameter("inst", NpgsqlDbType.Uuid) { Value = (object?)instanceId ?? DBNull.Value });
    ins.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) { Value = (object?)leaseExpiry ?? DBNull.Value });
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _registerInstanceAsync(
      NpgsqlConnection conn, Guid instanceId, TimeSpan? lastHeartbeatOffset = null) {
    var hb = lastHeartbeatOffset.HasValue
      ? DateTimeOffset.UtcNow + lastHeartbeatOffset.Value
      : DateTimeOffset.UtcNow;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, @hb, @hb, '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = EXCLUDED.last_heartbeat_at";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.Add(new NpgsqlParameter("hb", NpgsqlDbType.TimestampTz) { Value = hb });
    await cmd.ExecuteNonQueryAsync();
  }
}
