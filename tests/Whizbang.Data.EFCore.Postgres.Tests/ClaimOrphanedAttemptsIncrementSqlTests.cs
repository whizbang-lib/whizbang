using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Phase H step 8 slice A/B/C — RED-first locks for the attempts-increment fix on
/// <c>claim_orphaned_inbox</c> / <c>_outbox</c> / <c>_perspective_events</c>.
/// </summary>
/// <remarks>
/// <para>
/// Pre-fix audit on a consumer application (2026-05-02): all 4023 pending wh_inbox rows on appservice-db
/// and 1832 on chatservice-db had <c>attempts = 0</c> despite being stuck for &gt; 24 h with
/// repeated lease cycles. Root cause: <c>claim_orphaned_*</c> reset <c>instance_id</c> +
/// <c>lease_expiry</c> on every re-claim but never bumped <c>attempts</c>, so a hung handler
/// looked indistinguishable from a brand-new message and there was no metric an operator
/// could query to surface the problem.
/// </para>
/// <para>
/// Locked invariant: a claim from <c>instance_id IS NULL</c> (first attempt) keeps
/// <c>attempts</c> at 0; any other re-claim path bumps <c>attempts</c> by 1.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public class ClaimOrphanedAttemptsIncrementSqlTests : EFCoreTestBase {

  // ============================================================================
  // INBOX
  // ============================================================================

  [Test]
  public async Task ClaimOrphanedInbox_FreshClaim_DoesNotIncrementAttemptsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var meId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    await _insertInboxRowAsync(conn, msgId, streamId, instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedInboxAsync(conn, meId);

    var attempts = await _readAttemptsAsync(conn, "wh_inbox", "message_id", msgId);
    await Assert.That(attempts).IsEqualTo(0)
      .Because("first claim from instance_id IS NULL is not a re-attempt; attempts stays at 0");
  }

  [Test]
  public async Task ClaimOrphanedInbox_LeaseExpiredOnOtherInstance_IncrementsAttemptsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var meId = (Guid)TrackedGuid.NewMedo();
    var deadInstance = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    // dead instance: registered but heartbeat past stale cutoff so claim_orphaned can take over
    await _registerInstanceAsync(conn, deadInstance, lastHeartbeatOffset: TimeSpan.FromHours(-1));
    await _insertInboxRowAsync(conn, msgId, streamId, instanceId: deadInstance,
      leaseExpiry: DateTimeOffset.UtcNow.AddSeconds(-10), attempts: 0);

    await _callClaimOrphanedInboxAsync(conn, meId);

    var attempts = await _readAttemptsAsync(conn, "wh_inbox", "message_id", msgId);
    await Assert.That(attempts).IsEqualTo(1)
      .Because("re-claim from a dead instance with expired lease is the second attempt — bump attempts");
  }

  [Test]
  public async Task ClaimOrphanedInbox_LeaseExpiredOnSelf_IncrementsAttemptsAsync() {
    // Restart-like scenario: the same instance had the lease, never renewed, lease expired.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var meId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    await _insertInboxRowAsync(conn, msgId, streamId, instanceId: meId,
      leaseExpiry: DateTimeOffset.UtcNow.AddSeconds(-10), attempts: 0);

    await _callClaimOrphanedInboxAsync(conn, meId);

    var attempts = await _readAttemptsAsync(conn, "wh_inbox", "message_id", msgId);
    await Assert.That(attempts).IsEqualTo(1)
      .Because("self re-claim after lease expiry still indicates a prior attempt that didn't finish");
  }

  [Test]
  public async Task ClaimOrphanedInbox_RepeatedReclaims_AccumulateAttemptsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var meId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    // start with attempts=2 (already had 2 prior attempts)
    await _insertInboxRowAsync(conn, msgId, streamId, instanceId: meId,
      leaseExpiry: DateTimeOffset.UtcNow.AddSeconds(-10), attempts: 2);

    await _callClaimOrphanedInboxAsync(conn, meId);

    var attempts = await _readAttemptsAsync(conn, "wh_inbox", "message_id", msgId);
    await Assert.That(attempts).IsEqualTo(3)
      .Because("subsequent re-claims continue to bump from the existing attempts value");
  }

  // ============================================================================
  // OUTBOX
  // ============================================================================

  [Test]
  public async Task ClaimOrphanedOutbox_FreshClaim_DoesNotIncrementAttemptsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var meId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    await _insertOutboxRowAsync(conn, msgId, streamId, instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedOutboxAsync(conn, meId);

    var attempts = await _readAttemptsAsync(conn, "wh_outbox", "message_id", msgId);
    await Assert.That(attempts).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimOrphanedOutbox_LeaseExpiredOnOtherInstance_IncrementsAttemptsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var meId = (Guid)TrackedGuid.NewMedo();
    var deadInstance = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    await _registerInstanceAsync(conn, deadInstance, lastHeartbeatOffset: TimeSpan.FromHours(-1));
    await _insertOutboxRowAsync(conn, msgId, streamId, instanceId: deadInstance,
      leaseExpiry: DateTimeOffset.UtcNow.AddSeconds(-10), attempts: 0);

    await _callClaimOrphanedOutboxAsync(conn, meId);

    var attempts = await _readAttemptsAsync(conn, "wh_outbox", "message_id", msgId);
    await Assert.That(attempts).IsEqualTo(1);
  }

  // ============================================================================
  // PERSPECTIVE_EVENTS
  // ============================================================================

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_FreshClaim_DoesNotIncrementAttemptsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var meId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    await _insertPerspectiveEventAsync(conn, workId, streamId, "Projection.Test", eventId,
      instanceId: null, leaseExpiry: null, attempts: 0);

    await _callClaimOrphanedPerspectiveEventsAsync(conn, meId);

    var attempts = await _readAttemptsAsync(conn, "wh_perspective_events", "event_work_id", workId);
    await Assert.That(attempts).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_LeaseExpiredOnOtherInstance_IncrementsAttemptsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var meId = (Guid)TrackedGuid.NewMedo();
    var deadInstance = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    await _registerInstanceAsync(conn, deadInstance, lastHeartbeatOffset: TimeSpan.FromHours(-1));
    await _insertPerspectiveEventAsync(conn, workId, streamId, "Projection.Test", eventId,
      instanceId: deadInstance, leaseExpiry: DateTimeOffset.UtcNow.AddSeconds(-10), attempts: 0);

    await _callClaimOrphanedPerspectiveEventsAsync(conn, meId);

    var attempts = await _readAttemptsAsync(conn, "wh_perspective_events", "event_work_id", workId);
    await Assert.That(attempts).IsEqualTo(1);
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static readonly DateTimeOffset _staleCutoff =
    DateTimeOffset.UtcNow.AddMinutes(-2);  // matches typical stale threshold

  private static async Task _callClaimOrphanedInboxAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_orphaned_inbox(@inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000, @stale)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.Add(new NpgsqlParameter("stale", NpgsqlDbType.TimestampTz) { Value = _staleCutoff });
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

  private static async Task _callClaimOrphanedPerspectiveEventsAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_orphaned_perspective_events(@inst, NOW() + INTERVAL '5 minutes', NOW(), 500, 0, 1)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* drain */ }
  }

  private static async Task<int> _readAttemptsAsync(NpgsqlConnection conn, string table, string idCol, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT attempts FROM {table} WHERE {idCol} = @id";
    cmd.Parameters.AddWithValue("id", id);
    var result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _insertInboxRowAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId,
      Guid? instanceId, DateTimeOffset? leaseExpiry, int attempts) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         instance_id, lease_expiry, stream_id, partition_number)
      VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, @att, NOW(),
              @inst, @lease, @stream, 0)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("att", attempts);
    ins.Parameters.Add(new NpgsqlParameter("inst", NpgsqlDbType.Uuid) { Value = (object?)instanceId ?? DBNull.Value });
    ins.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) { Value = (object?)leaseExpiry ?? DBNull.Value });
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertOutboxRowAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId,
      Guid? instanceId, DateTimeOffset? leaseExpiry, int attempts) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, instance_id, lease_expiry)
      VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{}', '{}', 1, @att,
              NOW(), @stream, 0, @inst, @lease)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("att", attempts);
    ins.Parameters.Add(new NpgsqlParameter("inst", NpgsqlDbType.Uuid) { Value = (object?)instanceId ?? DBNull.Value });
    ins.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) { Value = (object?)leaseExpiry ?? DBNull.Value });
    await ins.ExecuteNonQueryAsync();
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
