using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the perform_maintenance() PostgreSQL function.
/// Tests deduplication table cleanup (Task 4) and stuck inbox purge (Task 5).
/// </summary>
public class MaintenanceTests : EFCoreTestBase {

  private async Task<NpgsqlConnection> _openConnectionAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task<List<(string TaskName, long RowsAffected, double DurationMs, string Status)>>
    _runMaintenanceAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM public.perform_maintenance()";
    await using var reader = await cmd.ExecuteReaderAsync();
    var results = new List<(string, long, double, string)>();
    while (await reader.ReadAsync()) {
      results.Add((
        reader.GetString(0),
        reader.GetInt64(1),
        reader.GetDouble(2),
        reader.GetString(3)
      ));
    }
    return results;
  }

  // ================================================================
  // Task 4: Deduplication table cleanup
  // ================================================================

  [Test]
  public async Task PerformMaintenance_PurgesDeduplicationEntries_OlderThanRetentionAsync() {
    // Arrange — insert dedup rows older than 30 days
    await using var conn = await _openConnectionAsync();
    var oldId = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_message_deduplication (message_id, first_seen_at)
      VALUES ('{oldId}', NOW() - INTERVAL '31 days')");

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert
    var dedupTask = results.FirstOrDefault(r => r.TaskName == "purge_old_deduplication");
    await Assert.That(dedupTask.TaskName).IsNotNull();
    await Assert.That(dedupTask.RowsAffected).IsGreaterThanOrEqualTo(1);

    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_message_deduplication WHERE message_id = '{oldId}'");
    await Assert.That(remaining).IsEqualTo(0);
  }

  [Test]
  public async Task PerformMaintenance_PreservesRecentDeduplicationEntries_WithinRetentionAsync() {
    // Arrange — insert dedup rows within 30 days
    await using var conn = await _openConnectionAsync();
    var recentId = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_message_deduplication (message_id, first_seen_at)
      VALUES ('{recentId}', NOW() - INTERVAL '5 days')");

    // Act
    await _runMaintenanceAsync(conn);

    // Assert
    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_message_deduplication WHERE message_id = '{recentId}'");
    await Assert.That(remaining).IsEqualTo(1);
  }

  [Test]
  public async Task PerformMaintenance_RespectsConfigurableRetentionPeriod_ViaWhSettingsAsync() {
    // Arrange — set retention to 1 day, insert a 2-day-old row
    await using var conn = await _openConnectionAsync();
    await conn.ExecuteAsync(@"
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('dedup_retention_days', '1', 'integer', 'test override')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = '1'");

    var oldId = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_message_deduplication (message_id, first_seen_at)
      VALUES ('{oldId}', NOW() - INTERVAL '2 days')");

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert — 2-day-old row should be deleted with 1-day retention
    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_message_deduplication WHERE message_id = '{oldId}'");
    await Assert.That(remaining).IsEqualTo(0);
  }

  [Test]
  public async Task PerformMaintenance_ReturnsCorrectRowCount_ForDeduplicationPurgeAsync() {
    // Arrange — insert 3 old rows
    await using var conn = await _openConnectionAsync();
    for (var i = 0; i < 3; i++) {
      var id = Guid.CreateVersion7();
      await conn.ExecuteAsync($@"
        INSERT INTO wh_message_deduplication (message_id, first_seen_at)
        VALUES ('{id}', NOW() - INTERVAL '31 days')");
    }

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert
    var dedupTask = results.FirstOrDefault(r => r.TaskName == "purge_old_deduplication");
    await Assert.That(dedupTask.RowsAffected).IsEqualTo(3);
  }

  [Test]
  public async Task PerformMaintenance_HandlesEmptyDeduplicationTable_GracefullyAsync() {
    // Arrange — ensure table is empty (no old rows)
    await using var conn = await _openConnectionAsync();

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert — task should return 0 rows affected, no error
    var dedupTask = results.FirstOrDefault(r => r.TaskName == "purge_old_deduplication");
    await Assert.That(dedupTask.TaskName).IsNotNull();
    await Assert.That(dedupTask.RowsAffected).IsEqualTo(0);
    await Assert.That(dedupTask.Status).IsEqualTo("ok");
  }

  // ================================================================
  // Task 5: Stuck inbox message cleanup
  // ================================================================

  [Test]
  public async Task PerformMaintenance_PurgesStuckInboxMessages_OlderThanRetentionAsync() {
    // Arrange — insert stuck inbox row (NULL processed_at, lease, instance) older than 7 days
    await using var conn = await _openConnectionAsync();
    var stuckId = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, status, attempts, received_at)
      VALUES ('{stuckId}', 'test', 'TestEvent', '{{}}'::jsonb, '{{}}'::jsonb, 'null'::jsonb, 1, 0, NOW() - INTERVAL '8 days')");

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert
    var stuckTask = results.FirstOrDefault(r => r.TaskName == "purge_stuck_inbox");
    await Assert.That(stuckTask.TaskName).IsNotNull();
    await Assert.That(stuckTask.RowsAffected).IsGreaterThanOrEqualTo(1);

    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_inbox WHERE message_id = '{stuckId}'");
    await Assert.That(remaining).IsEqualTo(0);
  }

  [Test]
  public async Task PerformMaintenance_PreservesRecentStuckInboxMessages_WithinRetentionAsync() {
    // Arrange — insert stuck row within 7 days
    await using var conn = await _openConnectionAsync();
    var recentId = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, status, attempts, received_at)
      VALUES ('{recentId}', 'test', 'TestEvent', '{{}}'::jsonb, '{{}}'::jsonb, 'null'::jsonb, 1, 0, NOW() - INTERVAL '3 days')");

    // Act
    await _runMaintenanceAsync(conn);

    // Assert
    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_inbox WHERE message_id = '{recentId}'");
    await Assert.That(remaining).IsEqualTo(1);
  }

  [Test]
  public async Task PerformMaintenance_PreservesLeasedInboxMessages_EvenIfOldAsync() {
    // Arrange — insert old row WITH lease_expiry (actively being processed)
    await using var conn = await _openConnectionAsync();
    var leasedId = Guid.CreateVersion7();
    var instanceId = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, status, attempts, received_at, instance_id, lease_expiry)
      VALUES ('{leasedId}', 'test', 'TestEvent', '{{}}'::jsonb, '{{}}'::jsonb, 'null'::jsonb, 1, 0, NOW() - INTERVAL '30 days', '{instanceId}', NOW() + INTERVAL '5 minutes')");

    // Act
    await _runMaintenanceAsync(conn);

    // Assert — leased row must NOT be deleted
    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_inbox WHERE message_id = '{leasedId}'");
    await Assert.That(remaining).IsEqualTo(1);
  }

  [Test]
  public async Task PerformMaintenance_PreservesClaimedInboxMessages_EvenIfOldAsync() {
    // Arrange — insert old row WITH instance_id but NULL lease (claimed, no lease yet)
    await using var conn = await _openConnectionAsync();
    var claimedId = Guid.CreateVersion7();
    var instanceId = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, status, attempts, received_at, instance_id)
      VALUES ('{claimedId}', 'test', 'TestEvent', '{{}}'::jsonb, '{{}}'::jsonb, 'null'::jsonb, 1, 0, NOW() - INTERVAL '30 days', '{instanceId}')");

    // Act
    await _runMaintenanceAsync(conn);

    // Assert — claimed row must NOT be deleted
    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_inbox WHERE message_id = '{claimedId}'");
    await Assert.That(remaining).IsEqualTo(1);
  }

  [Test]
  public async Task PerformMaintenance_PreservesProcessedInboxMessages_HandledByTask2Async() {
    // Arrange — insert old row WITH processed_at (already handled by existing Task 2)
    await using var conn = await _openConnectionAsync();
    var processedId = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, status, attempts, received_at, processed_at)
      VALUES ('{processedId}', 'test', 'TestEvent', '{{}}'::jsonb, '{{}}'::jsonb, 'null'::jsonb, 3, 0, NOW() - INTERVAL '30 days', NOW() - INTERVAL '29 days')");

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert — Task 2 (purge_completed_inbox) should have deleted it, NOT Task 5
    var completedTask = results.FirstOrDefault(r => r.TaskName == "purge_completed_inbox");
    await Assert.That(completedTask.RowsAffected).IsGreaterThanOrEqualTo(1);

    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_inbox WHERE message_id = '{processedId}'");
    await Assert.That(remaining).IsEqualTo(0);
  }

  [Test]
  public async Task PerformMaintenance_RespectsConfigurableStuckRetention_ViaWhSettingsAsync() {
    // Arrange — set stuck retention to 1 day, insert 2-day-old stuck row
    await using var conn = await _openConnectionAsync();
    await conn.ExecuteAsync(@"
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('stuck_inbox_retention_days', '1', 'integer', 'test override')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = '1'");

    var stuckId = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, status, attempts, received_at)
      VALUES ('{stuckId}', 'test', 'TestEvent', '{{}}'::jsonb, '{{}}'::jsonb, 'null'::jsonb, 1, 0, NOW() - INTERVAL '2 days')");

    // Act
    await _runMaintenanceAsync(conn);

    // Assert — 2-day-old stuck row should be deleted with 1-day retention
    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_inbox WHERE message_id = '{stuckId}'");
    await Assert.That(remaining).IsEqualTo(0);
  }

  // ================================================================
  // Task 6: Abandoned active-stream ownership cleanup
  // ================================================================

  [Test]
  public async Task PerformMaintenance_PrunesActiveStreamsWhenInstanceIsMissing_DeletesAbandonedRowsAsync() {
    // Arrange — one live instance with its stream, one abandoned row, one NULL-owner row.
    await using var conn = await _openConnectionAsync();

    var liveId = Guid.CreateVersion7();
    var liveStream = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES ('{liveId}', 'test', 'host', 1, NOW(), NOW())");
    await conn.ExecuteAsync($@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry, last_activity_at)
      VALUES ('{liveStream}', 0, '{liveId}', NOW() + INTERVAL '5 minutes', NOW())");

    var missingId = Guid.CreateVersion7();  // NOT inserted into wh_service_instances
    var abandonedStream = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry, last_activity_at)
      VALUES ('{abandonedStream}', 0, '{missingId}', NOW() + INTERVAL '5 minutes', NOW())");

    var unownedStream = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry, last_activity_at)
      VALUES ('{unownedStream}', 0, NULL, NULL, NOW())");

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert — task reports 1 row deleted.
    var task6 = results.FirstOrDefault(r => r.TaskName == "purge_abandoned_active_streams");
    await Assert.That(task6.TaskName).IsNotNull();
    await Assert.That(task6.RowsAffected).IsEqualTo(1L);
    await Assert.That(task6.Status).IsEqualTo("ok");

    // Abandoned row is gone.
    var abandoned = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = '{abandonedStream}'");
    await Assert.That(abandoned).IsEqualTo(0);

    // Live-owner row survives.
    var live = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = '{liveStream}'");
    await Assert.That(live).IsEqualTo(1);

    // NULL-owner row survives (not touched by this task).
    var unowned = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = '{unownedStream}'");
    await Assert.That(unowned).IsEqualTo(1);
  }

  [Test]
  public async Task PerformMaintenance_PreservesLiveOwnerActiveStreamsEvenWithExpiredLeaseAsync() {
    // A live instance's lease can expire without the instance itself being dead — e.g.,
    // if the instance paused briefly. Liveness is per-instance (heartbeat), not per-lease.
    // Task 6 must not prune these; that's claim_orphaned_inbox's job.
    await using var conn = await _openConnectionAsync();

    var liveId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES ('{liveId}', 'test', 'host', 1, NOW(), NOW())");
    await conn.ExecuteAsync($@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry, last_activity_at)
      VALUES ('{streamId}', 0, '{liveId}', NOW() - INTERVAL '1 hour', NOW())");

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert — row survives because the owner still heartbeats.
    var task6 = results.FirstOrDefault(r => r.TaskName == "purge_abandoned_active_streams");
    await Assert.That(task6.RowsAffected).IsEqualTo(0L);

    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = '{streamId}'");
    await Assert.That(remaining).IsEqualTo(1);
  }

  [Test]
  public async Task PerformMaintenance_WithNoAbandonedStreams_ReportsZeroRowsAffectedAsync() {
    // Empty-state guard: no rows to clean up should still return a result record.
    await using var conn = await _openConnectionAsync();

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert — task is present with ok status and 0 rows.
    var task6 = results.FirstOrDefault(r => r.TaskName == "purge_abandoned_active_streams");
    await Assert.That(task6.TaskName).IsNotNull();
    await Assert.That(task6.RowsAffected).IsEqualTo(0L);
    await Assert.That(task6.Status).IsEqualTo("ok");
  }
}
