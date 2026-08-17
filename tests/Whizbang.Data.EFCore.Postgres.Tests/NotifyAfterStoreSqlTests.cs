using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// v0.686 — locks in the design of the cheap NOTIFY emission path used by both
/// <c>notify_instance_owners</c> (migration 045 perf rewrite) and the new store-level
/// NOTIFY calls inside <c>store_inbox_messages</c> / <c>store_outbox_messages</c>
/// (migrations 021 / 020).
///
/// <para>Background — a 2026-06-11 bulk import attempted to re-enable the 30 s safety-net
/// polling cadence on a consumer dev environment. It regressed catastrophically because the v0.685
/// shape of <c>notify_instance_owners</c> cost ~24 ms per call, and the store-level wire-up
/// invoked it ~15k times across a 17k-event bulk import. The fix has two parts:</para>
///
/// <list type="number">
/// <item><description><strong>045 perf rewrite</strong>: early-return Step 2 when every input stream is already pinned in <c>wh_active_streams</c> (the 99 % bulk-import case), and replace the UNION ALL across <c>wh_outbox/wh_inbox/wh_perspective_events</c> with IF/ELSIF dispatch on <c>p_payload</c>.</description></item>
/// <item><description><strong>020 + 021 wire-up</strong>: after the INSERT loop, collect newly-stored stream_ids and PERFORM <c>notify_instance_owners</c> once per category — so consumers wake immediately on the first event-per-stream rather than waiting on the 30 s safety-net poll.</description></item>
/// </list>
///
/// <para>The tests in this file lock both halves into regression coverage so future
/// refactors don't silently revert the perf shape or drop the wire-up.</para>
/// </summary>
public class NotifyAfterStoreSqlTests : EFCoreTestBase {

  // ============================================================================
  // notify_instance_owners — 045 perf rewrite lock-ins
  // ============================================================================

