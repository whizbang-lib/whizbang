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
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/131_DebounceArmsOnFoundWorkOnly.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/130_NotifyDebounce.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/126_FreshWorkClaimFairness.sql</code-under-test>
[Category("Shard1")]
public class NotifyDebounceSqlTests : EFCoreTestBase {

  [Test]
  public async Task FloodTowardFreshWatermark_SuppressesNotify_AndSlidesTheWatermarkAsync() {
    // Adaptive (137): a fresh watermark alone no longer suppresses — suppression requires a
    // sustained FLOOD toward a draining live target. Here the suppressed target is primed at
    // rapid_run = churn-1 with its last doorbell 30ms ago, so THIS doorbell trips the ceiling.
    // The two-target fence and the slide-on-suppress are preserved.
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
    await _primeRowAsync(conn, suppressed, "inbox", lastWorkAgeSeconds: 2, lastAttemptMsAgo: 30, rapidRun: 4);
    await _setWatermarkAsync(conn, control, ageSeconds: 600);    // stale: must fire

    var received = await _captureNotificationsAsync(conn, [suppressed, control], async () => {
      await _notifyAsync(conn, "inbox", streamA);   // toward the flooded, draining target
      await _notifyAsync(conn, "inbox", streamB);   // toward the stale one — the ordering fence
    });

    // The control notification is the fence: same connection, ordered delivery — when it
    // has arrived, the suppressed one would already be here if it had fired.
    await Assert.That(received.Any(r => r.Channel == $"wh_work_i_{control}")).IsTrue()
      .Because("a stale watermark means the instance may be asleep — the doorbell must ring");
    await Assert.That(received.Any(r => r.Channel == $"wh_work_i_{suppressed}")).IsFalse()
      .Because("a sustained flood toward a draining target is the redundant pg_notify load the "
             + "debounce exists to remove — the linger poll delivers the suppressed store");

    await Assert.That(await _watermarkAgeSecondsAsync(conn, suppressed)).IsLessThan(2)
      .Because("a suppressed store slides the watermark: it IS work, and the drainer's "
             + "linger poll restarting on it is exactly what the slide predicts");
  }

  [Test]
  public async Task StaleWatermark_Fires_WithoutArmingSuppressionAsync() {
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
    // The adaptive controller (137) records the attempt (rate state, for flood detection), but a
    // fire MUST NOT arm suppression: the row it creates carries last_work_at = NULL. Only a claim
    // that finds work may arm it (131/133) — the same condition that arms the C# drain linger.
    // This replaces the old "no row on fire" rule with a type-enforced NULL watermark: a woken-
    // but-empty claim's make-up doorbell can never be swallowed, since NULL fails the freshness
    // gate and cannot suppress (issue #677 part 1).
    var s = await _readNotifyStateAsync(conn, inst);
    await Assert.That(s.LastWorkIsNull).IsTrue()
      .Because("a fire records rate state but must not arm suppression — a NULL watermark can "
             + "never satisfy the freshness gate, so the make-up doorbell is never swallowed");
    await Assert.That(s.FiredCount).IsEqualTo(1L);
  }

