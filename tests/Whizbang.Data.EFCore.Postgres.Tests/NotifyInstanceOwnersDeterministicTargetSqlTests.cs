using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// v0.685 lock-in — <c>notify_instance_owners</c> must deliver a NOTIFY to
/// the deterministic owner (partition-modulo on live instances) when the
/// stream is NOT in <c>wh_active_streams</c>. Without this, first-event-on-
/// new-stream consumers wait up to
/// <c>NotifyHealthyPollingIntervalMilliseconds</c> on the safety-net poll
/// before <c>claim_orphaned_*</c> discovers the row — observed on the
/// 2026-06-11 slot-3 import as a 30 s+ dispatch-to-processing delay on cold
/// start.
///
/// The deterministic target must match the partition-modulo formula that
/// <c>claim_orphaned_*</c> uses: the instance whose
/// <c>ROW_NUMBER() OVER (ORDER BY instance_id) - 1 = partition_number % active_count</c>.
/// So the same instance that WOULD claim the row when its ClaimWorker next
/// ticks is the one that gets the notify — preserving the algorithmic
/// assignment design (no race, no broadcast).
/// </summary>
/// <docs>fundamentals/work-coordinator/notify-instance-owners</docs>
public class NotifyInstanceOwnersDeterministicTargetSqlTests : EFCoreTestBase {

  [Test]
  public async Task NotifyInstanceOwners_UnclaimedStream_NotifiesPartitionModuloOwnerAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    // Sequential UUIDs so the ROW_NUMBER OVER (ORDER BY instance_id) ranks are predictable.
    // instance_a → rank 0, instance_b → rank 1, instance_c → rank 2.
    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    var instanceB = new Guid("00000000-0000-0000-0000-00000000000b");
    var instanceC = new Guid("00000000-0000-0000-0000-00000000000c");
    await _registerInstanceAsync(conn, instanceA, fresh: true);
    await _registerInstanceAsync(conn, instanceB, fresh: true);
    await _registerInstanceAsync(conn, instanceC, fresh: true);

    var streamId = Guid.NewGuid();
    // partition_number = 7 → 7 % 3 = 1 → rank 1 → instance_b is the deterministic owner.
    const int partitionNumber = 7;
    await _insertOutboxRowAsync(conn, streamId: streamId, partitionNumber: partitionNumber);

    // Ensure the stream is NOT in wh_active_streams (i.e. cold-start case).
    await using (var clear = conn.CreateCommand()) {
      clear.CommandText = "DELETE FROM wh_active_streams WHERE stream_id = @sid";
      clear.Parameters.AddWithValue("sid", streamId);
      await clear.ExecuteNonQueryAsync();
    }

    var received = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA, instanceB, instanceC],
      emit: async () => {
        await using var emit = conn.CreateCommand();
        emit.CommandText = "SELECT notify_instance_owners('outbox', ARRAY[@sid]::uuid[])";
        emit.Parameters.AddWithValue("sid", streamId);
        await emit.ExecuteNonQueryAsync();
      });

    await Assert.That(received.Count).IsEqualTo(1)
      .Because("v0.685 — exactly ONE instance MUST receive the notify for an unclaimed stream. Per-instance fan-out (the legacy path) returns zero rows because the stream is not in wh_active_streams; the deterministic-target fallback MUST emit one and only one notify to the partition-modulo owner. Without this fallback, no instance receives a wake and the consumer waits on safety-net poll (slot-3 2026-06-11 observed 30 s+ cold-start latency).");

    var (channel, payload) = received[0];
    await Assert.That(channel).IsEqualTo($"wh_work_i_{instanceB}")
      .Because("partition_number 7 % active_count 3 = rank 1; instance_b is at rank 1 (ROW_NUMBER OVER ORDER BY instance_id) so the deterministic owner is instance_b. Same formula used by claim_orphaned_* — the instance that gets the notify is the same one that would claim the row on the next tick. No race, no broadcast.");
    await Assert.That(payload).IsEqualTo("outbox")
      .Because("Payload pass-through MUST be preserved end-to-end.");
  }

  // --- helpers ---

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId, bool fresh) {
    var hb = fresh ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, @hb, @hb, '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = EXCLUDED.last_heartbeat_at";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.Add(new NpgsqlParameter("hb", NpgsqlDbType.TimestampTz) { Value = hb });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _insertOutboxRowAsync(NpgsqlConnection conn, Guid streamId, int partitionNumber) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
      VALUES (@mid, 'test-topic', 'TestEvent', '{}', '{}', 0, 0, NOW(), @sid, @part)";
    cmd.Parameters.AddWithValue("mid", Guid.NewGuid());
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("part", partitionNumber);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<List<(string Channel, string Payload)>> _captureNotificationsAsync(
      NpgsqlConnection conn,
      IReadOnlyList<Guid> instancesToListen,
      Func<Task> emit) {
    var received = new List<(string Channel, string Payload)>();
    void handler(object? _, NpgsqlNotificationEventArgs args) {
      received.Add((args.Channel, args.Payload));
    }
    conn.Notification += handler;
    try {
      foreach (var instance in instancesToListen) {
        await using var listen = conn.CreateCommand();
        listen.CommandText = $"LISTEN \"wh_work_i_{instance}\"";
        await listen.ExecuteNonQueryAsync();
      }

      await emit();

      // Force a round-trip so NOTIFY messages buffered after the emit are dispatched.
      await using var ping = conn.CreateCommand();
      ping.CommandText = "SELECT 1";
      _ = await ping.ExecuteScalarAsync();
    } finally {
      conn.Notification -= handler;
      foreach (var instance in instancesToListen) {
        await using var unlisten = conn.CreateCommand();
        unlisten.CommandText = $"UNLISTEN \"wh_work_i_{instance}\"";
        await unlisten.ExecuteNonQueryAsync();
      }
    }
    return received;
  }
}
