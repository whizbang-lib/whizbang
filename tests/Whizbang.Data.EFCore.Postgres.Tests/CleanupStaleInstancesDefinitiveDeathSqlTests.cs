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
/// v0.687 — definitive-dead cutoff bypass for the v0.681 alive-lock guard in
/// <c>cleanup_stale_instances</c> (migration 011).
///
/// <para>Background — a production forensic investigation. A rolling-restart event left 1,381
/// inbox rows held by a dead pod's instance_id with lease_expiry in the future.
/// <c>cleanup_stale_instances</c> would normally release these leases as part of its
/// dead-instance removal, but the v0.681 alive-lock guard (added so the adaptive
/// heartbeat cadence wouldn't false-positive a live instance with a stale heartbeat)
/// also blocks deletion when the dead pod's session-level advisory lock is still held
/// in <c>pg_locks</c>. For OOMKilled pods on a misbehaving TCP path (half-open
/// connection, no FIN sent), the lock can linger until OS TCP keepalive fires — that's
/// up to <c>tcp_keepalives_idle</c> seconds, which defaults to 7200 (two hours) on
/// most Linux kernels. Until the lock releases, the dead pod's leases stay frozen and
/// <c>claim_orphaned_*</c> can't pick them up.</para>
///
/// <para>The fix is the second parameter <c>p_definitive_dead_cutoff</c> — a hard
/// time-based threshold past which the alive-lock guard is bypassed. The lock remains
/// the primary liveness signal for the legitimate adaptive-heartbeat case (heartbeat
/// 30 s ≤ age &lt; 5 min) but the heartbeat-table staleness becomes authoritative once
/// it crosses the definitive cutoff (≥ 5 min by default in production callers). The
/// parameter is optional with DEFAULT NULL so existing single-arg callers preserve
/// their pre-v0.687 semantics.</para>
///
/// <para><strong>Locked invariants:</strong></para>
/// <list type="bullet">
/// <item><description>When <c>p_definitive_dead_cutoff</c> is provided AND the
/// instance's heartbeat is older than it, the instance is deleted EVEN IF the
/// alive-lock is still held.</description></item>
/// <item><description>When the instance's heartbeat is stale but newer than the
/// definitive cutoff, the alive-lock guard still applies (preserves the legitimate
/// adaptive-heartbeat case).</description></item>
/// <item><description>When <c>p_definitive_dead_cutoff</c> is NULL (legacy callers),
/// behavior is identical to v0.681 — the alive-lock guard is the sole bypass condition.</description></item>
/// </list>
/// </summary>
public class CleanupStaleInstancesDefinitiveDeathSqlTests : EFCoreTestBase {

  [Test]
  public async Task CleanupStaleInstances_HeartbeatPastDefinitiveCutoff_DeletesEvenWhenLockHeldAsync() {
    // RED: the v0.687 fix. A pod is OOMKilled. Its TCP session lingers half-open
    // (worst case: hours). Its alive-lock therefore stays held in pg_locks. The
    // heartbeat row hasn't been updated in 5+ minutes. cleanup_stale_instances must
    // delete it anyway — the heartbeat staleness past the definitive cutoff is
    // authoritative.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var deadInstance = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, deadInstance, lastHeartbeatOffset: TimeSpan.FromMinutes(-10));

    // Hold the alive-lock for the dead instance from a SECOND connection, simulating
    // the half-open TCP case where Postgres hasn't noticed the session is dead.
    await using var lockHolder = new NpgsqlConnection(ConnectionString);
    await lockHolder.OpenAsync();
    try {
      await _claimAliveLockAsync(lockHolder, deadInstance);
      var lockStillHeld = await _isAliveLockHeldAsync(conn, deadInstance);
      await Assert.That(lockStillHeld).IsTrue()
        .Because("Test precondition: the lock-holder connection must currently hold the alive-lock so the cleanup function sees it in pg_locks.");

      // Call cleanup with BOTH cutoffs. Stale at 30 s, definitively dead at 5 min.
      var deleted = await _callCleanupAsync(
        conn,
        staleCutoff: DateTimeOffset.UtcNow.AddSeconds(-30),
        definitiveDeadCutoff: DateTimeOffset.UtcNow.AddMinutes(-5));

      await Assert.That(deleted).Contains(deadInstance)
        .Because("v0.687 — when heartbeat is older than p_definitive_dead_cutoff, the alive-lock guard is bypassed and the instance is deleted. Without this, leases held by OOMKilled pods on misbehaving TCP paths stay frozen until the OS keepalive timer expires (default 2 h on Linux).");
    } finally {
      await lockHolder.CloseAsync();
    }
  }