  [Test]
  public async Task DoorbellFire_WithNoWorkFoundSince_MustNotSuppressTheFollowUpAsync() {
    // The fenced-commit sequence that motivated the make-up doorbell (118) and broke under
    // the debounce (130): the commit-time doorbell fires and wakes a claim that finds
    // NOTHING (the row is fence-held, pre-visibility), so the drain linger never arms —
    // then the fenced-retry stamp rings the make-up doorbell, the only remaining wake for
    // the now-visible row. If the first fire's stamp suppresses it, nobody is polling
    // tight and visibility falls to the adaptive cap (observed: 10.4 s against a 1.5 s
    // budget). Suppression must therefore arm ONLY on found work — the same condition
    // that arms the linger.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var inst = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, inst, TimeSpan.Zero);
    await _ownStreamAsync(conn, stream, inst);

    // Doorbell #1: the commit-time ring (fires — no watermark yet). The woken claim finds
    // nothing and stamps nothing (locked by ClaimWork_Empty_DoesNotStampAsync).
    var first = await _captureNotificationsAsync(conn, [inst], async () =>
      await _notifyAsync(conn, "perspective", stream));
    await _claimAsync(conn, inst);   // the pre-visibility claim: empty, stamps nothing

    // Doorbell #2: the make-up ring, moments later. No work was found in between, so the
    // linger is not armed and this ring is the only prompt wake — it must fire.
    var second = await _captureNotificationsAsync(conn, [inst], async () =>
      await _notifyAsync(conn, "perspective", stream));

    await Assert.That(first.Count).IsEqualTo(1);
    await Assert.That(second.Count).IsEqualTo(1)
      .Because("no claim found work between the two rings, so nothing armed the drain "
             + "linger — a suppressed make-up doorbell here strands the stamped row on "
             + "the adaptive/backstop cadence (issue #677)");
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
  public async Task ClaimWork_PerspectiveEventWithUnstampedUnderlyingEvent_DoesNotArmWatermarkAsync() {
    // The #677 root cause: a perspective_events row is created Stored at commit BEFORE its
    // underlying wh_event_store event is stamped with a commit_sequence (the per-database
    // ordering fence holds it). claim_work returns the stream, but the fetch gate hides the
    // unstamped event so the drainer makes no progress — arming the perspective watermark for
    // it is a lie that lets the debounce suppress the fence-clearing stamp's make-up ring.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var inst = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, inst, TimeSpan.Zero);
    // Fence-held event: in wh_event_store with commit_sequence NULL (not yet stamped).
    await _insertEventStoreRowAsync(conn, eventId, stream, commitSequenceNull: true);
    // Leased perspective row referencing that unstamped event.
    await _insertPerspectiveEventAsync(conn, stream, eventId, inst);

    await _claimAsync(conn, inst);

    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT count(*) FROM wh_notify_state WHERE instance_id = @id AND payload_kind = 'perspective'";
    q.Parameters.AddWithValue("id", inst);
    await Assert.That((long)(await q.ExecuteScalarAsync() ?? 0L)).IsEqualTo(0L)
      .Because("a claimed-but-undrainable perspective stream (event unstamped) is no drain "
             + "progress; arming the watermark for it strands the make-up doorbell (issue #677)");
  }

  [Test]
  public async Task ClaimWork_PerspectiveEventWithStampedUnderlyingEvent_ArmsWatermarkAsync() {
    // The partner case: once the underlying event IS stamped, the perspective work is genuinely
    // drainable, so finding it is real progress and MUST arm the watermark (the #665 storm
    // protection the debounce provides depends on a busy drainer keeping its watermark fresh).
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var inst = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, inst, TimeSpan.Zero);
    await _insertEventStoreRowAsync(conn, eventId, stream, commitSequenceNull: false);
    await _insertPerspectiveEventAsync(conn, stream, eventId, inst);

    await _claimAsync(conn, inst);

    await Assert.That(await _watermarkAgeSecondsAsync(conn, inst, "perspective")).IsLessThan(2)
      .Because("a drainable perspective stream is real progress — its watermark must arm so "
             + "producers suppress redundant doorbells toward this actively-draining instance");
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

  [Test]
  public async Task SporadicDoorbell_FiresDespiteFreshWatermark_WhenNotInFloodAsync() {
    // Adaptive notify (interactive-latency fix): a LONE doorbell toward a live instance whose
    // found-work watermark is fresh must still FIRE. The pre-adaptive debounce suppressed it —
    // any recent activity (fresh watermark) armed suppression, so a single chat message landing
    // within the window of unrelated prior work was stranded on the drainer's adaptive poll cap
    // (the #677 class: forensic ~10.5s against a ~1.5s budget). Suppression must require a
    // sustained flood, not mere recent draining: one doorbell is not a storm.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var inst = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, inst, TimeSpan.Zero);
    await _ownStreamAsync(conn, stream, inst);
    await _setWatermarkAsync(conn, inst, ageSeconds: 2);   // fresh: drained 2s ago, linger active

    var received = await _captureNotificationsAsync(conn, [inst], async () =>
      await _notifyAsync(conn, "inbox", stream));         // a single, isolated doorbell

    await Assert.That(received.Any(r => r.Channel == $"wh_work_i_{inst}")).IsTrue()
      .Because("one sporadic doorbell is not a flood — it must fire immediately so an "
             + "interactive message is delivered on the doorbell, not quantized to the "
             + "drainer's adaptive poll cap. Only a sustained rapid run may debounce.");
  }

  [Test]
  public async Task FloodRun_EscalatesToCeiling_SuppressesTowardDrainingTargetAsync() {
    // Adaptive notify: a SUSTAINED rapid run toward a live target that is genuinely draining
    // (fresh found-work watermark) escalates the window to the ceiling and debounces — the #665
    // churn win under real fan-out load, preserved. rapid_run reaches notify_churn_run (5) on
    // this doorbell (primed at 4, arriving 30ms after the last — inside the 100ms rapid gap).
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var inst = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, inst, TimeSpan.Zero);
    await _ownStreamAsync(conn, stream, inst);
    await _primeRowAsync(conn, inst, "inbox", lastWorkAgeSeconds: 2, lastAttemptMsAgo: 30, rapidRun: 4);

    var received = await _captureNotificationsAsync(conn, [inst], async () =>
      await _notifyAsync(conn, "inbox", stream));

    await Assert.That(received.Any(r => r.Channel == $"wh_work_i_{inst}")).IsFalse()
      .Because("a sustained rapid run toward a draining live target debounces at the ceiling — "
             + "the linger poll (which outlives the ceiling) delivers the suppressed store");
    var s = await _readNotifyStateAsync(conn, inst);
    await Assert.That(s.RapidRun).IsEqualTo(5);
    await Assert.That(s.SuppressedCount).IsEqualTo(1L);
    await Assert.That(s.EffectiveWindowMs).IsEqualTo(7000)
      .Because("crossing churn escalates the effective window to the ceiling (7s) — the regime gauge");
  }

