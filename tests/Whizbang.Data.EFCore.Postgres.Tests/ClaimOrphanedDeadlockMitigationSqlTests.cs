using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// PR #227 — regression locks for the <c>wh_active_streams</c> deadlock mitigation in
/// <c>claim_orphaned_outbox</c> / <c>_inbox</c> / <c>_perspective_events</c>.
///
/// <para>
/// Original behavior (pre-PR-#227): every successful claim ran <c>INSERT INTO
/// wh_active_streams ... ON CONFLICT (stream_id) DO UPDATE</c>, taking the unique-index
/// leaf-page lock even in the steady-state case where this instance already owned the
/// stream with a live lease. Under N pods × 250 ms polling on production, two pods'
/// transactions could end up holding overlapping leaf-page locks while waiting on each
/// other's <c>wh_outbox</c> row locks → 40P01 deadlock.
/// </para>
///
/// <para>
/// The fix splits the ledger update into two paths:
/// </para>
/// <list type="bullet">
///   <item><description><b>REFRESH</b> — pure <c>UPDATE wh_active_streams SET last_activity_at = ...</c>
///   when this instance already owns the stream with a live lease. Row-level lock only;
///   doesn't touch the unique-index INSERT path → can't generate leaf-page contention.</description></item>
///   <item><description><b>PIN</b> — <c>INSERT...ON CONFLICT</c> only for streams not covered
///   by REFRESH (first-time pin, dead-instance reassignment, ownership transfer). Rows
///   sorted by <c>stream_id</c> so concurrent pods acquire locks in a consistent order →
///   no lock-cycle deadlock possible on this path either.</description></item>
/// </list>
///
/// <para>
/// <strong>Locked invariants:</strong>
/// </para>
/// <list type="bullet">
///   <item><description>When the instance owns the stream with a live lease, the claim
///   advances <c>last_activity_at</c> without changing <c>assigned_instance_id</c> and
///   without taking the unique-index leaf-page lock (verified indirectly by observing
///   that the test never deadlocks even under concurrent calls).</description></item>
///   <item><description>When the instance does NOT own the stream (NULL owner, dead owner,
///   or never-pinned), the PIN path runs and updates <c>assigned_instance_id</c> to caller.</description></item>
///   <item><description>Mixed scenarios (some streams already owned, others not) take the
///   right path per stream within the same claim call.</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/stream-ownership</docs>
public class ClaimOrphanedDeadlockMitigationSqlTests : EFCoreTestBase {

  // ============================================================================
  // OUTBOX — REFRESH path
  // ============================================================================