  [Test]
  public async Task CleanupStaleInstances_HeartbeatBeforeDefinitiveCutoff_LockGuardStillAppliesAsync() {
    // Regression lock for the v0.681 behavior: within the lock-guard window (heartbeat
    // stale by stale_cutoff but newer than definitive_dead_cutoff), the alive-lock
    // still wins. This preserves the legitimate adaptive-heartbeat case where the
    // direct conn is healthy but the heartbeat table write was delayed.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var staleButLiveInstance = (Guid)TrackedGuid.NewMedo();
    // Heartbeat is 90 s old: past the 30 s stale cutoff but well before the 5 min
    // definitive cutoff.
    await _registerInstanceAsync(conn, staleButLiveInstance, lastHeartbeatOffset: TimeSpan.FromSeconds(-90));

    await using var lockHolder = new NpgsqlConnection(ConnectionString);
    await lockHolder.OpenAsync();
    try {
      await _claimAliveLockAsync(lockHolder, staleButLiveInstance);

      var deleted = await _callCleanupAsync(
        conn,
        staleCutoff: DateTimeOffset.UtcNow.AddSeconds(-30),
        definitiveDeadCutoff: DateTimeOffset.UtcNow.AddMinutes(-5));

      await Assert.That(deleted).DoesNotContain(staleButLiveInstance)
        .Because("Within the lock-guard window (stale_cutoff ≤ age < definitive_cutoff) the alive-lock is authoritative — adaptive heartbeat may legitimately have delayed the table write.");
    } finally {
      await lockHolder.CloseAsync();
    }
  }

  [Test]
  public async Task CleanupStaleInstances_NoDefinitiveCutoff_LegacyBehaviorPreservedAsync() {
    // Backwards-compatibility lock: when p_definitive_dead_cutoff is omitted (NULL),
    // behavior must match pre-v0.687. The alive-lock guard is the only bypass.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var oldDeadInstance = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, oldDeadInstance, lastHeartbeatOffset: TimeSpan.FromMinutes(-30));

    await using var lockHolder = new NpgsqlConnection(ConnectionString);
    await lockHolder.OpenAsync();
    try {
      await _claimAliveLockAsync(lockHolder, oldDeadInstance);

      // Single-arg signature — definitive cutoff defaults to NULL.
      var deleted = await _callCleanupAsync(
        conn,
        staleCutoff: DateTimeOffset.UtcNow.AddSeconds(-30),
        definitiveDeadCutoff: null);

      await Assert.That(deleted).DoesNotContain(oldDeadInstance)
        .Because("When p_definitive_dead_cutoff is NULL (legacy callers), the alive-lock guard still wins regardless of heartbeat age — same as pre-v0.687.");
    } finally {
      await lockHolder.CloseAsync();
    }
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _claimAliveLockAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT claim_instance_alive_lock(@id)";
    cmd.Parameters.AddWithValue("id", instanceId);
    _ = await cmd.ExecuteScalarAsync();
  }

  private static async Task<bool> _isAliveLockHeldAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT EXISTS (
        SELECT 1 FROM pg_locks
        WHERE locktype = 'advisory'
          AND classid = ((hashtext('wh_instance_alive:' || @id::text)::bigint >> 32) & x'FFFFFFFF'::bigint)::oid
          AND objid = (hashtext('wh_instance_alive:' || @id::text)::bigint & x'FFFFFFFF'::bigint)::oid
          AND granted = true
      )";
    cmd.Parameters.AddWithValue("id", instanceId);
    var result = await cmd.ExecuteScalarAsync();
    return result is bool b && b;
  }

  private static async Task<List<Guid>> _callCleanupAsync(
      NpgsqlConnection conn,
      DateTimeOffset staleCutoff,
      DateTimeOffset? definitiveDeadCutoff) {
    await using var cmd = conn.CreateCommand();
    if (definitiveDeadCutoff is null) {
      cmd.CommandText = "SELECT deleted_instance_id FROM cleanup_stale_instances(@cutoff)";
    } else {
      cmd.CommandText = "SELECT deleted_instance_id FROM cleanup_stale_instances(@cutoff, @def)";
      cmd.Parameters.Add(new NpgsqlParameter("def", NpgsqlDbType.TimestampTz) {
        Value = definitiveDeadCutoff.Value
      });
    }
    cmd.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz) {
      Value = staleCutoff
    });

    var deleted = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      deleted.Add(reader.GetGuid(0));
    }
    return deleted;
  }

  private static async Task _registerInstanceAsync(
      NpgsqlConnection conn, Guid instanceId, TimeSpan lastHeartbeatOffset) {
    var hb = DateTimeOffset.UtcNow + lastHeartbeatOffset;
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
