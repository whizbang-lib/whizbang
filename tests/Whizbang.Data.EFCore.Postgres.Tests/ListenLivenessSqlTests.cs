using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 2b of zero-idle-polling — integration regression locks for the
/// LISTEN-connection-as-liveness-signal change across migration 052
/// (<c>wh_live_instances</c> view) and the additive predicates in
/// migrations 024 / 025 / 027 (<c>claim_orphaned_outbox</c>,
/// <c>claim_orphaned_inbox</c>, <c>claim_orphaned_perspective_events</c>).
///
/// <para>
/// Each test pairs an instance whose heartbeat is past the stale cutoff with
/// either (a) no side connection, exercising the previous heartbeat-only
/// semantics that should still treat the instance as dead, or (b) an open
/// side connection carrying the
/// <c>application_name='whizbang-&lt;instance_id&gt;'</c> stamp that
/// <see cref="Whizbang.Data.Postgres.Notifications.PgSharedNotifyConnection.ComputeApplicationName"/>
/// emits at runtime. Scenario (b) must treat the instance as alive even when
/// the heartbeat row is stale — that's the architectural prerequisite
/// Slice 5 of zero-idle-polling relies on to safely relax the heartbeat
/// cadence from 5 s to 30 s.
/// </para>
///
/// <para>
/// The "side connection" simulates the per-pod LISTEN connection that lives
/// inside <see cref="Whizbang.Data.Postgres.Notifications.PgSharedNotifyConnection"/>
/// for the duration of the pod's lifetime. From the perspective of
/// <c>pg_stat_activity</c> on the shared Postgres backend it's
/// indistinguishable from the real thing — any client backend connection with
/// the matching <c>application_name</c> proves the pod is alive at the TCP
/// layer regardless of how recently its in-process worker called
/// <c>register_instance_heartbeat</c>.
/// </para>
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>The <c>wh_live_instances</c> view joins
/// <c>wh_service_instances</c> against <c>pg_stat_activity</c> on the
/// <c>whizbang-&lt;instance_id&gt;</c> format and exposes <c>listen_alive</c>
/// per row.</description></item>
/// <item><description><c>claim_orphaned_outbox</c> does NOT re-claim leases
/// from an instance whose heartbeat is stale but whose LISTEN connection
/// is currently registered in <c>pg_stat_activity</c>.</description></item>
/// <item><description>Same invariant for <c>claim_orphaned_inbox</c>.</description></item>
/// <item><description><c>claim_orphaned_perspective_events</c> does NOT
/// re-claim leases from an instance whose <c>wh_service_instances</c> row
/// has been cleaned up but whose LISTEN connection is still
/// registered.</description></item>
/// <item><description>Without the side connection, the previous heartbeat-only
/// semantics still hold (existing tests in
/// <c>ClaimOrphanedActiveStreamsPinningSqlTests</c> cover this — these tests
/// add the LISTEN-aware path on top).</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer#listen-as-heartbeat</docs>
[Category("Shard1")]
public class ListenLivenessSqlTests : EFCoreTestBase {

  private static readonly DateTimeOffset _staleCutoff = DateTimeOffset.UtcNow.AddMinutes(-1);

  // ============================================================================
  // wh_live_instances VIEW
  // ============================================================================

