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
[Category("Shard4")]
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

    // 114 edge-notify: the 'perspective' doorbell is precise — it rings only when the
    // emit chain actually CREATED work items, which requires an association for the
    // event type. Seeded here; the association-less case has its own lock below.
    await _seedPerspectiveAssociationAsync(conn);

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
      .Because("Burst protection (114 edge-notify): the first row is still pending, so the second store piles behind it — silent, a wake is already owed. Same suppression outcome the cold-only gate bought (the 2026-06-12 bulk regression), now derived from queue state instead of stream age.");
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

    await _seedPerspectiveAssociationAsync(conn);
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
      .Because("Burst protection (114 edge-notify): the first call's outbox row AND its perspective work items are still pending, so the second store piles behind them on both channels — a wake is already owed and the in-flight drain's refetch picks the rows up. This is the bulk-import suppression, preserved by construction.");
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
      .Because("Per-stream edges (114): the busy stream's row piles behind its still-pending first message (silent); the fresh stream's row is its first pending work (rings). Exactly one NOTIFY for the batch.");
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
  // Empty→non-empty edge — the interactive-latency contract (proposal: notify-on-the-true-edge)
  // ============================================================================
  // The v0.686.1 cold-only gate and ClaimWorker's notify-healthy poll relaxation
  // (5 s; bus-wired backstop ~10 s) each assume the other covers HOT (already-
  // pinned) streams. Neither does. Contract under test: the doorbell rings when a
  // store creates a stream's FIRST pending row (the queue's empty→non-empty edge,
  // per category), judged by the SAME predicate the drain fetch uses:
  //   outbox:      processed_at IS NULL AND published_at IS NULL AND schedule-eligible
  //   inbox:       processed_at IS NULL AND schedule-eligible
  //   perspective: wh_perspective_events.processed_at IS NULL
  // Rows piled behind pending work stay silent (bulk protection, same outcome as
  // the cold-only gate); a drained-to-empty stream re-arms the edge, so resume-
  // after-idle rings instantly. The emptiness probe must be a LOCKING read
  // (FOR SHARE) so a concurrent completion cannot produce a stale-read lost wakeup.

  [Test]
  public async Task StoreInboxMessages_DrainedStreamThenStore_FiresNotifyAsync() {
    // The edge resets: once the consumer catches up (row drained), the next store
    // is an empty→non-empty transition again — no matter how long the idle gap.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    await _registerInstanceAsync(conn, instanceA);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var firstMsgId = (Guid)TrackedGuid.NewMedo();
    _ = await _captureNotificationsAsync(conn, [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, $"[{_inboxMessageJson(firstMsgId, streamId)}]"));

    await _markInboxDrainedAsync(conn, firstMsgId);

    var received = await _captureNotificationsAsync(conn, [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, $"[{_inboxMessageJson((Guid)TrackedGuid.NewMedo(), streamId)}]"));

    await Assert.That(received).Count().IsEqualTo(1)
      .Because("A store into a DRAINED (empty-queue) stream must ring the doorbell — the wake condition is queue emptiness, not stream age. Without this, an interactive stream waits on the notify-healthy claim poll (5-10 s) for every hop after its first.");
    await Assert.That(received[0].Payload).IsEqualTo("inbox")
      .Because("The hot-stream wake rides the same 'inbox' doorbell the cold path uses.");
  }

  [Test]
  public async Task StoreOutboxMessages_DrainedStreamThenStore_FiresOutboxAndPerspectiveNotifyAsync() {
    // Outbox variant, asserting BOTH payloads: 'perspective' freshness is the
    // latency the user actually sees (the read model), so a hot-stream wake that
    // only re-armed 'outbox' would still leave visible state poll-paced.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    await _registerInstanceAsync(conn, instanceA);
    await _seedPerspectiveAssociationAsync(conn);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var firstMsgId = (Guid)TrackedGuid.NewMedo();
    _ = await _captureNotificationsAsync(conn, [instanceA],
      emit: async () => await _callStoreOutboxMessagesAsync(conn, instanceA, $"[{_outboxMessageJson(firstMsgId, streamId, isEvent: true)}]"));

    await _markOutboxDrainedAsync(conn, firstMsgId);
    await _markPerspectiveWorkDrainedAsync(conn, streamId);

    var received = await _captureNotificationsAsync(conn, [instanceA],
      emit: async () => await _callStoreOutboxMessagesAsync(conn, instanceA, $"[{_outboxMessageJson((Guid)TrackedGuid.NewMedo(), streamId, isEvent: true)}]"));

    var payloads = received.Select(r => r.Payload).OrderBy(p => p).ToList();
    await Assert.That(payloads).Contains("outbox")
      .Because("A store into a drained stream must re-arm transport pickup immediately.");
    await Assert.That(payloads).Contains("perspective")
      .Because("The perspective doorbell must ring too when the stream's perspective queue was empty — read-model freshness IS the user-visible latency.");
  }

  [Test]
  public async Task StoreInboxMessages_OnlyFutureScheduledRowPending_StoreStillFiresNotifyAsync() {
    // Predicate-mirror lock: a row deferred into the future is NOT drainable now,
    // so it must not count as pending. If the emptiness probe diverged from the
    // drain fetch's eligibility predicate here, a parked retry would silently
    // absorb the doorbell for every later store on the stream.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    await _registerInstanceAsync(conn, instanceA);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var firstMsgId = (Guid)TrackedGuid.NewMedo();
    _ = await _captureNotificationsAsync(conn, [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, $"[{_inboxMessageJson(firstMsgId, streamId)}]"));

    await _deferInboxRowAsync(conn, firstMsgId);

    var received = await _captureNotificationsAsync(conn, [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, $"[{_inboxMessageJson((Guid)TrackedGuid.NewMedo(), streamId)}]"));

    await Assert.That(received).Count().IsEqualTo(1)
      .Because("The emptiness probe must mirror the drain fetch's eligibility predicate: a future-scheduled row is invisible to the drain, so it must be invisible to the probe too — the new (drainable-now) row is the stream's first pending work.");
  }

  [Test]
  public async Task StoreInboxMessages_ProducerNotOwner_NotifiesPinnedOwnerChannelAsync() {
    // Fleet routing lock: the edge changes WHEN the doorbell rings, never WHO
    // receives it. A producer storing into a stream owned by another instance
    // rings the OWNER's channel (notify_instance_owners Step 1), cross-instance.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    var instanceB = new Guid("00000000-0000-0000-0000-00000000000b");
    await _registerInstanceAsync(conn, instanceA);
    await _registerInstanceAsync(conn, instanceB);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var firstMsgId = (Guid)TrackedGuid.NewMedo();
    _ = await _captureNotificationsAsync(conn, [instanceA, instanceB],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, $"[{_inboxMessageJson(firstMsgId, streamId)}]"));
    await _markInboxDrainedAsync(conn, firstMsgId);

    var received = await _captureNotificationsAsync(conn, [instanceA, instanceB],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceB, $"[{_inboxMessageJson((Guid)TrackedGuid.NewMedo(), streamId)}]"));

    await Assert.That(received).Count().IsEqualTo(1)
      .Because("Exactly one doorbell for the drained stream's first new row.");
    await Assert.That(received[0].Channel).IsEqualTo($"wh_work_i_{instanceA}")
      .Because("The stream is pinned to instanceA; the producer (instanceB) rings the pinned owner's channel, not its own — routing is untouched by the edge design.");
  }

  [Test]
  public async Task StoreInboxMessages_BusyStreamDoesNotSuppressDrainedStreamInSameBatchAsync() {
    // Cross-stream independence: emptiness is judged PER STREAM. One busy stream
    // in the batch must not absorb the doorbell owed to a drained stream.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    await _registerInstanceAsync(conn, instanceA);

    var busyStream = (Guid)TrackedGuid.NewMedo();
    var drainedStream = (Guid)TrackedGuid.NewMedo();

    var busyMsg = (Guid)TrackedGuid.NewMedo();
    var drainedMsg = (Guid)TrackedGuid.NewMedo();
    _ = await _captureNotificationsAsync(conn, [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA,
        $"[{_inboxMessageJson(busyMsg, busyStream)},{_inboxMessageJson(drainedMsg, drainedStream)}]"));
    await _markInboxDrainedAsync(conn, drainedMsg);
    // busyMsg stays pending: busyStream is mid-backlog, drainedStream is caught up.

    var received = await _captureNotificationsAsync(conn, [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA,
        $"[{_inboxMessageJson((Guid)TrackedGuid.NewMedo(), busyStream)},{_inboxMessageJson((Guid)TrackedGuid.NewMedo(), drainedStream)}]"));

    await Assert.That(received).Count().IsEqualTo(1)
      .Because("Per-stream edges: the busy stream's new row piles behind pending work (silent), while the drained stream's new row is its first pending row (rings). One doorbell total, for the drained stream only.");
  }

  [Test]
  public async Task StoreInboxMessages_CompletionHeldOpenConcurrently_StoreStillWakesOwnerAsync() {
    // The MVCC knife-edge (lost-wakeup) lock. A completion UPDATE of the stream's
    // last pending row is held OPEN in a second connection's transaction while the
    // store runs. A plain-read probe would see the stale "still pending" version,
    // stay silent, and the parked drain would never learn about the new row. The
    // FOR SHARE probe must block on the completion's row lock, re-evaluate after
    // commit, see the queue empty, and ring. Two-connection shape, same family as
    // the exactly-once schedule-claim tests.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    await using var holderContext = CreateDbContext();
    var holderConn = await _openAsync(holderContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    await _registerInstanceAsync(conn, instanceA);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var firstMsgId = (Guid)TrackedGuid.NewMedo();
    _ = await _captureNotificationsAsync(conn, [instanceA],
      emit: async () => await _callStoreInboxMessagesAsync(conn, instanceA, $"[{_inboxMessageJson(firstMsgId, streamId)}]"));

    // Hold the completion open: the row is being completed but the commit hasn't landed.
    await using var holderTx = await holderConn.BeginTransactionAsync();
    await using (var complete = holderConn.CreateCommand()) {
      complete.Transaction = holderTx;
      complete.CommandText = "UPDATE wh_inbox SET processed_at = NOW() WHERE message_id = @mid";
      complete.Parameters.AddWithValue("mid", firstMsgId);
      _ = await complete.ExecuteNonQueryAsync();
    }

    var received = await _captureNotificationsAsync(conn, [instanceA],
      emit: async () => {
        // The store runs concurrently with the held-open completion. With a locking
        // probe it blocks until the commit below; with a plain read it returns
        // immediately having seen the stale pending row (and the assertion fails).
        var storeTask = Task.Run(async () => {
          await using var storeContext = CreateDbContext();
          var storeConn = await _openAsync(storeContext);
          await _callStoreInboxMessagesAsync(storeConn, instanceA, $"[{_inboxMessageJson((Guid)TrackedGuid.NewMedo(), streamId)}]");
        });

        // Commit the completion once the store is provably parked on the row lock —
        // or immediately if the store already finished (the plain-read failure mode,
        // which the assertion below then catches). DB lock state is the completion
        // signal here, not wall-clock time.
        await using (var lockProbe = holderConn.CreateCommand()) {
          lockProbe.Transaction = holderTx;
          lockProbe.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid WHERE NOT l.granted AND a.query ILIKE '%store_inbox_messages%')";
          while (!storeTask.IsCompleted && !(bool)(await lockProbe.ExecuteScalarAsync() ?? false)) {
            await Task.Yield();
          }
        }
        await holderTx.CommitAsync();
        await storeTask;
      });

    await Assert.That(received).Count().IsEqualTo(1)
      .Because("The emptiness probe must be a LOCKING read (FOR SHARE): serialized against the in-flight completion, it re-evaluates after the commit, sees the queue empty, and rings. A plain read sees the stale pending row and loses the wakeup — the drain's final refetch predates this store's commit, so nothing else would ever ring.");
  }

  [Test]
  public async Task StoreOutboxMessages_EventWithoutAssociations_DoesNotFirePerspectiveNotifyAsync() {
    // The precise perspective contract's storm guard: an event type with NO perspective
    // associations creates no work items, so its perspective queue is permanently empty.
    // A proxy rule ("ring perspective whenever an event lands on an empty perspective
    // queue") would therefore ring on EVERY store for such types — the v0.686 storm on a
    // new channel. The doorbell must ring only when work was actually created.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    await _registerInstanceAsync(conn, instanceA);
    // Deliberately NO association seeded: this event type feeds no perspective. The
    // suite-level seed helper is idempotent per type, so this test uses its own type name.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var json = $"[{_outboxMessageJsonOfType((Guid)TrackedGuid.NewMedo(), streamId, "Test.NoPerspectives, Test")}]";

    var received = await _captureNotificationsAsync(
      conn,
      instancesToListen: [instanceA],
      emit: async () => await _callStoreOutboxMessagesAsync(conn, instanceA, json));

    var payloads = received.Select(r => r.Payload).OrderBy(p => p).ToList();
    await Assert.That(payloads).Contains("outbox")
      .Because("Transport pickup is still owed for the new outbox row.");
    await Assert.That(payloads.Contains("perspective")).IsFalse()
      .Because("No perspective work was created, so a perspective doorbell would be a spurious wake — and for association-less types it would fire on EVERY store, reintroducing the per-store notify storm.");
  }

  private static async Task _seedPerspectiveAssociationAsync(NpgsqlConnection conn) {
    // The emit chain joins wh_message_associations on normalized_message_type with
    // association_type = 'perspective' to create wh_perspective_events work items.
    // Idempotent: concurrent duplicate rows are harmless (uq_perspective_event dedupes
    // the work items), but avoid them anyway.
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_message_associations (message_type, normalized_message_type, association_type, target_name, service_name)
      SELECT 'Test.X, Test', 'Test.X, Test', 'perspective', 'TestPerspective', 'test-svc'
      WHERE NOT EXISTS (
        SELECT 1 FROM wh_message_associations
        WHERE normalized_message_type = 'Test.X, Test'
          AND association_type = 'perspective'
          AND target_name = 'TestPerspective')";
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _markInboxDrainedAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_inbox SET processed_at = NOW() WHERE message_id = @mid";
    cmd.Parameters.AddWithValue("mid", messageId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _markOutboxDrainedAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_outbox SET processed_at = NOW() WHERE message_id = @mid";
    cmd.Parameters.AddWithValue("mid", messageId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _markPerspectiveWorkDrainedAsync(NpgsqlConnection conn, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_perspective_events SET processed_at = NOW() WHERE stream_id = @sid AND processed_at IS NULL";
    cmd.Parameters.AddWithValue("sid", streamId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _deferInboxRowAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_inbox SET scheduled_for = NOW() + INTERVAL '1 hour' WHERE message_id = @mid";
    cmd.Parameters.AddWithValue("mid", messageId);
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
    // These tests lock the emptiness-probe edge semantics, which sit UPSTREAM of the 130
    // doorbell debounce: same-kind repeat notifies within the debounce window are
    // deliberately suppressed in production (the drain linger covers them), which would
    // mask the probe behavior under test. Debounce off — its own contract is locked by
    // NotifyDebounceSqlTests.
    await using (var off = conn.CreateCommand()) {
      off.CommandText = "UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'notify_debounce_seconds'";
      await off.ExecuteNonQueryAsync();
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

  private static string _outboxMessageJsonOfType(Guid messageId, Guid streamId, string messageType) {
    return $$"""
      {
        "MessageId": "{{messageId}}",
        "Destination": "test-topic",
        "MessageType": "{{messageType}}",
        "EnvelopeType": "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
        "Envelope": {"p":1},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": true
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
