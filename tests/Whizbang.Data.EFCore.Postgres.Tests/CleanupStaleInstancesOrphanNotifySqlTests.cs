using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// v0.502 slice B.3 — regression lock for the orphan-redistribution NOTIFY emission added
/// to <c>cleanup_stale_instances</c> (migration 011).
///
/// <para>
/// When a stale instance gets deleted and its leases get released, the function now emits
/// <c>pg_notify('wh_work_i_&lt;live_instance&gt;', 'orphan')</c> for every LIVE instance so
/// they each run a catch-up <c>claim_orphaned_*</c> over the newly-unowned rows. Without
/// this, live instances only discover the released work on their next poll tick — which
/// under the v0.502 NotifyHealthyPollingIntervalMilliseconds=30000 default could be up to
/// 30 seconds away.
/// </para>
///
/// <para><strong>Locked invariants:</strong></para>
/// <list type="bullet">
///   <item><description>Stale-instance cleanup emits exactly one NOTIFY per live instance
///   (deduplication is implicit in the SQL: one row per live instance, one NOTIFY each).</description></item>
///   <item><description>Channel name format is <c>wh_work_i_&lt;live_instance_id&gt;</c>
///   (matches <c>PgWorkNotificationListener.ChannelName</c>).</description></item>
///   <item><description>Payload is literal <c>'orphan'</c> (parsed by the listener into
///   <c>WorkSignalCategory.OrphanRedistribute</c>).</description></item>
///   <item><description>If no instances are stale (nothing to delete), no NOTIFY fires.</description></item>
///   <item><description>If the only remaining instance is the listener itself, it still
///   gets a NOTIFY — orphan redistribution applies to whoever the live listeners are.</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
[Category("Shard1")]
public class CleanupStaleInstancesOrphanNotifySqlTests : EFCoreTestBase {

  [Test]
  public async Task CleanupStaleInstances_NoStale_NoOrphanNotifyAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var live = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, live, lastHeartbeatOffset: TimeSpan.Zero);

    var received = await _captureNotificationsAsync(conn, [live], async () =>
      await _callCleanupAsync(conn, staleCutoff: DateTimeOffset.UtcNow.AddMinutes(-5)));

    await Assert.That(received).IsEmpty()
      .Because("when no instances are stale, no orphan NOTIFY should fire");
  }

  [Test]
  public async Task CleanupStaleInstances_OneStaleOneLive_EmitsOrphanOnLiveChannelAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var live = (Guid)TrackedGuid.NewMedo();
    var dead = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, live, lastHeartbeatOffset: TimeSpan.Zero);
    await _registerInstanceAsync(conn, dead, lastHeartbeatOffset: TimeSpan.FromHours(-1));

    var received = await _captureNotificationsAsync(conn, [live, dead], async () =>
      await _callCleanupAsync(conn, staleCutoff: DateTimeOffset.UtcNow.AddMinutes(-5)));

    await Assert.That(received).Count().IsEqualTo(1)
      .Because("exactly one orphan NOTIFY for the one live instance");
    await Assert.That(received[0].Channel).IsEqualTo($"wh_work_i_{live}")
      .Because("channel naming must match PgWorkNotificationListener.ChannelName");
    await Assert.That(received[0].Payload).IsEqualTo("orphan")
      .Because("payload is parsed by the listener into WorkSignalCategory.OrphanRedistribute");
  }

  [Test]
  public async Task CleanupStaleInstances_MultipleLive_EmitsOrphanOncePerLiveAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var live1 = (Guid)TrackedGuid.NewMedo();
    var live2 = (Guid)TrackedGuid.NewMedo();
    var live3 = (Guid)TrackedGuid.NewMedo();
    var dead = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, live1, lastHeartbeatOffset: TimeSpan.Zero);
    await _registerInstanceAsync(conn, live2, lastHeartbeatOffset: TimeSpan.Zero);
    await _registerInstanceAsync(conn, live3, lastHeartbeatOffset: TimeSpan.Zero);
    await _registerInstanceAsync(conn, dead, lastHeartbeatOffset: TimeSpan.FromHours(-1));

    var received = await _captureNotificationsAsync(conn, [live1, live2, live3, dead], async () =>
      await _callCleanupAsync(conn, staleCutoff: DateTimeOffset.UtcNow.AddMinutes(-5)));

    await Assert.That(received).Count().IsEqualTo(3)
      .Because("one orphan NOTIFY per live instance");
    var channels = received.Select(r => r.Channel).OrderBy(c => c).ToList();
    var expected = new[] { live1, live2, live3 }
      .Select(g => $"wh_work_i_{g}").OrderBy(c => c).ToList();
    await Assert.That(channels).IsEquivalentTo(expected)
      .Because("one NOTIFY per LIVE instance — never on a dead-instance's channel");
  }

  [Test]
  public async Task CleanupStaleInstances_DeadInstancesNeverGetOrphanNotifyAsync() {
    // Even though the dead instance is on the LISTEN list (the test captures both),
    // the cleanup function must only emit to LIVE instance channels. The dead instance
    // is in the process of being removed; emitting to its channel would be wasted work.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var live = (Guid)TrackedGuid.NewMedo();
    var dead = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, live, lastHeartbeatOffset: TimeSpan.Zero);
    await _registerInstanceAsync(conn, dead, lastHeartbeatOffset: TimeSpan.FromHours(-1));

    var received = await _captureNotificationsAsync(conn, [live, dead], async () =>
      await _callCleanupAsync(conn, staleCutoff: DateTimeOffset.UtcNow.AddMinutes(-5)));

    var deadChannels = received.Where(r => r.Channel == $"wh_work_i_{dead}").ToList();
    await Assert.That(deadChannels).IsEmpty()
      .Because("never emit orphan NOTIFY on a dead-instance channel");
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

  private static async Task _callCleanupAsync(NpgsqlConnection conn, DateTimeOffset staleCutoff) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM cleanup_stale_instances(@cutoff)";
    cmd.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz) { Value = staleCutoff });
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* drain */ }
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

      // Force a round-trip so NOTIFY messages buffered after COMMIT are dispatched.
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