  [Test]
  public async Task LiveInstancesView_WithoutListenConnection_ReportsListenAliveFalseAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);

    var row = await _readLiveInstanceRowAsync(conn, meId);

    await Assert.That(row).IsNotNull()
      .Because("The view exposes one row per registered instance — LEFT JOIN onto pg_stat_activity, so the instance row always appears even when no LISTEN connection exists.");
    await Assert.That(row!.Value.ListenAlive).IsFalse()
      .Because("No side connection with the matching application_name is open, so the LEFT JOIN produces NULL pg_stat_activity columns and listen_alive resolves to false.");
  }

  [Test]
  public async Task LiveInstancesView_WithListenConnection_ReportsListenAliveTrueAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);

    await using var sideConn = await _openSideConnectionAsync(meId);

    var row = await _readLiveInstanceRowAsync(conn, meId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Value.ListenAlive).IsTrue()
      .Because("Side connection with application_name='whizbang-<instance_id>' is open, so pg_stat_activity reports a backend matching the join predicate and listen_alive resolves to true.");
  }

  // ============================================================================
  // claim_orphaned_outbox
  // ============================================================================

  [Test]
  public async Task ClaimOrphanedOutbox_StaleHeartbeat_NoListenConnection_LeasesReclaimableAsync() {
    // Baseline regression lock — the pre-Slice-2b heartbeat-only semantics still
    // hold when no LISTEN side connection is present. An instance with stale
    // heartbeat MUST be treated as dead so a healthy peer can take over.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var staleOwnerId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    // staleOwnerId registered with a heartbeat older than _staleCutoff — dead.
    await _registerInstanceAsync(conn, staleOwnerId, lastHeartbeatOffset: TimeSpan.FromMinutes(-10));
    // active_streams row is FRESH (the slice-6 ownership ledger is independent of
    // heartbeat freshness), but the per-row outbox lease is EXPIRED — that's the
    // orphan condition the claim function looks for.
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0,
      ownerInstanceId: staleOwnerId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5));
    await _insertOutboxRowAsync(conn, msgId, streamId, partitionNumber: 0,
      instanceId: staleOwnerId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 0);

    await _callClaimOrphanedOutboxAsync(conn, meId);

    var newOwner = await _readOutboxInstanceIdAsync(conn, msgId);
    await Assert.That(newOwner).IsEqualTo(meId)
      .Because("Stale heartbeat + no LISTEN connection = dead by every signal; the message must be re-claimed by the live peer.");
  }

  [Test]
  public async Task ClaimOrphanedOutbox_StaleHeartbeat_WithListenConnection_LeasesNotReclaimedAsync() {
    // The Slice 2b behavior change — an instance whose heartbeat row is stale
    // but whose LISTEN connection is currently registered in pg_stat_activity
    // must be treated as alive. Its leases are NOT eligible for cross-instance
    // re-claim.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var alivePodId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    // alivePodId has stale heartbeat — under heartbeat-only semantics, dead.
    await _registerInstanceAsync(conn, alivePodId, lastHeartbeatOffset: TimeSpan.FromMinutes(-10));
    // active_streams row is fresh, outbox row's per-row lease has EXPIRED — without
    // the LISTEN-alive signal the orphan-claim would proceed (verified by the sibling
    // _NoListenConnection test).
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0,
      ownerInstanceId: alivePodId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5));
    await _insertOutboxRowAsync(conn, msgId, streamId, partitionNumber: 0,
      instanceId: alivePodId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 0);

    // Open a side connection mirroring alivePodId's real LISTEN connection.
    await using var sideConn = await _openSideConnectionAsync(alivePodId);

    await _callClaimOrphanedOutboxAsync(conn, meId);

    var ownerAfter = await _readOutboxInstanceIdAsync(conn, msgId);
    await Assert.That(ownerAfter).IsEqualTo(alivePodId)
      .Because("LISTEN-alive signal must preserve the lease — Slice 5 of zero-idle-polling relies on this so HeartbeatWorker can safely relax to 30 s cadence without weakening orphan detection.");
  }

  // ============================================================================
  // claim_orphaned_inbox
  // ============================================================================

  [Test]
  public async Task ClaimOrphanedInbox_StaleHeartbeat_NoListenConnection_LeasesReclaimableAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var staleOwnerId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    await _registerInstanceAsync(conn, staleOwnerId, lastHeartbeatOffset: TimeSpan.FromMinutes(-10));
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0,
      ownerInstanceId: staleOwnerId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5));
    await _insertInboxRowAsync(conn, msgId, streamId, partitionNumber: 0,
      instanceId: staleOwnerId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 0);

    await _callClaimOrphanedInboxAsync(conn, meId);

    var newOwner = await _readInboxInstanceIdAsync(conn, msgId);
    await Assert.That(newOwner).IsEqualTo(meId)
      .Because("Stale heartbeat + no LISTEN connection = dead; baseline behavior unchanged.");
  }

  [Test]
  public async Task ClaimOrphanedInbox_StaleHeartbeat_WithListenConnection_LeasesNotReclaimedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var alivePodId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    await _registerInstanceAsync(conn, alivePodId, lastHeartbeatOffset: TimeSpan.FromMinutes(-10));
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0,
      ownerInstanceId: alivePodId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5));
    await _insertInboxRowAsync(conn, msgId, streamId, partitionNumber: 0,
      instanceId: alivePodId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 0);

    await using var sideConn = await _openSideConnectionAsync(alivePodId);

    await _callClaimOrphanedInboxAsync(conn, meId);

    var ownerAfter = await _readInboxInstanceIdAsync(conn, msgId);
    await Assert.That(ownerAfter).IsEqualTo(alivePodId)
      .Because("LISTEN-alive signal preserves the lease — additive defensive predicate from Slice 2b.");
  }

  // ============================================================================
  // claim_orphaned_perspective_events
  // ============================================================================

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_DeadInstance_NoListenConnection_LeasesReclaimableAsync() {
    // claim_orphaned_perspective_events uses a different liveness shape: "alive
    // = row exists in wh_service_instances" (no heartbeat freshness check; the
    // row is removed by cleanup_stale_instances). Simulate that by NOT
    // registering the original owner — their row never existed (or was
    // already DELETEd by cleanup_stale_instances).
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var deadId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    // deadId never registered — its wh_service_instances row doesn't exist.
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0, ownerInstanceId: deadId);
    await _insertPerspectiveEventAsync(conn, workId, streamId, "Projection.Test", eventId,
      instanceId: deadId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 0);

    await _callClaimOrphanedPerspectiveEventsAsync(conn, meId);

    var newOwner = await _readPerspectiveEventInstanceIdAsync(conn, workId);
    await Assert.That(newOwner).IsEqualTo(meId)
      .Because("No wh_service_instances row + no LISTEN connection = dead by every signal; the lease must transfer.");
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_DeadInstanceRow_WithListenConnection_LeasesNotReclaimedAsync() {
    // The Slice 2b additive defensive predicate — even if cleanup_stale_instances
    // has DELETEd the wh_service_instances row, an active LISTEN connection
    // proves the pod is alive (race window during pod restart, or transient
    // ordering between cleanup_stale_instances and the pod's heartbeat).
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var meId = (Guid)TrackedGuid.NewMedo();
    var rebootingPodId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, meId);
    // rebootingPodId's wh_service_instances row is GONE (cleanup_stale_instances)
    // — but a LISTEN connection is currently registered, proving the pod is
    // back up and re-establishing state.
    await _upsertActiveStreamRowAsync(conn, streamId, partitionNumber: 0,
      ownerInstanceId: rebootingPodId);
    await _insertPerspectiveEventAsync(conn, workId, streamId, "Projection.Test", eventId,
      instanceId: rebootingPodId, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 0);

    await using var sideConn = await _openSideConnectionAsync(rebootingPodId);

    await _callClaimOrphanedPerspectiveEventsAsync(conn, meId);

    var ownerAfter = await _readPerspectiveEventInstanceIdAsync(conn, workId);
    await Assert.That(ownerAfter).IsEqualTo(rebootingPodId)
      .Because("LISTEN-alive defensive predicate covers the brief race window where the wh_service_instances row has been cleaned up but the pod is back up and has re-opened its LISTEN connection.");
  }

  // ============================================================================
  // Helpers — fixture interactions
  // ============================================================================

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  /// <summary>
  /// Opens a side connection carrying the
  /// <c>application_name='whizbang-&lt;instance_id&gt;'</c> stamp that
  /// PgSharedNotifyConnection emits on its real per-pod LISTEN connection.
  /// From <c>pg_stat_activity</c>'s perspective this is indistinguishable
  /// from the real thing — any client backend connection with the matching
  /// application_name proves the pod is alive at the TCP layer.
  ///
  /// Each test that calls this must wrap the result in <c>await using</c>
  /// so the connection closes (and its <c>pg_stat_activity</c> row vanishes)
  /// when the test scope ends — otherwise tests interfere with each other
  /// when run in parallel.
  /// </summary>
  private async Task<NpgsqlConnection> _openSideConnectionAsync(Guid instanceId) {
    var csBuilder = new NpgsqlConnectionStringBuilder(ConnectionString) {
      ApplicationName = $"whizbang-{instanceId:D}",
    };
    var sideConn = new NpgsqlConnection(csBuilder.ConnectionString);
    await sideConn.OpenAsync();
    // Issue a trivial query so pg_stat_activity definitely reports an
    // initialized row (some Postgres versions don't surface a backend in
    // pg_stat_activity until it executes at least one statement).
    await using var cmd = sideConn.CreateCommand();
    cmd.CommandText = "SELECT 1";
    await cmd.ExecuteScalarAsync();
    return sideConn;
  }

  private async Task<(Guid InstanceId, bool ListenAlive)?> _readLiveInstanceRowAsync(
      NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id, listen_alive FROM wh_live_instances WHERE instance_id = @id";
    cmd.Parameters.AddWithValue("id", instanceId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return null;
    }
    return (reader.GetGuid(0), reader.GetBoolean(1));
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

  private static async Task _callClaimOrphanedPerspectiveEventsAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_orphaned_perspective_events(@inst, NOW() + INTERVAL '5 minutes', NOW(), 500, 0, 1)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* drain */ }
  }

  private static async Task<Guid?> _readOutboxInstanceIdAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id FROM wh_outbox WHERE message_id = @msg";
    cmd.Parameters.AddWithValue("msg", messageId);
    var result = await cmd.ExecuteScalarAsync();
    return result switch {
      null => null,
      DBNull => null,
      _ => (Guid)result,
    };
  }

  private static async Task<Guid?> _readInboxInstanceIdAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id FROM wh_inbox WHERE message_id = @msg";
    cmd.Parameters.AddWithValue("msg", messageId);
    var result = await cmd.ExecuteScalarAsync();
    return result switch {
      null => null,
      DBNull => null,
      _ => (Guid)result,
    };
  }

  private static async Task<Guid?> _readPerspectiveEventInstanceIdAsync(NpgsqlConnection conn, Guid workId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id FROM wh_perspective_events WHERE event_work_id = @id";
    cmd.Parameters.AddWithValue("id", workId);
    var result = await cmd.ExecuteScalarAsync();
    return result switch {
      null => null,
      DBNull => null,
      _ => (Guid)result,
    };
  }

  private static async Task _upsertActiveStreamRowAsync(
      NpgsqlConnection conn, Guid streamId, int partitionNumber,
      Guid? ownerInstanceId, DateTimeOffset? lastActivityAt = null,
      DateTimeOffset? leaseExpiry = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
      VALUES (@sid, @part, @inst, @la, @lease)
      ON CONFLICT (stream_id) DO UPDATE
        SET partition_number = EXCLUDED.partition_number,
            assigned_instance_id = EXCLUDED.assigned_instance_id,
            last_activity_at = EXCLUDED.last_activity_at,
            lease_expiry = EXCLUDED.lease_expiry";
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("part", partitionNumber);
    cmd.Parameters.Add(new NpgsqlParameter("inst", NpgsqlDbType.Uuid) { Value = (object?)ownerInstanceId ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("la", NpgsqlDbType.TimestampTz) {
      Value = (object?)(lastActivityAt ?? DateTimeOffset.UtcNow) ?? DBNull.Value,
    });
    cmd.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) {
      Value = (object?)(leaseExpiry ?? DateTimeOffset.UtcNow.AddMinutes(5)) ?? DBNull.Value,
    });
    await cmd.ExecuteNonQueryAsync();
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