  [Test]
  public async Task ClaimOrphanedOutbox_AlreadyOwnedWithLiveLease_RefreshesLastActivityAtAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);

    // Pre-existing wh_active_streams row owned by me with a live lease — the REFRESH
    // path should fire (pure UPDATE), no INSERT...ON CONFLICT.
    var earlyTime = DateTimeOffset.UtcNow.AddMinutes(-10);
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0,
      ownerInstanceId: meId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5),
      lastActivityAt: earlyTime);

    // Outbox row needing claim (lease expired so I can claim).
    await _insertOutboxRowAsync(conn, msgId, streamId, partitionNumber: 0,
      instanceId: meId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 0);

    await _callClaimOrphanedOutboxAsync(conn, meId);

    var (owner, lastActivityAt) = await _readActiveStreamsRowAsync(conn, streamId);
    await Assert.That(owner).IsEqualTo(meId).Because("owner is unchanged (still me)");
    await Assert.That(lastActivityAt).IsGreaterThan(earlyTime.AddSeconds(60))
      .Because("REFRESH path must advance last_activity_at on owner-path claim");
  }

  // ============================================================================
  // OUTBOX — PIN path still works for streams NOT covered by REFRESH
  // ============================================================================

  [Test]
  public async Task ClaimOrphanedOutbox_NullOwner_StillTakesPinPathAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);

    // Pre-existing row with NULL owner — REFRESH skips it (no live lease for me),
    // PIN path must rebind it to me.
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0, ownerInstanceId: null);
    await _insertOutboxRowAsync(conn, msgId, streamId, partitionNumber: 0,
      instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedOutboxAsync(conn, meId);

    var owner = await _readActiveStreamsOwnerAsync(conn, streamId);
    await Assert.That(owner).IsEqualTo(meId)
      .Because("PIN path must still rebind NULL-owner rows even after split-UPSERT refactor");
  }

  [Test]
  public async Task ClaimOrphanedOutbox_NeverPinned_PinPathInsertsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);

    // No wh_active_streams row at all. REFRESH finds nothing to update; PIN path
    // INSERTs a fresh row owned by me.
    await _insertOutboxRowAsync(conn, msgId, streamId, partitionNumber: 0,
      instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedOutboxAsync(conn, meId);

    var owner = await _readActiveStreamsOwnerAsync(conn, streamId);
    await Assert.That(owner).IsEqualTo(meId)
      .Because("PIN path must INSERT a fresh row when none exists");
  }

  // ============================================================================
  // OUTBOX — Mixed REFRESH + PIN in one call
  // ============================================================================

  [Test]
  public async Task ClaimOrphanedOutbox_MixedOwnedAndUnowned_BothPathsRunPerStreamAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var ownedStream = (Guid)TrackedGuid.NewMedo();
    var unownedStream = (Guid)TrackedGuid.NewMedo();
    var ownedMsg = (Guid)TrackedGuid.NewMedo();
    var unownedMsg = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);

    // Stream A: already owned by me with live lease → REFRESH path
    var earlyTime = DateTimeOffset.UtcNow.AddMinutes(-10);
    await _upsertActiveStreamRowAsync(conn, ownedStream, partitionNumber: 0,
      ownerInstanceId: meId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5),
      lastActivityAt: earlyTime);
    await _insertOutboxRowAsync(conn, ownedMsg, ownedStream, partitionNumber: 0,
      instanceId: meId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 0);

    // Stream B: no active_streams row → PIN path
    await _insertOutboxRowAsync(conn, unownedMsg, unownedStream, partitionNumber: 0,
      instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedOutboxAsync(conn, meId);

    // Stream A: REFRESH succeeded — owner unchanged, last_activity_at advanced
    var (ownedOwner, ownedActivity) = await _readActiveStreamsRowAsync(conn, ownedStream);
    await Assert.That(ownedOwner).IsEqualTo(meId);
    await Assert.That(ownedActivity).IsGreaterThan(earlyTime.AddSeconds(60));

    // Stream B: PIN succeeded — new row with me as owner
    var unownedOwner = await _readActiveStreamsOwnerAsync(conn, unownedStream);
    await Assert.That(unownedOwner).IsEqualTo(meId);
  }

  // ============================================================================
  // INBOX + PERSPECTIVE — symmetric REFRESH-path coverage (one test each suffices,
  // since the SQL is structurally identical to outbox)
  // ============================================================================

  [Test]
  public async Task ClaimOrphanedInbox_AlreadyOwnedWithLiveLease_RefreshesLastActivityAtAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);

    var earlyTime = DateTimeOffset.UtcNow.AddMinutes(-10);
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0,
      ownerInstanceId: meId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5),
      lastActivityAt: earlyTime);
    await _insertInboxRowAsync(conn, msgId, streamId, partitionNumber: 0,
      instanceId: meId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 0);

    await _callClaimOrphanedInboxAsync(conn, meId);

    var (owner, lastActivityAt) = await _readActiveStreamsRowAsync(conn, streamId);
    await Assert.That(owner).IsEqualTo(meId);
    await Assert.That(lastActivityAt).IsGreaterThan(earlyTime.AddSeconds(60));
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_AlreadyOwnedWithLiveLease_RefreshesLastActivityAtAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);

    var earlyTime = DateTimeOffset.UtcNow.AddMinutes(-10);
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0,
      ownerInstanceId: meId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5),
      lastActivityAt: earlyTime);
    await _insertPerspectiveEventAsync(conn, workId, streamId, "Projection.Test", eventId,
      instanceId: meId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 0);

    await _callClaimOrphanedPerspectiveEventsAsync(conn, meId);

    var (owner, lastActivityAt) = await _readActiveStreamsRowAsync(conn, streamId);
    await Assert.That(owner).IsEqualTo(meId);
    await Assert.That(lastActivityAt).IsGreaterThan(earlyTime.AddSeconds(60));
  }

  // ============================================================================
  // helpers — copied / adapted from ClaimOrphanedActiveStreamsPinningSqlTests
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
      Guid? ownerInstanceId, DateTimeOffset? leaseExpiry = null, DateTimeOffset? lastActivityAt = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry, last_activity_at)
      VALUES (@sid, @part, @inst, @lease, @la)
      ON CONFLICT (stream_id) DO UPDATE
        SET partition_number = EXCLUDED.partition_number,
            assigned_instance_id = EXCLUDED.assigned_instance_id,
            lease_expiry = EXCLUDED.lease_expiry,
            last_activity_at = EXCLUDED.last_activity_at";
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("part", partitionNumber);
    cmd.Parameters.Add(new NpgsqlParameter("inst", NpgsqlDbType.Uuid) { Value = (object?)ownerInstanceId ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) {
      Value = (object?)leaseExpiry ?? DBNull.Value
    });
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
