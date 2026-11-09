// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
[Category("Shard4")]
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
    var (TaskName, RowsAffected, _, _) = results.FirstOrDefault(r => r.TaskName == "purge_old_deduplication");
    await Assert.That(TaskName).IsNotNull();
    await Assert.That(RowsAffected).IsGreaterThanOrEqualTo(1);

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
    _ = await _runMaintenanceAsync(conn);

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
    var (_, RowsAffected, _, _) = results.FirstOrDefault(r => r.TaskName == "purge_old_deduplication");
    await Assert.That(RowsAffected).IsEqualTo(3);
  }

  [Test]
  public async Task PerformMaintenance_HandlesEmptyDeduplicationTable_GracefullyAsync() {
    // Arrange — ensure table is empty (no old rows)
    await using var conn = await _openConnectionAsync();

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert — task should return 0 rows affected, no error
    var (TaskName, RowsAffected, _, Status) = results.FirstOrDefault(r => r.TaskName == "purge_old_deduplication");
    await Assert.That(TaskName).IsNotNull();
    await Assert.That(RowsAffected).IsEqualTo(0);
    await Assert.That(Status).IsEqualTo("ok");
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
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, received_at)
        VALUES ('{stuckId}', 'test', 'TestEvent', '{{}}'::jsonb, '{{}}'::jsonb, 'null'::jsonb, NOW() - INTERVAL '8 days')
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0 FROM m");

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert
    var (TaskName, RowsAffected, _, _) = results.FirstOrDefault(r => r.TaskName == "purge_stuck_inbox");
    await Assert.That(TaskName).IsNotNull();
    await Assert.That(RowsAffected).IsGreaterThanOrEqualTo(1);

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
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, received_at)
        VALUES ('{recentId}', 'test', 'TestEvent', '{{}}'::jsonb, '{{}}'::jsonb, 'null'::jsonb, NOW() - INTERVAL '3 days')
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0 FROM m");

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
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, received_at)
        VALUES ('{leasedId}', 'test', 'TestEvent', '{{}}'::jsonb, '{{}}'::jsonb, 'null'::jsonb, NOW() - INTERVAL '30 days')
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts, instance_id, lease_expiry)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, '{instanceId}', NOW() + INTERVAL '5 minutes' FROM m");

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
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, received_at)
        VALUES ('{claimedId}', 'test', 'TestEvent', '{{}}'::jsonb, '{{}}'::jsonb, 'null'::jsonb, NOW() - INTERVAL '30 days')
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts, instance_id)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, '{instanceId}' FROM m");

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
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, received_at)
        VALUES ('{processedId}', 'test', 'TestEvent', '{{}}'::jsonb, '{{}}'::jsonb, 'null'::jsonb, NOW() - INTERVAL '30 days')
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts, processed_at)
      SELECT message_id, stream_id, received_at, priority, is_event, 3, 0, NOW() - INTERVAL '29 days' FROM m");

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert — Task 2 (purge_completed_inbox) should have deleted it, NOT Task 5
    var (_, RowsAffected, _, _) = results.FirstOrDefault(r => r.TaskName == "purge_completed_inbox");
    await Assert.That(RowsAffected).IsGreaterThanOrEqualTo(1);

    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_inbox WHERE message_id = '{processedId}'");
    await Assert.That(remaining).IsEqualTo(0);
  }

  // ================================================================
  // Task 2: the completed-inbox purge is bounded per cycle (366)
  //
  // The purge was one unbounded DELETE inside the single maintenance call, which runs under a fixed
  // command budget. A consumer that stores many arrivals settled (a message discarded on receive is
  // stored processed, and only this purge removes it) can accumulate more completed rows than one
  // DELETE finishes inside that budget. The call then times out, the whole cycle rolls back, and
  // the backlog only grows: every later cycle has more to delete and fails the same way, and every
  // sweep that runs after the call in the cycle is skipped with it.
  // ================================================================

  private static Task _insertInboxRowAsync(NpgsqlConnection conn, Guid id, bool processed) =>
    conn.ExecuteAsync($@"
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, received_at)
        VALUES ('{id}', 'test', 'TestEvent', '{{}}'::jsonb, '{{}}'::jsonb, 'null'::jsonb, NOW())
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts, processed_at)
      SELECT message_id, stream_id, received_at, priority, is_event, 5, 0, {(processed ? "NOW()" : "NULL")} FROM m");

  private static Task _setCompletedInboxPurgeBatchAsync(NpgsqlConnection conn, string value) =>
    conn.ExecuteAsync($@"
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('completed_inbox_purge_batch_size', '{value}', 'integer', 'test override')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value");

  private static async Task<long> _purgedCompletedInboxAsync(NpgsqlConnection conn) =>
    (await _runMaintenanceAsync(conn)).Single(r => r.TaskName == "purge_completed_inbox").RowsAffected;

  [Test]
  public async Task PerformMaintenance_PurgesAtMostTheBatchOfCompletedInboxRows_AndTheRestOnTheNextCycleAsync() {
    await using var conn = await _openConnectionAsync();
    await _setCompletedInboxPurgeBatchAsync(conn, "3");
    for (var i = 0; i < 5; i++) {
      await _insertInboxRowAsync(conn, Guid.CreateVersion7(), processed: true);
    }
    var unprocessedId = Guid.CreateVersion7();
    await _insertInboxRowAsync(conn, unprocessedId, processed: false);

    await Assert.That(await _purgedCompletedInboxAsync(conn)).IsEqualTo(3L)
      .Because("one cycle deletes at most the batch, so the call fits its budget however large the backlog is");
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_inbox_state WHERE processed_at IS NOT NULL"))
      .IsEqualTo(2L);

    await Assert.That(await _purgedCompletedInboxAsync(conn)).IsEqualTo(2L)
      .Because("the rest of the backlog goes on the next cycle");
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_inbox_state WHERE processed_at IS NOT NULL"))
      .IsEqualTo(0L);
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_inbox"))
      .IsEqualTo(1L).Because("the message row of a completed row goes with it, and an unprocessed row is never purged");
    await Assert.That(await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM wh_inbox WHERE message_id = '{unprocessedId}'"))
      .IsEqualTo(1L);
  }

  [Test]
  public async Task PerformMaintenance_ABatchSetBelowOne_StillPurgesOneRowPerCycleAsync() {
    await using var conn = await _openConnectionAsync();
    await _setCompletedInboxPurgeBatchAsync(conn, "0");
    await _insertInboxRowAsync(conn, Guid.CreateVersion7(), processed: true);
    await _insertInboxRowAsync(conn, Guid.CreateVersion7(), processed: true);

    await Assert.That(await _purgedCompletedInboxAsync(conn)).IsEqualTo(1L)
      .Because("a batch of zero written by hand would stop the purge for good; debug_mode is the only off switch");
  }

  [Test]
  public async Task PerformMaintenance_WithNoBatchSetting_PurgesUpToTheDefaultAsync() {
    await using var conn = await _openConnectionAsync();
    await conn.ExecuteAsync("DELETE FROM wh_settings WHERE setting_key = 'completed_inbox_purge_batch_size'");
    await _insertInboxRowAsync(conn, Guid.CreateVersion7(), processed: true);
    await _insertInboxRowAsync(conn, Guid.CreateVersion7(), processed: true);

    await Assert.That(await _purgedCompletedInboxAsync(conn)).IsEqualTo(2L)
      .Because("a database without the row uses the default, which is far above two rows");
  }

  [Test]
  public async Task Migration_SeedsTheCompletedInboxPurgeBatchDefaultAsync() {
    await using var conn = await _openConnectionAsync();

    var value = await conn.ExecuteScalarAsync<string>(
      "SELECT setting_value FROM wh_settings WHERE setting_key = 'completed_inbox_purge_batch_size'");

    await Assert.That(value).IsEqualTo("50000")
      .Because("the knob is visible to an operator reading wh_settings, at the value the function falls back to");
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
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, scope, received_at)
        VALUES ('{stuckId}', 'test', 'TestEvent', '{{}}'::jsonb, '{{}}'::jsonb, 'null'::jsonb, NOW() - INTERVAL '2 days')
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0 FROM m");

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
    var (TaskName, RowsAffected, _, Status) = results.FirstOrDefault(r => r.TaskName == "purge_abandoned_active_streams");
    await Assert.That(TaskName).IsNotNull();
    await Assert.That(RowsAffected).IsEqualTo(1L);
    await Assert.That(Status).IsEqualTo("ok");

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
    var (_, RowsAffected, _, _) = results.FirstOrDefault(r => r.TaskName == "purge_abandoned_active_streams");
    await Assert.That(RowsAffected).IsEqualTo(0L);

    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = '{streamId}'");
    await Assert.That(remaining).IsEqualTo(1);
  }

  /// <summary>
  /// Production forensic: wh_active_streams accumulated a large number of rows
  /// older than 7 days, 99% of them with <c>assigned_instance_id IS NULL</c>.
  /// Root cause: <c>cleanup_stale_instances</c> nulls
  /// <c>assigned_instance_id</c> when its owner instance dies, but
  /// <c>perform_maintenance</c>'s Task 6 DELETE filters on
  /// <c>assigned_instance_id IS NOT NULL</c> — so NULL-owner rows are
  /// invisible to the cleanup forever. This test locks the fix: NULL-owner
  /// rows older than a grace period MUST also be purged.
  /// </summary>
  [Test]
  public async Task PerformMaintenance_PrunesNullOwnerActiveStreamsOlderThanGrace_DeletesAbandonedAsync() {
    await using var conn = await _openConnectionAsync();

    var oldUnowned = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry, last_activity_at)
      VALUES ('{oldUnowned}', 0, NULL, NULL, NOW() - INTERVAL '2 hours')");

    var results = await _runMaintenanceAsync(conn);

    var (TaskName, RowsAffected, _, Status) = results.FirstOrDefault(r => r.TaskName == "purge_abandoned_active_streams");
    await Assert.That(TaskName).IsNotNull();
    await Assert.That(Status).IsEqualTo("ok");
    await Assert.That(RowsAffected).IsGreaterThanOrEqualTo(1L)
      .Because("A NULL-owner row whose last activity is past the grace period (1 hour) MUST be deleted — otherwise these rows accumulate forever (production accumulated a large backlog of them).");

    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = '{oldUnowned}'");
    await Assert.That(remaining).IsEqualTo(0)
      .Because("The specific NULL-owner row past the grace period MUST be gone after maintenance.");
  }

  /// <summary>
  /// Race-safety regression lock: a NULL-owner row whose last activity is
  /// RECENT must be preserved. NULL-owner is briefly transient — between
  /// <c>cleanup_stale_instances</c> nulling the field and the next claim
  /// cycle re-assigning via <c>INSERT ON CONFLICT</c>, the row legitimately
  /// has no owner. Deleting these would race the re-assignment path and
  /// risk losing in-flight stream identity.
  /// </summary>
  [Test]
  public async Task PerformMaintenance_PreservesFreshNullOwnerActiveStreams_RaceSafetyAsync() {
    await using var conn = await _openConnectionAsync();

    var freshUnowned = Guid.CreateVersion7();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry, last_activity_at)
      VALUES ('{freshUnowned}', 0, NULL, NULL, NOW())");

    await _runMaintenanceAsync(conn);

    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = '{freshUnowned}'");
    await Assert.That(remaining).IsEqualTo(1)
      .Because("A fresh NULL-owner row (within the grace period) MUST survive maintenance — it may be mid-reassignment via claim_orphaned_*'s INSERT ON CONFLICT path.");
  }

  [Test]
  public async Task PerformMaintenance_WithNoAbandonedStreams_ReportsZeroRowsAffectedAsync() {
    // Empty-state guard: no rows to clean up should still return a result record.
    await using var conn = await _openConnectionAsync();

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert — task is present with ok status and 0 rows.
    var (TaskName, RowsAffected, _, Status) = results.FirstOrDefault(r => r.TaskName == "purge_abandoned_active_streams");
    await Assert.That(TaskName).IsNotNull();
    await Assert.That(RowsAffected).IsEqualTo(0L);
    await Assert.That(Status).IsEqualTo("ok");
  }

  // ================================================================
  // Debug-mode preservation
  //
  // In debug mode (wh_settings.debug_mode = true), `complete_*` functions retain
  // their rows for forensics with `processed_at` stamped. perform_maintenance
  // MUST NOT delete those rows or it defeats the debug-mode purpose entirely.
  // ================================================================

  [Test]
  public async Task PerformMaintenance_DebugModeOn_PreservesProcessedOutboxRowsAsync() {
    await using var conn = await _openConnectionAsync();
    await _setDebugModeAsync(conn, true);

    var msgId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number, processed_at, published_at)
      VALUES ('{msgId}', 'topic', 'Type', '{{}}', '{{}}', 5, 0, NOW(), '{streamId}', 0, NOW(), NOW())");

    var results = await _runMaintenanceAsync(conn);

    var (_, RowsAffected, _, _) = results.FirstOrDefault(r => r.TaskName == "purge_completed_outbox");
    await Assert.That(RowsAffected).IsEqualTo(0L);

    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_outbox WHERE message_id = '{msgId}'");
    await Assert.That(remaining).IsEqualTo(1);
  }

  [Test]
  public async Task PerformMaintenance_DebugModeOn_PreservesProcessedInboxRowsAsync() {
    await using var conn = await _openConnectionAsync();
    await _setDebugModeAsync(conn, true);

    var msgId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await conn.ExecuteAsync($@"
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id)
        VALUES ('{msgId}', 'TestHandler', 'Type', '{{}}', '{{}}', NOW(), '{streamId}')
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, processed_at)
      SELECT message_id, stream_id, received_at, priority, is_event, 5, 0, 0, NOW() FROM m");

    var results = await _runMaintenanceAsync(conn);

    var (_, RowsAffected, _, _) = results.FirstOrDefault(r => r.TaskName == "purge_completed_inbox");
    await Assert.That(RowsAffected).IsEqualTo(0L);

    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_inbox WHERE message_id = '{msgId}'");
    await Assert.That(remaining).IsEqualTo(1);
  }

  [Test]
  public async Task PerformMaintenance_DebugModeOn_PreservesProcessedPerspectiveEventRowsAsync() {
    await using var conn = await _openConnectionAsync();
    await _setDebugModeAsync(conn, true);

    var workId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at, processed_at)
      VALUES ('{workId}', '{streamId}', 'TestPerspective', '{eventId}', 1, 0, NOW(), NOW())");

    var results = await _runMaintenanceAsync(conn);

    var (_, RowsAffected, _, _) = results.FirstOrDefault(r => r.TaskName == "purge_completed_perspective_events");
    await Assert.That(RowsAffected).IsEqualTo(0L);

    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_perspective_events WHERE event_work_id = '{workId}'");
    await Assert.That(remaining).IsEqualTo(1);
  }

  [Test]
  public async Task PerformMaintenance_DebugModeOff_DeletesProcessedOutboxRowsAsync() {
    // Negative path of the debug-mode guard: with debug_mode=false (default), the
    // production-style purge must continue to fire as before.
    await using var conn = await _openConnectionAsync();
    await _setDebugModeAsync(conn, false);

    var msgId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await conn.ExecuteAsync($@"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number, processed_at)
      VALUES ('{msgId}', 'topic', 'Type', '{{}}', '{{}}', 5, 0, NOW(), '{streamId}', 0, NOW())");

    var results = await _runMaintenanceAsync(conn);

    var (_, RowsAffected, _, _) = results.FirstOrDefault(r => r.TaskName == "purge_completed_outbox");
    await Assert.That(RowsAffected).IsGreaterThanOrEqualTo(1);

    var remaining = await conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_outbox WHERE message_id = '{msgId}'");
    await Assert.That(remaining).IsEqualTo(0);
  }

  private static async Task _setDebugModeAsync(NpgsqlConnection conn, bool enabled) {
    var value = enabled ? "true" : "false";
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('debug_mode', @v, 'boolean', 'When true, complete_* functions retain rows for forensics; perform_maintenance must skip purge of completed rows.')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value";
    cmd.Parameters.AddWithValue("v", value);
    await cmd.ExecuteNonQueryAsync();
  }

  // ================================================================
  // Task 8: Recovered dead-letter purge
  //
  // wh_dead_letters had no purge path at all, so every row a consumer ever
  // dead-lettered stayed forever — including rows already marked Recovered,
  // which are finished business. Combined with a recovery loop that writes a
  // NEW row per republish attempt, an unhealthy deployment grew this table
  // without bound. Rows still awaiting a human decision must survive the
  // sweep regardless of age; only settled ones are reclaimable.
  // ================================================================

  private static Task _insertDeadLetterAsync(
      NpgsqlConnection conn, Guid id, int recoveryStatus, string age) =>
    conn.ExecuteAsync($@"
      INSERT INTO wh_dead_letters (
        dead_letter_id, source_table, source_id, message_type, envelope,
        failure_reason, attempts_when_dlq, dead_lettered_at, recovery_status, generation)
      VALUES (
        '{id}', 'wh_inbox', '{Guid.CreateVersion7()}', 'TestEvent', '{{}}'::jsonb,
        17, 3, NOW() - INTERVAL '{age}', {recoveryStatus}, 'test-generation')");

  private static Task<long> _countDeadLetterAsync(NpgsqlConnection conn, Guid id) =>
    conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM wh_dead_letters WHERE dead_letter_id = '{id}'");

  [Test]
  public async Task PerformMaintenance_PurgesRecoveredDeadLetters_PastRetentionAsync() {
    // Arrange — a Recovered row older than the default retention window.
    await using var conn = await _openConnectionAsync();
    var settledId = Guid.CreateVersion7();
    await _insertDeadLetterAsync(conn, settledId, recoveryStatus: 3, age: "60 days");

    // Act
    var results = await _runMaintenanceAsync(conn);

    // Assert — the sweep reports itself and the row is gone.
    var (TaskName, RowsAffected, _, _) = results.FirstOrDefault(r => r.TaskName == "purge_recovered_dead_letters");
    await Assert.That(TaskName).IsEqualTo("purge_recovered_dead_letters");
    await Assert.That(RowsAffected).IsGreaterThanOrEqualTo(1);
    await Assert.That(await _countDeadLetterAsync(conn, settledId)).IsEqualTo(0);
  }

  [Test]
  public async Task PerformMaintenance_PreservesHoldForReviewDeadLetters_RegardlessOfAgeAsync() {
    // Arrange — HoldForReview is a human decision that has not been made yet.
    // Age must never be sufficient reason to discard it.
    await using var conn = await _openConnectionAsync();
    var heldId = Guid.CreateVersion7();
    await _insertDeadLetterAsync(conn, heldId, recoveryStatus: 2, age: "365 days");

    // Act
    await _runMaintenanceAsync(conn);

    // Assert
    await Assert.That(await _countDeadLetterAsync(conn, heldId)).IsEqualTo(1);
  }

  [Test]
  public async Task PerformMaintenance_PreservesPendingAndFailedDeadLetters_RegardlessOfAgeAsync() {
    // Arrange — Pending(0) is unresolved work and PermanentlyFailed(4) is the
    // forensic record of something that never succeeded. Neither is settled.
    await using var conn = await _openConnectionAsync();
    var pendingId = Guid.CreateVersion7();
    var failedId = Guid.CreateVersion7();
    await _insertDeadLetterAsync(conn, pendingId, recoveryStatus: 0, age: "365 days");
    await _insertDeadLetterAsync(conn, failedId, recoveryStatus: 4, age: "365 days");

    // Act
    await _runMaintenanceAsync(conn);

    // Assert
    await Assert.That(await _countDeadLetterAsync(conn, pendingId)).IsEqualTo(1);
    await Assert.That(await _countDeadLetterAsync(conn, failedId)).IsEqualTo(1);
  }

  [Test]
  public async Task PerformMaintenance_PreservesRecentRecoveredDeadLetters_WithinRetentionAsync() {
    // Arrange — a freshly recovered row is still useful for diagnosing the
    // failure it came from, so the window must be honored rather than
    // deleting on status alone.
    await using var conn = await _openConnectionAsync();
    var recentId = Guid.CreateVersion7();
    await _insertDeadLetterAsync(conn, recentId, recoveryStatus: 3, age: "1 hour");

    // Act
    await _runMaintenanceAsync(conn);

    // Assert
    await Assert.That(await _countDeadLetterAsync(conn, recentId)).IsEqualTo(1);
  }

  [Test]
  public async Task PerformMaintenance_RespectsConfigurableDeadLetterRetention_ViaWhSettingsAsync() {
    // Arrange — narrow the window to 1 day; a 2-day-old settled row becomes eligible.
    await using var conn = await _openConnectionAsync();
    await conn.ExecuteAsync(@"
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('dead_letter_retention_days', '1', 'integer', 'test override')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = '1'");

    var id = Guid.CreateVersion7();
    await _insertDeadLetterAsync(conn, id, recoveryStatus: 3, age: "2 days");

    try {
      // Act
      await _runMaintenanceAsync(conn);

      // Assert — 2-day-old settled row is eligible under a 1-day window
      await Assert.That(await _countDeadLetterAsync(conn, id)).IsEqualTo(0);
    } finally {
      await conn.ExecuteAsync(@"
        UPDATE wh_settings SET setting_value = '7'
        WHERE setting_key = 'dead_letter_retention_days'");
    }
  }

}
