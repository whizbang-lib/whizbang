using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// F2 (Temporal Engine) — RED-first locks for <c>notify_schedules_due()</c> (migration 066).
///
/// <para>Mirrors <c>notify_scheduled_retry_due()</c>: finds Active schedules whose
/// <c>next_fire_at</c> has elapsed and emits one <c>pg_notify('wh_work_i_&lt;owner&gt;', 'schedule')</c>
/// per owning live instance so each instance runs a catch-up claim of its due schedules.</para>
///
/// <para><strong>Locked invariants:</strong> only Active (status=0) + due (<c>next_fire_at &lt;= NOW()</c>)
/// + stream-owned schedules notify; the payload is <c>'schedule'</c> (matches
/// <c>ScheduleDueSignal</c>'s <c>[WireName("schedule")]</c>); one NOTIFY per unique owner.</para>
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public class NotifySchedulesDueSqlTests : EFCoreTestBase {

  [Test]
  public async Task NotifySchedulesDue_DueActiveOwnedSchedule_NotifiesOwnerWithSchedulePayloadAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var owner = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, owner);
    await _upsertActiveStreamAsync(conn, streamId, partitionNumber: 0, owner);
    await _insertScheduleAsync(conn, (Guid)TrackedGuid.NewMedo(), streamId,
      nextFireAt: DateTimeOffset.UtcNow.AddMinutes(-1), status: 0);

    var received = await _captureNotificationsAsync(conn, [owner], async () =>
      await _callNotifySchedulesDueAsync(conn));

    await Assert.That(received).Count().IsEqualTo(1);
    await Assert.That(received[0].Channel).IsEqualTo($"wh_work_i_{owner}");
    await Assert.That(received[0].Payload).IsEqualTo("schedule");
  }

  [Test]
  public async Task NotifySchedulesDue_FutureSchedule_EmitsNothingAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var owner = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, owner);
    await _upsertActiveStreamAsync(conn, streamId, partitionNumber: 0, owner);
    await _insertScheduleAsync(conn, (Guid)TrackedGuid.NewMedo(), streamId,
      nextFireAt: DateTimeOffset.UtcNow.AddHours(1), status: 0);

    var received = await _captureNotificationsAsync(conn, [owner], async () =>
      await _callNotifySchedulesDueAsync(conn));

    await Assert.That(received).Count().IsEqualTo(0)
      .Because("a schedule whose next_fire_at is in the future is not due");
  }

  [Test]
  public async Task NotifySchedulesDue_PausedSchedule_EmitsNothingAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var owner = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, owner);
    await _upsertActiveStreamAsync(conn, streamId, partitionNumber: 0, owner);
    await _insertScheduleAsync(conn, (Guid)TrackedGuid.NewMedo(), streamId,
      nextFireAt: DateTimeOffset.UtcNow.AddMinutes(-1), status: 1);   // Paused

    var received = await _captureNotificationsAsync(conn, [owner], async () =>
      await _callNotifySchedulesDueAsync(conn));

    await Assert.That(received).Count().IsEqualTo(0)
      .Because("a paused schedule must not fire even when its next_fire_at has elapsed");
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

  private static async Task<List<(string Channel, string Payload)>> _captureNotificationsAsync(
      NpgsqlConnection conn, Guid[] ownersToListen, Func<Task> emit) {
    var received = new List<(string, string)>();
    void handler(object sender, NpgsqlNotificationEventArgs args) => received.Add((args.Channel, args.Payload));
    conn.Notification += handler;
    try {
      foreach (var owner in ownersToListen) {
        await using var listen = conn.CreateCommand();
        listen.CommandText = $"LISTEN \"wh_work_i_{owner}\"";
        await listen.ExecuteNonQueryAsync();
      }
      await emit();
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

  private static async Task _callNotifySchedulesDueAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT notify_schedules_due()";
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _insertScheduleAsync(
      NpgsqlConnection conn, Guid scheduleId, Guid streamId, DateTimeOffset nextFireAt, short status) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_schedules
        (schedule_id, stream_id, partition_number, recurrence_kind, next_fire_at, status, event_type)
      VALUES (@sid, @stream, 0, 0, @next, @status, 'TestOccurrence')";
    cmd.Parameters.AddWithValue("sid", scheduleId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.Add(new NpgsqlParameter("next", NpgsqlDbType.TimestampTz) { Value = nextFireAt });
    cmd.Parameters.AddWithValue("status", status);
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