  [Test]
  public async Task CalmGap_ResetsRapidRun_FiresAndLeavesWatermarkArmedAsync() {
    // A calm gap (wider than the rapid gap) resets the run to 0 even if it was high: the target
    // is no longer flooding, so its doorbell fires at the floor. And a fire must NOT clobber the
    // found-work watermark — only claim_work owns last_work_at.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var inst = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, inst, TimeSpan.Zero);
    await _ownStreamAsync(conn, stream, inst);
    await _primeRowAsync(conn, inst, "inbox", lastWorkAgeSeconds: 2, lastAttemptMsAgo: 5000, rapidRun: 10);

    var received = await _captureNotificationsAsync(conn, [inst], async () =>
      await _notifyAsync(conn, "inbox", stream));

    await Assert.That(received.Any(r => r.Channel == $"wh_work_i_{inst}")).IsTrue()
      .Because("a calm gap means the flood is over — the doorbell fires at the floor again");
    var s = await _readNotifyStateAsync(conn, inst);
    await Assert.That(s.RapidRun).IsEqualTo(0);
    await Assert.That(s.FiredCount).IsEqualTo(1L);
    await Assert.That(s.EffectiveWindowMs).IsEqualTo(50);
    await Assert.That(await _watermarkAgeSecondsAsync(conn, inst)).IsLessThan(4)
      .Because("a fire must NOT reset the found-work watermark — claim_work alone owns last_work_at, "
             + "so it stays ~2s armed, not slid or cleared by the fire");
  }

  [Test]
  public async Task CeilingZero_OffSwitch_AlwaysFires_EvenUnderFloodAsync() {
    // notify_debounce_seconds <= 0 disables suppression entirely — the off switch — regardless
    // of rapid_run or watermark freshness. Preserves 130's off-switch semantics adaptively.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var inst = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, inst, TimeSpan.Zero);
    await _ownStreamAsync(conn, stream, inst);
    await _setSettingAsync(conn, "notify_debounce_seconds", "0");
    await _primeRowAsync(conn, inst, "inbox", lastWorkAgeSeconds: 1, lastAttemptMsAgo: 20, rapidRun: 50);

    var received = await _captureNotificationsAsync(conn, [inst], async () =>
      await _notifyAsync(conn, "inbox", stream));

    await Assert.That(received.Any(r => r.Channel == $"wh_work_i_{inst}")).IsTrue()
      .Because("ceiling <= 0 is the global off switch: suppression is disabled entirely, even "
             + "under a sustained flood toward a draining target");
    var s = await _readNotifyStateAsync(conn, inst);
    await Assert.That(s.EffectiveWindowMs).IsEqualTo(0);
  }