  [Test]
  public async Task NotifyInstanceOwners_AllStreamsPinned_OnlyPinnedOwnerNotifiedAsync() {
    // v0.686 perf invariant: when every input stream is already pinned in
    // wh_active_streams (the bulk-import hot path), Step 2 must NOT emit a
    // deterministic-target notify. Step 1 covers the pinned owner. This locks
    // in the v_unclaimed_streams early-return — the design that lets the store-
    // level NOTIFY calls be safe at high call rates.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    // Three sequential UUIDs: instanceA → rank 0, instanceB → rank 1, instanceC → rank 2.
    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    var instanceB = new Guid("00000000-0000-0000-0000-00000000000b");
    var instanceC = new Guid("00000000-0000-0000-0000-00000000000c");
    await _registerInstanceAsync(conn, instanceA);
    await _registerInstanceAsync(conn, instanceB);
    await _registerInstanceAsync(conn, instanceC);

    var streamId = (Guid)TrackedGuid.NewMedo();
    // partition_number 7 % 3 active = rank 1 = instanceB. If Step 2 ran, instanceB would
    // get a notify even though the stream is pinned to instanceA — wrong.
    const int partitionNumber = 7;
    await _insertOutboxRowAsync(conn, streamId, partitionNumber);
    await _upsertActiveStreamAsync(conn, streamId, partitionNumber, instanceA);

    var received = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA, instanceB, instanceC],
      emit: async () => await _callNotifyInstanceOwnersAsync(conn, "outbox", streamId));

    await Assert.That(received).Count().IsEqualTo(1)
      .Because("Step 2 must short-circuit when every input stream is already pinned. Otherwise the rank-deterministic owner gets an extra (wrong) notify and the function pays the table-scan cost on every call — the 2026-06-11 consumer regression.");
    await Assert.That(received[0].Channel).IsEqualTo($"wh_work_i_{instanceA}")
      .Because("Step 1 must emit to the pinned owner (instanceA) — NOT the partition-modulo deterministic target (instanceB) — because the stream is claimed.");
  }

  [Test]
  public async Task NotifyInstanceOwners_MixedPinnedAndUnclaimed_BothStepsEmitAsync() {
    // The early-return must NOT fire when only SOME input streams are pinned. The
    // unclaimed subset still needs Step 2 to cover it. Locks in v_unclaimed_streams's
    // subset-not-empty semantics.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    var instanceB = new Guid("00000000-0000-0000-0000-00000000000b");
    var instanceC = new Guid("00000000-0000-0000-0000-00000000000c");
    await _registerInstanceAsync(conn, instanceA);
    await _registerInstanceAsync(conn, instanceB);
    await _registerInstanceAsync(conn, instanceC);

    var pinnedStream = (Guid)TrackedGuid.NewMedo();
    await _insertOutboxRowAsync(conn, pinnedStream, partitionNumber: 0);
    await _upsertActiveStreamAsync(conn, pinnedStream, partitionNumber: 0, instanceA);

    var unclaimedStream = (Guid)TrackedGuid.NewMedo();
    // partition 7 % 3 = rank 1 → instanceB.
    await _insertOutboxRowAsync(conn, unclaimedStream, partitionNumber: 7);

    var received = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA, instanceB, instanceC],
      emit: async () => await _callNotifyInstanceOwnersAsync(conn, "outbox", pinnedStream, unclaimedStream));

    var channels = received.Select(r => r.Channel).OrderBy(c => c).ToHashSet();
    await Assert.That(channels).Contains($"wh_work_i_{instanceA}")
      .Because("Step 1 must still emit to the pinned owner.");
    await Assert.That(channels).Contains($"wh_work_i_{instanceB}")
      .Because("Step 2 must emit to the deterministic-target rank for the unclaimed stream.");
    await Assert.That(received).Count().IsEqualTo(2)
      .Because("Exactly two notifies — pinned (Step 1) + unclaimed (Step 2). instanceC must NOT receive.");
  }

  // ============================================================================
  // store_inbox_messages / store_outbox_messages — store-level NOTIFY wire-up
  // ============================================================================

  [Test]
  public async Task StoreInboxMessages_NewStream_FiresInboxNotifyToCallerAsync() {
    // v0.686 wire-up: store_inbox_messages must call notify_instance_owners('inbox', ...)
    // after inserting newly-stored messages. Without this, consumers wait up to the
    // safety-net poll (30 s) before claim_orphaned_inbox discovers the row — the cold-
    // start gap that motivated migration 045 in the first place.
    //
    // Routing: store_inbox_messages UPSERTs the caller (p_instance_id) into
    // wh_active_streams as the stream's owner BEFORE the NOTIFY runs, so Step 1 of
    // notify_instance_owners emits to the caller's channel. This is correct: the
    // caller is the instance that physically stored the row and most often the one
    // that should process it (in a consumer deployment, the caller is the replica that
    // received the transport message). Step 2's partition-modulo deterministic
    // routing is the COLD-START fallback when no instance has yet pinned the
    // stream — once pinned, all subsequent notifies go through Step 1.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    var instanceB = new Guid("00000000-0000-0000-0000-00000000000b");
    var instanceC = new Guid("00000000-0000-0000-0000-00000000000c");
    await _registerInstanceAsync(conn, instanceA);
    await _registerInstanceAsync(conn, instanceB);
    await _registerInstanceAsync(conn, instanceC);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var json = $"[{_inboxMessageJson(msgId, streamId)}]";

    var received = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA, instanceB, instanceC],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, json));

    await Assert.That(received).Count().IsEqualTo(1)
      .Because("store_inbox_messages must emit one NOTIFY per call when a new stream-bearing message lands.");
    await Assert.That(received[0].Channel).IsEqualTo($"wh_work_i_{instanceA}")
      .Because("Caller pins the stream into wh_active_streams BEFORE NOTIFY; Step 1 emits to the pinned owner (the caller).");
    await Assert.That(received[0].Payload).IsEqualTo("inbox")
      .Because("Inbox-store path must emit the 'inbox' payload.");
  }

  [Test]
  public async Task StoreInboxMessages_NullStreamId_NoNotifyAsync() {
    // Safety: messages with no stream_id (rare — local-emit testing paths) must not
    // attempt to look up a partition for NOTIFY routing.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    await _registerInstanceAsync(conn, instanceA);

    var msgId = (Guid)TrackedGuid.NewMedo();
    var json = $"[{_inboxMessageJsonNullStream(msgId)}]";

    var received = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, json));

    await Assert.That(received).Count().IsEqualTo(0)
      .Because("Null-stream messages have no partition → no routing target → no NOTIFY.");
  }

  [Test]
  public async Task StoreOutboxMessages_NewEventStream_FiresOutboxAndPerspectiveNotifyToCallerAsync() {
    // v0.686 wire-up: store_outbox_messages must call notify_instance_owners('outbox', ...)
    // after inserting newly-stored messages, AND notify_instance_owners('perspective', ...)
    // when _emit_event_store_chain ran (i.e., at least one event with stream_id was
    // stored). Without the perspective notify, perspective_events rows created via the
    // local-emit chain wait up to the safety-net poll before PerspectiveEventWorker
    // discovers them.
    //
    // Routing — same as the inbox case: caller pins the stream first, so Step 1 of
    // notify_instance_owners emits to the caller's channel for both payloads.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    var instanceB = new Guid("00000000-0000-0000-0000-00000000000b");
    var instanceC = new Guid("00000000-0000-0000-0000-00000000000c");
    await _registerInstanceAsync(conn, instanceA);
    await _registerInstanceAsync(conn, instanceB);
    await _registerInstanceAsync(conn, instanceC);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var json = $"[{_outboxMessageJson(msgId, streamId, isEvent: true)}]";

    var received = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA, instanceB, instanceC],
      emit: async () => await _callStoreOutboxMessagesAsync(conn, instanceA, json));

    var byChannel = received
      .GroupBy(r => r.Channel)
      .ToDictionary(g => g.Key, g => g.Select(r => r.Payload).OrderBy(p => p).ToList());

    await Assert.That(byChannel.ContainsKey($"wh_work_i_{instanceA}")).IsTrue()
      .Because("Caller (instanceA) pins the stream, so Step 1 routes both notifies to the caller's channel.");
    var payloads = byChannel[$"wh_work_i_{instanceA}"];
    await Assert.That(payloads).Contains("outbox")
      .Because("store_outbox_messages must emit 'outbox' for newly-stored rows.");
    await Assert.That(payloads).Contains("perspective")
      .Because("After _emit_event_store_chain inserts perspective_events for matching perspectives, the chain must emit 'perspective' so PerspectiveEventWorker wakes immediately.");
    await Assert.That(byChannel.ContainsKey($"wh_work_i_{instanceB}")).IsFalse()
      .Because("Step 2 deterministic-target must NOT fire when the caller already pinned the stream — that's the 045 perf early-return.");
  }

  [Test]
  public async Task StoreInboxMessages_SecondCallSameStream_DoesNotFireNotifyAsync() {
    // v0.686.1 conditional-NOTIFY invariant: once a stream is pinned in
    // wh_active_streams (i.e., NOT cold anymore), subsequent store calls for that
    // stream must NOT emit a NOTIFY. The pinned owner's worker will pick up the
    // new rows on its own claim cycle. Skipping the redundant NOTIFY eliminates
    // the per-event NOTIFY storm during bulk imports (17k events on 350 streams
    // → 350 NOTIFYs instead of 17k) while preserving the cold-start latency fix.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    await _registerInstanceAsync(conn, instanceA);

    var streamId = (Guid)TrackedGuid.NewMedo();

    // First call: stream is COLD → NOTIFY must fire (cold-start contract).
    var firstMsgId = (Guid)TrackedGuid.NewMedo();
    var firstJson = $"[{_inboxMessageJson(firstMsgId, streamId)}]";
    var firstReceived = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, firstJson));
    await Assert.That(firstReceived).Count().IsEqualTo(1)
      .Because("First store call for a stream must wake the owner — that's the v0.686 cold-start fix that this conditional design preserves.");

    // Second call (DIFFERENT message_id, SAME stream): stream is now HOT (pinned
    // in wh_active_streams) → NOTIFY must be skipped.
    var secondMsgId = (Guid)TrackedGuid.NewMedo();
    var secondJson = $"[{_inboxMessageJson(secondMsgId, streamId)}]";
    var secondReceived = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, secondJson));
    await Assert.That(secondReceived).IsEmpty()
      .Because("v0.686.1 — subsequent store calls for an already-pinned stream MUST NOT emit a NOTIFY. The pinned owner's worker is already on the case; the NOTIFY storm is what made the bulk-import regress in a consumer deployment (2026-06-12).");
  }

  [Test]
  public async Task StoreOutboxMessages_SecondCallSameStream_DoesNotFireNotifyAsync() {
    // Same invariant as the inbox case, applied to the outbox store path. Locks
    // BOTH the 'outbox' AND 'perspective' NOTIFY payloads to the cold-stream
    // gate — neither should fire on the second store for the same stream.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    await _registerInstanceAsync(conn, instanceA);

    var streamId = (Guid)TrackedGuid.NewMedo();

    // First call (event): wake.
    var firstMsgId = (Guid)TrackedGuid.NewMedo();
    var firstJson = $"[{_outboxMessageJson(firstMsgId, streamId, isEvent: true)}]";
    var firstReceived = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA],
      emit: async () => await _callStoreOutboxMessagesAsync(conn, instanceA, firstJson));
    var firstPayloads = firstReceived.Select(r => r.Payload).OrderBy(p => p).ToList();
    await Assert.That(firstPayloads).Contains("outbox");
    await Assert.That(firstPayloads).Contains("perspective");

    // Second call (event, same stream): hot stream → no NOTIFYs.
    var secondMsgId = (Guid)TrackedGuid.NewMedo();
    var secondJson = $"[{_outboxMessageJson(secondMsgId, streamId, isEvent: true)}]";
    var secondReceived = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA],
      emit: async () => await _callStoreOutboxMessagesAsync(conn, instanceA, secondJson));
    await Assert.That(secondReceived).IsEmpty()
      .Because("v0.686.1 — both outbox AND perspective NOTIFY paths gate on cold-stream pinning. Hot streams skip both payloads; the pinned owner drains naturally on its next claim cycle.");
  }

  [Test]
  public async Task StoreInboxMessages_MixedColdAndHotStreams_NotifiesOnlyColdStreamsAsync() {
    // The cold-stream gate must operate per-stream within a batch: a single
    // store call that includes BOTH a fresh stream AND a pre-pinned stream
    // should emit a NOTIFY for the cold one only. Locks the subset semantics.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    await _registerInstanceAsync(conn, instanceA);

    var hotStream = (Guid)TrackedGuid.NewMedo();
    var coldStream = (Guid)TrackedGuid.NewMedo();

    // Pre-pin hotStream by storing a first message; consume that NOTIFY.
    var preMsgId = (Guid)TrackedGuid.NewMedo();
    var preJson = $"[{_inboxMessageJson(preMsgId, hotStream)}]";
    _ = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, preJson));

    // Now batch a hot-stream message AND a cold-stream message in one call.
    var hotMsg = (Guid)TrackedGuid.NewMedo();
    var coldMsg = (Guid)TrackedGuid.NewMedo();
    var batchJson = $"[{_inboxMessageJson(hotMsg, hotStream)},{_inboxMessageJson(coldMsg, coldStream)}]";
    var received = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, batchJson));

    await Assert.That(received).Count().IsEqualTo(1)
      .Because("v0.686.1 — exactly one NOTIFY for the cold subset of the batch. The hot stream contributes nothing; the cold stream contributes one.");
  }

  [Test]
  public async Task StoreOutboxMessages_NonEventMessage_OnlyOutboxNotifyAsync() {
    // Outbox messages that are NOT events (IsEvent=false) skip the event-store chain
    // and so should NOT emit the 'perspective' notify. Locks in the if-cardinality-gate
    // semantics of the emit chain.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    await _registerInstanceAsync(conn, instanceA);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var json = $"[{_outboxMessageJson(msgId, streamId, isEvent: false)}]";

    var received = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA],
      emit: async () => await _callStoreOutboxMessagesAsync(conn, instanceA, json));

    var payloads = received.Select(r => r.Payload).OrderBy(p => p).ToList();
    await Assert.That(payloads).Contains("outbox")
      .Because("'outbox' notify must fire for non-event messages too — they still need transport pickup.");
    await Assert.That(payloads.Contains("perspective")).IsFalse()
      .Because("Non-event messages do not run _emit_event_store_chain, so 'perspective' notify is wrong here.");
  }

  // ============================================================================
  // Hot-stream notify window — the interactive-latency contract
  // ============================================================================
  // The v0.686.1 cold-only gate and ClaimWorker's notify-healthy poll relaxation
  // (NotifyHealthyPollingIntervalMilliseconds = 5 s; bus-wired backstop ~10 s) each
  // assume the other side covers HOT (already-pinned) streams. Neither does: a hot
  // stream's store emits no NOTIFY, and the relaxed poll is the only wake source —
  // so an interactive stream (idle between hops, someone watching each hop) pays
  // 0-5 s per hop. Bulk import never notices because its claim loop always has
  // work in hand.
  //
  // Contract under test: 'hot_stream_notify_window_ms' in wh_settings bounds
  // hot-stream NOTIFY emission per stream. 0 = notify on every store (the v0.686
  // behavior); the bulk-import protection above (SecondCallSameStream /
  // MixedColdAndHot, whose second store lands *within* any real window) is
  // unaffected because a real window collapses bursts exactly as the cold-only
  // gate did.

  [Test]
  public async Task StoreInboxMessages_HotStreamWindowZero_SecondCallFiresNotifyAsync() {
    // window=0: a hot (pinned) stream's store must STILL wake the owner. Without
    // this, the pinned owner's only wake source is the relaxed notify-healthy
    // claim poll — the interactive-latency gap this window exists to close.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    await _registerInstanceAsync(conn, instanceA);
    await _setHotStreamNotifyWindowMsAsync(conn, 0);

    var streamId = (Guid)TrackedGuid.NewMedo();

    // First call pins the stream (cold contract, consumed here).
    var firstJson = $"[{_inboxMessageJson((Guid)TrackedGuid.NewMedo(), streamId)}]";
    var firstReceived = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, firstJson));
    await Assert.That(firstReceived).Count().IsEqualTo(1)
      .Because("Cold first store must notify — the v0.686 contract is unchanged by the window.");

    // Second call, same (now hot) stream, window=0 → must notify again.
    var secondJson = $"[{_inboxMessageJson((Guid)TrackedGuid.NewMedo(), streamId)}]";
    var secondReceived = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, secondJson));

    await Assert.That(secondReceived).Count().IsEqualTo(1)
      .Because("hot_stream_notify_window_ms=0 must restore per-store NOTIFY for hot streams: an interactive stream otherwise waits on the notify-healthy claim poll (5-10 s) for EVERY hop, because the cold-only gate and the relaxed poll each assume the other covers hot streams.");
    await Assert.That(secondReceived[0].Payload).IsEqualTo("inbox")
      .Because("The hot-stream wake must ride the same 'inbox' doorbell the cold path uses.");
  }

  [Test]
  public async Task StoreOutboxMessages_HotStreamWindowZero_SecondCallFiresOutboxAndPerspectiveNotifyAsync() {
    // Same contract on the outbox path, and it must cover BOTH payloads: the
    // 'perspective' doorbell is what makes the read model (what the user actually
    // sees) refresh promptly, so a hot-stream wake that only re-arms 'outbox'
    // would still leave the visible state stale until the poll.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    await _registerInstanceAsync(conn, instanceA);
    await _setHotStreamNotifyWindowMsAsync(conn, 0);

    var streamId = (Guid)TrackedGuid.NewMedo();

    var firstJson = $"[{_outboxMessageJson((Guid)TrackedGuid.NewMedo(), streamId, isEvent: true)}]";
    _ = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA],
      emit: async () => await _callStoreOutboxMessagesAsync(conn, instanceA, firstJson));

    var secondJson = $"[{_outboxMessageJson((Guid)TrackedGuid.NewMedo(), streamId, isEvent: true)}]";
    var secondReceived = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA],
      emit: async () => await _callStoreOutboxMessagesAsync(conn, instanceA, secondJson));

    var payloads = secondReceived.Select(r => r.Payload).OrderBy(p => p).ToList();
    await Assert.That(payloads).Contains("outbox")
      .Because("window=0: the hot stream's second store must re-arm transport pickup immediately.");
    await Assert.That(payloads).Contains("perspective")
      .Because("window=0: the perspective doorbell must fire too — read-model freshness IS the user-visible latency.");
  }

  private static async Task _setHotStreamNotifyWindowMsAsync(NpgsqlConnection conn, int windowMs) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('hot_stream_notify_window_ms', @val, 'integer', 'test seed')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value";
    cmd.Parameters.AddWithValue("val", windowMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await cmd.ExecuteNonQueryAsync();
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
      NpgsqlConnection conn, IReadOnlyList<Guid> instancesToListen, Func<Task> emit) {
    var received = new List<(string, string)>();
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

  private static async Task _callNotifyInstanceOwnersAsync(
      NpgsqlConnection conn, string payload, params Guid[] streamIds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT notify_instance_owners(@payload, @ids)";
    cmd.Parameters.AddWithValue("payload", payload);
    cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) {
      Value = streamIds
    });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _callStoreInboxMessagesAsync(NpgsqlConnection conn, Guid instanceId, string messagesJson) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM store_inbox_messages(@p_msgs::jsonb, @p_inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    cmd.Parameters.Add(new NpgsqlParameter("p_msgs", NpgsqlDbType.Jsonb) { Value = messagesJson });
    cmd.Parameters.AddWithValue("p_inst", instanceId);
    _ = await cmd.ExecuteScalarAsync();
  }

  private static async Task _callStoreOutboxMessagesAsync(NpgsqlConnection conn, Guid instanceId, string messagesJson) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM store_outbox_messages(@p_msgs::jsonb, @p_inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    cmd.Parameters.Add(new NpgsqlParameter("p_msgs", NpgsqlDbType.Jsonb) { Value = messagesJson });
    cmd.Parameters.AddWithValue("p_inst", instanceId);
    _ = await cmd.ExecuteScalarAsync();
  }

  private static async Task _upsertActiveStreamAsync(NpgsqlConnection conn, Guid streamId, int partitionNumber, Guid? owner) {
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
      Value = (object?)owner ?? DBNull.Value
    });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _insertOutboxRowAsync(NpgsqlConnection conn, Guid streamId, int partitionNumber) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
      VALUES (@mid, 'test-topic', 'TestEvent', '{}', '{}', 0, 0, NOW(), @sid, @part)";
    cmd.Parameters.AddWithValue("mid", (Guid)TrackedGuid.NewMedo());
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("part", partitionNumber);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static string _inboxMessageJson(Guid messageId, Guid streamId) {
    return $$"""
      {
        "MessageId": "{{messageId}}",
        "HandlerName": "TestHandler",
        "EnvelopeType": "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
        "MessageType": "Test.X, Test",
        "Envelope": {"p":1},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": true
      }
      """;
  }

  private static string _inboxMessageJsonNullStream(Guid messageId) {
    return $$"""
      {
        "MessageId": "{{messageId}}",
        "HandlerName": "TestHandler",
        "EnvelopeType": "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
        "MessageType": "Test.X, Test",
        "Envelope": {"p":1},
        "Metadata": {},
        "Scope": null,
        "StreamId": null,
        "IsEvent": false
      }
      """;
  }

  private static string _outboxMessageJson(Guid messageId, Guid streamId, bool isEvent) {
    var isEventLiteral = isEvent ? "true" : "false";
    return $$"""
      {
        "MessageId": "{{messageId}}",
        "Destination": "test-topic",
        "MessageType": "Test.X, Test",
        "EnvelopeType": "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
        "Envelope": {"p":1},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": {{isEventLiteral}}
      }
      """;
  }
}
