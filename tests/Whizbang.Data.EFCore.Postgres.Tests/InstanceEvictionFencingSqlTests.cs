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
/// <c>cleanup_stale_instances</c> deletes a stale instance's row and releases its work, but
/// <c>record_heartbeat</c> is an unguarded <c>INSERT ... ON CONFLICT DO UPDATE</c> — the reaped
/// instance's next heartbeat re-inserts it and it rejoins as though nothing happened, still holding
/// whatever it believed it owned. These tests lock the fence: a reaped instance is tombstoned in
/// <c>wh_instance_evictions</c>, and <c>record_heartbeat</c> consults that tombstone and refuses
/// rather than silently letting the instance back in — reporting the refusal through its own return
/// value rather than a side channel.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/106_InstanceEvictionFencing.sql</code-under-test>
public class InstanceEvictionFencingSqlTests : EFCoreTestBase {

  [Test]
  public async Task CleanupStaleInstances_WhenItReapsAnInstance_TombstonesItAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var deadInstance = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, deadInstance, lastHeartbeatOffset: TimeSpan.FromMinutes(-10));

    await _cleanupAsync(conn, staleCutoff: DateTimeOffset.UtcNow.AddSeconds(-30));

    var tombstoned = await _isEvictedAsync(conn, deadInstance);
    await Assert.That(tombstoned).IsTrue()
      .Because("reaping must leave a durable record behind, or a returning zombie has nothing to consult");
  }

  [Test]
  public async Task RecordHeartbeat_ForATombstonedInstance_RefusesAndDoesNotInsertAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var evictedInstance = (Guid)TrackedGuid.NewMedo();
    await _tombstoneAsync(conn, evictedInstance);

    var accepted = await _recordHeartbeatAsync(conn, evictedInstance);

    await Assert.That(accepted).IsFalse()
      .Because("the reaped instance's next heartbeat must be refused, not silently re-inserted");

    var row = await _rowExistsAsync(conn, evictedInstance);
    await Assert.That(row).IsFalse()
      .Because("a refused heartbeat must not create wh_service_instances state for the evicted id");
  }

  [Test]
  public async Task RecordHeartbeat_ForAnNonEvictedInstance_AcceptsAndReturnsTrueAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var freshInstance = (Guid)TrackedGuid.NewMedo();

    var accepted = await _recordHeartbeatAsync(conn, freshInstance);

    await Assert.That(accepted).IsTrue()
      .Because("an instance with no tombstone is an ordinary heartbeat and must be accepted");
    await Assert.That(await _rowExistsAsync(conn, freshInstance)).IsTrue();
  }

  // The end-to-end shape the defect actually took: reap, then the SAME process (same instance_id)
  // resumes and heartbeats again.
  [Test]
  public async Task Zombie_ThatHeartbeatsAfterBeingReaped_IsRefusedNotRejoinedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var zombie = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, zombie, lastHeartbeatOffset: TimeSpan.FromMinutes(-10));
    await _cleanupAsync(conn, staleCutoff: DateTimeOffset.UtcNow.AddSeconds(-30));
    await Assert.That(await _rowExistsAsync(conn, zombie)).IsFalse()
      .Because("precondition: cleanup must have actually reaped it");

    // The zombie resumes and calls heartbeat again, exactly as the paused process would.
    var accepted = await _recordHeartbeatAsync(conn, zombie);

    await Assert.That(accepted).IsFalse()
      .Because("this is the defect itself: without the tombstone, this call would silently succeed "
               + "and the zombie would rejoin as though it had never been reaped");
    await Assert.That(await _rowExistsAsync(conn, zombie)).IsFalse();
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

  private static async Task _cleanupAsync(NpgsqlConnection conn, DateTimeOffset staleCutoff) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT deleted_instance_id FROM cleanup_stale_instances(@cutoff)";
    cmd.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz) { Value = staleCutoff });
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* drain */ }
  }

  private static async Task _tombstoneAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_instance_evictions (instance_id, evicted_at, reason)
      VALUES (@id, NOW(), 'test')";
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<bool> _isEvictedAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM wh_instance_evictions WHERE instance_id = @id)";
    cmd.Parameters.AddWithValue("id", instanceId);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<bool> _rowExistsAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM wh_service_instances WHERE instance_id = @id)";
    cmd.Parameters.AddWithValue("id", instanceId);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<bool> _recordHeartbeatAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT record_heartbeat(@id, 'test-svc', 'test-host', 1, '{}'::jsonb)";
    cmd.Parameters.AddWithValue("id", instanceId);
    var result = await cmd.ExecuteScalarAsync();
    return (bool)result!;
  }
}
