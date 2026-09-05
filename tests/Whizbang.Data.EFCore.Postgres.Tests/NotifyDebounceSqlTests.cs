using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Locks the SQL half of the doorbell debounce (issue #665): under fan-out load,
/// <c>store_*_messages</c> fired one <c>pg_notify</c> per message — measured at a
/// double-digit share of database CPU during a bulk ingest, nearly all of it redundant
/// because the target instance was already awake and draining. The debounce keys on the
/// TARGET instance: <c>wh_notify_state.last_work_at</c> is a per-instance watermark
/// stamped by <c>claim_work</c> whenever the instance finds work; while the watermark is
/// fresher than the <c>notify_debounce_seconds</c> setting (default 7), a notify to that
/// instance is suppressed and the watermark slides (the suppressed store IS work the
/// drainer's linger poll will find). The C# linger (default 8 s) outlives the window by
/// design — the suppression self-expires before the drainer stops polling, so no sleep
/// handshake is needed.</para>
/// <para>Safety edges locked here: suppression never applies toward a non-live instance
/// (its doorbell must fire so the deterministic re-target path takes over), and a
/// non-positive setting disables suppression entirely (today's behavior).</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/130_NotifyDebounce.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/126_FreshWorkClaimFairness.sql</code-under-test>
[Category("Shard1")]
public class NotifyDebounceSqlTests : EFCoreTestBase {

  [Test]
  public async Task FreshWatermark_SuppressesNotify_AndSlidesTheWatermarkAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var suppressed = (Guid)TrackedGuid.NewMedo();
    var control = (Guid)TrackedGuid.NewMedo();
    var streamA = (Guid)TrackedGuid.NewMedo();
    var streamB = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, suppressed, TimeSpan.Zero);
    await _registerInstanceAsync(conn, control, TimeSpan.Zero);
    await _ownStreamAsync(conn, streamA, suppressed);
    await _ownStreamAsync(conn, streamB, control);
    await _setWatermarkAsync(conn, suppressed, ageSeconds: 2);   // fresh: inside the 7s window
    await _setWatermarkAsync(conn, control, ageSeconds: 600);    // stale: must fire

    var received = await _captureNotificationsAsync(conn, [suppressed, control], async () => {
      await _notifyAsync(conn, "inbox", streamA);   // toward the fresh watermark
      await _notifyAsync(conn, "inbox", streamB);   // toward the stale one — the ordering fence
    });

    // The control notification is the fence: same connection, ordered delivery — when it
    // has arrived, the suppressed one would already be here if it had fired.
    await Assert.That(received.Any(r => r.Channel == $"wh_work_i_{control}")).IsTrue()
      .Because("a stale watermark means the instance may be asleep — the doorbell must ring");
    await Assert.That(received.Any(r => r.Channel == $"wh_work_i_{suppressed}")).IsFalse()
      .Because("a fresh watermark means the target is draining or lingering — every extra "
             + "doorbell to it is the redundant pg_notify load the debounce exists to remove");

    await Assert.That(await _watermarkAgeSecondsAsync(conn, suppressed)).IsLessThan(2)
      .Because("a suppressed store slides the watermark: it IS work, and the drainer's "
             + "linger poll restarting on it is exactly what the slide predicts");
  }

  [Test]
  public async Task StaleWatermark_Fires_AndStampsPredictedWakeAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var inst = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, inst, TimeSpan.Zero);
    await _ownStreamAsync(conn, stream, inst);
    // no watermark row at all — first store after idle

    var received = await _captureNotificationsAsync(conn, [inst], async () =>
      await _notifyAsync(conn, "inbox", stream));

    await Assert.That(received.Count).IsEqualTo(1);
    await Assert.That(await _watermarkAgeSecondsAsync(conn, inst)).IsLessThan(2)
      .Because("firing stamps a predicted-awake watermark, so the burst that follows the "
             + "first store of an idle-to-busy edge is suppressed — one doorbell per edge");
  }

  [Test]
  public async Task DeadInstance_FreshWatermark_StillFiresAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var dead = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, dead, TimeSpan.FromMinutes(-5));  // past heartbeat window
    await _ownStreamAsync(conn, stream, dead);
    await _setWatermarkAsync(conn, dead, ageSeconds: 1);

    var received = await _captureNotificationsAsync(conn, [dead], async () =>
      await _notifyAsync(conn, "inbox", stream));

    await Assert.That(received.Count).IsEqualTo(1)
      .Because("suppression toward a non-live instance strands work behind a corpse's "
             + "watermark — a dead target's doorbell fires so re-targeting machinery engages");
  }

  [Test]
  public async Task DebounceDisabled_NonPositiveSetting_AlwaysFiresAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    await using (var set = conn.CreateCommand()) {
      set.CommandText = @"UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'notify_debounce_seconds'";
      await set.ExecuteNonQueryAsync();
    }
    var inst = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, inst, TimeSpan.Zero);
    await _ownStreamAsync(conn, stream, inst);
    await _setWatermarkAsync(conn, inst, ageSeconds: 1);

    var received = await _captureNotificationsAsync(conn, [inst], async () =>
      await _notifyAsync(conn, "inbox", stream));

    await Assert.That(received.Count).IsEqualTo(1)
      .Because("a non-positive setting is the off switch: exact pre-debounce behavior, "
             + "tunable live from the settings table without a redeploy");
  }

  [Test]
  public async Task DebounceSetting_SeededAtSevenSecondsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT setting_value FROM wh_settings WHERE setting_key = 'notify_debounce_seconds'";
    await Assert.That((string?)await q.ExecuteScalarAsync()).IsEqualTo("7")
      .Because("the SQL window (7 s) must sit inside the C# linger (8 s): the watermark "
             + "self-expires while the drainer is still polling, which is the whole "
             + "no-stranded-message invariant");
  }

  [Test]
  public async Task ClaimWork_FindingWork_StampsTheWatermarkAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var inst = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, inst, TimeSpan.Zero);
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"INSERT INTO wh_outbox
                          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, instance_id, lease_expiry, stream_id, partition_number)
                          VALUES (@msg, 'topic', 'T', '{}', '{}', 1, 0, NOW(), @inst, NOW() + INTERVAL '5 minutes', @sid, 0)";
      cmd.Parameters.AddWithValue("msg", (Guid)TrackedGuid.NewMedo());
      cmd.Parameters.AddWithValue("inst", inst);
      cmd.Parameters.AddWithValue("sid", stream);
      await cmd.ExecuteNonQueryAsync();
    }

    await _claimAsync(conn, inst);

    await Assert.That(await _watermarkAgeSecondsAsync(conn, inst, "outbox")).IsLessThan(2)
      .Because("the stamp rides inside claim_work — zero extra round trips — and it is "
             + "what tells producers this instance is awake and polling");
  }

  [Test]
  public async Task ClaimWork_Empty_DoesNotStampAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var inst = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, inst, TimeSpan.Zero);

    await _claimAsync(conn, inst);

    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT count(*) FROM wh_notify_state WHERE instance_id = @id";
    q.Parameters.AddWithValue("id", inst);
    await Assert.That((long)(await q.ExecuteScalarAsync() ?? 0L)).IsEqualTo(0L)
      .Because("an idle fleet's empty claims must not write — the empty-call short-circuit "
             + "keeps the idle floor at ~1 ms and the debounce must not regress it");
  }

  [Test]
  public async Task FreshWatermark_OfAnotherKind_DoesNotSuppressAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var inst = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, inst, TimeSpan.Zero);
    await _ownStreamAsync(conn, stream, inst);
    await _setWatermarkAsync(conn, inst, ageSeconds: 1, kind: "outbox");  // fresh, WRONG kind

    var received = await _captureNotificationsAsync(conn, [inst], async () =>
      await _notifyAsync(conn, "perspective", stream));

    await Assert.That(received.Count).IsEqualTo(1)
      .Because("the debounce keys per (instance, payload kind): an outbox doorbell's "
             + "freshness must never swallow the perspective doorbell that follows it — "
             + "each kind's consumers earn suppression only from their own kind");
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

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid id, TimeSpan hbOffset) {
    var hb = DateTimeOffset.UtcNow + hbOffset;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, @hb, @hb, '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = EXCLUDED.last_heartbeat_at";
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.Add(new NpgsqlParameter("hb", NpgsqlDbType.TimestampTz) { Value = hb });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _ownStreamAsync(NpgsqlConnection conn, Guid streamId, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at)
                        VALUES (@sid, 0, @inst, NOW())";
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _setWatermarkAsync(NpgsqlConnection conn, Guid instanceId, int ageSeconds, string kind = "inbox") {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"INSERT INTO wh_notify_state (instance_id, payload_kind, last_work_at)
                        VALUES (@id, @kind, NOW() - make_interval(secs => @age))
                        ON CONFLICT (instance_id, payload_kind) DO UPDATE SET last_work_at = EXCLUDED.last_work_at";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("kind", kind);
    cmd.Parameters.AddWithValue("age", ageSeconds);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<double> _watermarkAgeSecondsAsync(NpgsqlConnection conn, Guid instanceId, string kind = "inbox") {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXTRACT(EPOCH FROM (NOW() - last_work_at)) FROM wh_notify_state WHERE instance_id = @id AND payload_kind = @kind";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("kind", kind);
    var v = await cmd.ExecuteScalarAsync();
    return v is null or DBNull ? double.MaxValue : Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _notifyAsync(NpgsqlConnection conn, string payload, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT notify_instance_owners(@p, ARRAY[@sid]::uuid[])";
    cmd.Parameters.AddWithValue("p", payload);
    cmd.Parameters.AddWithValue("sid", streamId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _claimAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_work(@inst, 'test-svc', 'test-host', 1)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* drain */ }
  }

  private static async Task<List<(string Channel, string Payload)>> _captureNotificationsAsync(
      NpgsqlConnection conn, IReadOnlyList<Guid> instances, Func<Task> emit) {
    var received = new List<(string Channel, string Payload)>();
    void handler(object? _, NpgsqlNotificationEventArgs args) {
      received.Add((args.Channel, args.Payload));
    }
    conn.Notification += handler;
    try {
      foreach (var instance in instances) {
        await using var listen = conn.CreateCommand();
        listen.CommandText = $"LISTEN \"wh_work_i_{instance}\"";
        await listen.ExecuteNonQueryAsync();
      }
      await emit();
      await using var ping = conn.CreateCommand();
      ping.CommandText = "SELECT 1";
      _ = await ping.ExecuteScalarAsync();
    } finally {
      conn.Notification -= handler;
      foreach (var instance in instances) {
        await using var unlisten = conn.CreateCommand();
        unlisten.CommandText = $"UNLISTEN \"wh_work_i_{instance}\"";
        await unlisten.ExecuteNonQueryAsync();
      }
    }
    return received;
  }
}