  [Test]
  public async Task FireBornRow_NullWatermark_NeverSelfSuppresses_NoMatterHowRapidAsync() {
    // The #677 part-1 invariant, now type-enforced: a doorbell FIRE records rate state but leaves
    // last_work_at NULL (claim_work alone arms it). So even back-to-back fires (rapid_run climbs)
    // cannot suppress a later doorbell — a NULL watermark can never satisfy the freshness gate.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var inst = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, inst, TimeSpan.Zero);
    await _ownStreamAsync(conn, stream, inst);
    // No prior row and no claim_work arming: the controller must create the row on the fire.

    var first = await _captureNotificationsAsync(conn, [inst], async () =>
      await _notifyAsync(conn, "inbox", stream));
    await Assert.That(first.Any(r => r.Channel == $"wh_work_i_{inst}")).IsTrue();
    var afterFirst = await _readNotifyStateAsync(conn, inst);
    await Assert.That(afterFirst.LastWorkIsNull).IsTrue()
      .Because("a fire must never arm suppression — the fire-born row carries a NULL watermark");

    // Immediately again (no delay): the gap is tiny, so rapid_run climbs past churn — yet with a
    // NULL watermark, suppression is impossible. The doorbell fires.
    var second = await _captureNotificationsAsync(conn, [inst], async () =>
      await _notifyAsync(conn, "inbox", stream));
    await Assert.That(second.Any(r => r.Channel == $"wh_work_i_{inst}")).IsTrue()
      .Because("a fire-born row (NULL last_work_at) can never suppress a later doorbell no matter "
             + "how rapid the run — claim_work must arm the watermark first (#677 part 1)");
    var afterSecond = await _readNotifyStateAsync(conn, inst);
    await Assert.That(afterSecond.LastWorkIsNull).IsTrue();
    await Assert.That(afterSecond.FiredCount).IsEqualTo(2L);
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

  // Prime a wh_notify_state row with an explicit watermark + rate state — no sleeps: ages are
  // set directly. lastWorkAgeSeconds = null means the found-work watermark is NULL (never armed).
  private static async Task _primeRowAsync(NpgsqlConnection conn, Guid inst, string kind,
      int? lastWorkAgeSeconds, int lastAttemptMsAgo, int rapidRun) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_notify_state (instance_id, payload_kind, last_work_at, last_attempt_at, rapid_run)
      VALUES (@id, @kind,
              CASE WHEN @lwNull THEN NULL ELSE NOW() - make_interval(secs => @lwAge) END,
              NOW() - make_interval(secs => @laMs / 1000.0),
              @rr)
      ON CONFLICT (instance_id, payload_kind) DO UPDATE
        SET last_work_at = EXCLUDED.last_work_at,
            last_attempt_at = EXCLUDED.last_attempt_at,
            rapid_run = EXCLUDED.rapid_run";
    cmd.Parameters.AddWithValue("id", inst);
    cmd.Parameters.AddWithValue("kind", kind);
    cmd.Parameters.AddWithValue("lwNull", lastWorkAgeSeconds is null);
    cmd.Parameters.AddWithValue("lwAge", (double)(lastWorkAgeSeconds ?? 0));
    cmd.Parameters.AddWithValue("laMs", (double)lastAttemptMsAgo);
    cmd.Parameters.AddWithValue("rr", rapidRun);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _setSettingAsync(NpgsqlConnection conn, string key, string value) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_settings SET setting_value = @v WHERE setting_key = @k";
    cmd.Parameters.AddWithValue("k", key);
    cmd.Parameters.AddWithValue("v", value);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<(long FiredCount, long SuppressedCount, int RapidRun,
      int EffectiveWindowMs, bool LastWorkIsNull)> _readNotifyStateAsync(
      NpgsqlConnection conn, Guid inst, string kind = "inbox") {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT fired_count, suppressed_count, rapid_run, effective_window_ms,
                               (last_work_at IS NULL)
                        FROM wh_notify_state WHERE instance_id = @id AND payload_kind = @kind";
    cmd.Parameters.AddWithValue("id", inst);
    cmd.Parameters.AddWithValue("kind", kind);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return (0L, 0L, 0, 0, true);
    }
    return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetBoolean(4));
  }

  private static async Task _notifyAsync(NpgsqlConnection conn, string payload, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT notify_instance_owners(@p, ARRAY[@sid]::uuid[])";
    cmd.Parameters.AddWithValue("p", payload);
    cmd.Parameters.AddWithValue("sid", streamId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _insertEventStoreRowAsync(NpgsqlConnection conn, Guid eventId, Guid streamId, bool commitSequenceNull) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = commitSequenceNull
      ? @"INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
          VALUES (@eid, @sid, @sid, 'TestAggregate', 1, 'TestEvent', NULL, NOW())"
      : @"INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at, commit_sequence)
          VALUES (@eid, @sid, @sid, 'TestAggregate', 1, 'TestEvent', NULL, NOW(), nextval('wh_commit_seq'))";
    ins.Parameters.AddWithValue("eid", eventId);
    ins.Parameters.AddWithValue("sid", streamId);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertPerspectiveEventAsync(NpgsqlConnection conn, Guid streamId, Guid eventId, Guid instanceId) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, instance_id, lease_expiry)
      VALUES
        (gen_random_uuid(), @sid, 'TestPerspective', @eid, 0, 1, 0, NOW(), @iid, NOW() + INTERVAL '5 minutes')";
    ins.Parameters.AddWithValue("sid", streamId);
    ins.Parameters.AddWithValue("eid", eventId);
    ins.Parameters.AddWithValue("iid", instanceId);
    await ins.ExecuteNonQueryAsync();
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
