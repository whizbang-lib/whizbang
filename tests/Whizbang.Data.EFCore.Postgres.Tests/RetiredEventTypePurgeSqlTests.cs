// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The purge of retired event types (#2329, migrations 538 and 539): <c>wh_retire_event_types</c> removes every stored
/// event of a type the application deleted, by the type's name, from long-lived mixed streams, a bounded batch of streams
/// at a time. The other types stay, the digests equal a recount of what stays, a gated stream waits whole, and a cursor on
/// a retired event steps back through the hand-on the superseding reaper shares. One test per rule, each with the control
/// that shows the harness sees the other outcome.
/// </summary>
/// <docs>fundamentals/events/retired-event-types</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/539_RetiredEventTypesArePurgedByName.sql</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class RetiredEventTypePurgeSqlTests : EFCoreTestBase {
  private const string RETIRED = "Probe.Contracts.CouponIssued, Probe.Contracts";
  private const string RETIRED_NAME = "Probe.Contracts.CouponIssued";
  private const string KEEP = "Probe.Contracts.OrderPlaced, Probe.Contracts";
  private const string GROUPED_KEEP = "Probe.Contracts.CouponRedeemed, Probe.Contracts";
  private const string GROUP = "coupon";
  private const int SUPERSEDING = 128;

  private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

  // ===== The names =====

  [Test]
  public async Task Names_ResolveToEveryNameTheTypeIsStoredUnder_AndAnUnknownNameToNoneAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await EmitAsync(conn, _t0, (stream, RETIRED, 0, null, null), (stream, RETIRED_NAME + ", Probe.Contracts.Legacy", 0, null, null),
      (stream, KEEP, 0, null, null));

    var rows = await _rowsAsync(conn,
      "SELECT retired_name || '=' || COALESCE(stored_name, '<none>') FROM wh_retired_event_type_names(@n)",
      ("n", new[] { RETIRED_NAME, " Probe.Contracts.CuponIssued ", "" }));

    await Assert.That(rows).IsEquivalentTo([
      "Probe.Contracts.CuponIssued=<none>",
      RETIRED_NAME + "=" + RETIRED,
      RETIRED_NAME + "=" + RETIRED_NAME + ", Probe.Contracts.Legacy",
    ]).Because("a name is matched without its assembly, under every assembly it was stored with; a misspelled one matches nothing");
  }

  // ===== The purge =====

  [Test]
  public async Task Retire_RemovesEveryEventOfTheType_FromAMixedStream_AndTheOtherTypesStayAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await EmitAsync(conn, _t0,
      (stream, KEEP, 0, null, null), (stream, RETIRED, 0, null, null), (stream, KEEP, 0, null, null), (stream, RETIRED, 0, null, null));
    await _stampAsync(conn);

    var result = await _retireAsync(conn);

    await Assert.That(result).IsEqualTo("2|1|0|ok");
    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([ids[0], ids[2]])
      .Because("the retired type leaves the stream and the type that stays is untouched");
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_event_body WHERE event_id = ANY(@ids)", ("ids", ids))).IsEqualTo(2L)
      .Because("a removed event's body goes with it");
  }

  [Test]
  public async Task Retire_UnfoldsTheTypeFromTheDigests_SoTheyEqualARecount_AndRefoldsAndBumpsOnceAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await EmitAsync(conn, _t0,
      (stream, KEEP, 0, null, null), (stream, RETIRED, 0, null, null), (stream, KEEP, 0, null, null), (stream, RETIRED, 0, null, null));
    await _stampAsync(conn);
    await _closeEpochsAsync(conn);
    var keepBefore = await _digestAsync(conn, stream, KEEP);
    var generation = await _generationAsync(conn);

    await _retireAsync(conn);

    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_stream_digests WHERE stream_id = @s AND event_type = @t",
      ("s", stream), ("t", RETIRED))).IsEqualTo(0L).Because("a retired type is counted by no digest");
    await Assert.That(await _digestAsync(conn, stream, KEEP)).IsEqualTo(keepBefore);
    await Assert.That(await _digestsDifferFromARecountAsync(conn, stream)).IsEqualTo(0L);
    await Assert.That(await _generationAsync(conn)).IsEqualTo(generation + 1)
      .Because("a retirement is a rule change: the sealed epochs it reached are refolded and consumers told once");
    await Assert.That(await _scalarAsync(conn, "SELECT epochs_drifted FROM verify_digest_epochs(NOW(), 0, 1000)")).IsEqualTo(0L)
      .Because("the refolded seals hold what stays");

    await _retireAsync(conn);
    await Assert.That(await _generationAsync(conn)).IsEqualTo(generation + 1)
      .Because("a batch that removes nothing moves nothing");
  }

  [Test]
  public async Task Retire_DropsWhatACutCarriedForTheType_AndItsRemovedEpochRowsAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await EmitAsync(conn, _t0,
      (stream, RETIRED, 0, null, null), (stream, KEEP, 0, null, null), (stream, RETIRED, 0, null, null));
    await _stampAsync(conn);
    await _closeEpochsAsync(conn);
    await _executeAsync(conn, "SELECT * FROM wh_truncate_stream_prefix(@s, 1)", ("s", stream));
    await Assert.That(await _scalarAsync(conn,
      "SELECT COALESCE(SUM(carried_count), 0) FROM wh_stream_digests WHERE stream_id = @s AND event_type = @t", ("s", stream), ("t", RETIRED)))
      .IsEqualTo(1L).Because("precondition: the truncate carried the first retired event into the type's bucket");
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_digest_epoch_removed WHERE event_type = @t", ("t", RETIRED)))
      .IsGreaterThan(0L).Because("precondition: and into the epoch it sat in");

    await _retireAsync(conn);

    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_stream_digests WHERE stream_id = @s AND event_type = @t",
      ("s", stream), ("t", RETIRED))).IsEqualTo(0L)
      .Because("a retired type's history is ineligible, what a cut carried for it included");
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_digest_epoch_removed WHERE event_type = @t", ("t", RETIRED)))
      .IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn, "SELECT epochs_drifted FROM verify_digest_epochs(NOW(), 0, 1000)")).IsEqualTo(0L);
  }

  [Test]
  public async Task Retire_TakesTheEventsCompanionRows_AndKeepsTheirDeduplicationAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _associateAsync(conn, RETIRED, "CouponView");
    var ids = await EmitAsync(conn, _t0, (stream, KEEP, 0, null, null), (stream, RETIRED, 0, null, null));
    await _stampAsync(conn);
    var retired = ids[1];
    await _executeAsync(conn, """
      INSERT INTO wh_perspective_applied (event_id, perspective_name, stream_id, applied_at) VALUES (@e, 'CouponView', @s, NOW());
      INSERT INTO wh_lifecycle_completions (event_id, instance_id) VALUES (@e, @i);
      INSERT INTO wh_event_destruction_hold (event_id, hold_until) VALUES (@e, NOW() - INTERVAL '1 hour');
      INSERT INTO wh_message_deduplication (message_id, first_seen_at) VALUES (@e, NOW()) ON CONFLICT DO NOTHING;
      """, ("e", retired), ("s", stream), ("i", (Guid)TrackedGuid.New()));
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_perspective_events WHERE event_id = @e", ("e", retired)))
      .IsEqualTo(1L).Because("precondition: the perspective that consumed the type was queued work for it");

    await Assert.That(await _retireAsync(conn)).IsEqualTo("1|1|0|ok")
      .Because("work no reader can ever apply, and a lapsed hold, do not hold a retired event back");

    foreach (var table in new[] { "wh_perspective_events", "wh_perspective_applied", "wh_lifecycle_completions", "wh_event_destruction_hold" }) {
      await Assert.That(await _scalarAsync(conn, $"SELECT COUNT(*) FROM {table} WHERE event_id = @e", ("e", retired))).IsEqualTo(0L)
        .Because($"{table} rows of a removed event go with it");
    }
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_message_deduplication WHERE message_id = @e", ("e", retired)))
      .IsEqualTo(1L).Because("a resend of a retired event is still recognized as one");
  }

  [Test]
  public async Task Retire_AStreamAGateHolds_WaitsWhole_AndTheOthersAreRetiredAsync() {
    await using var conn = await _openAsync();
    var held = (Guid)TrackedGuid.New();
    var snapshotted = (Guid)TrackedGuid.New();
    var free = (Guid)TrackedGuid.New();
    var unstamped = (Guid)TrackedGuid.New();
    var heldIds = await EmitAsync(conn, _t0, (held, RETIRED, 0, null, null), (held, KEEP, 0, null, null), (held, RETIRED, 0, null, null));
    var snapIds = await EmitAsync(conn, _t0.AddMinutes(1), (snapshotted, RETIRED, 0, null, null), (snapshotted, KEEP, 0, null, null));
    await EmitAsync(conn, _t0.AddMinutes(2), (free, RETIRED, 0, null, null), (free, KEEP, 0, null, null));
    await _stampAsync(conn);
    await EmitAsync(conn, _t0.AddMinutes(3), (unstamped, RETIRED, 0, null, null));
    await _executeAsync(conn, "INSERT INTO wh_event_destruction_hold (event_id, hold_until) VALUES (@e, NOW() + INTERVAL '1 day')",
      ("e", heldIds[2]));
    await _executeAsync(conn, """
      INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, created_at)
      VALUES (@s, 'CouponView', @e, '{}'::jsonb, 1, NOW())
      """, ("s", snapshotted), ("e", snapIds[0]));

    await Assert.That(await _retireAsync(conn)).IsEqualTo("1|1|3|ok (3 streams waiting)");
    await Assert.That(await _storedAsync(conn, held)).IsEquivalentTo(heldIds)
      .Because("a stream leaves whole or waits whole: the unheld retired event of a held stream stays too");
    await Assert.That((await _storedAsync(conn, snapshotted)).Count).IsEqualTo(2);
    await Assert.That((await _storedAsync(conn, unstamped)).Count).IsEqualTo(1)
      .Because("an event the stamper has not reached still owes the stamped order its place");
    await Assert.That((await _storedAsync(conn, free)).Count).IsEqualTo(1);

    await _executeAsync(conn, "UPDATE wh_event_destruction_hold SET hold_until = NOW() - INTERVAL '1 second' WHERE event_id = @e",
      ("e", heldIds[2]));
    await _stampAsync(conn);
    await Assert.That(await _retireAsync(conn)).IsEqualTo("3|2|1|ok (1 streams waiting)")
      .Because("once the hold lapses and the stamper reaches the event, those streams are retired; the snapshot still holds its own");
  }

  [Test]
  public async Task Retire_TakesAtMostTheBatchOfStreams_AndTheNextBatchTakesTheRestAsync() {
    await using var conn = await _openAsync();
    for (var i = 0; i < 3; i++) {
      var stream = (Guid)TrackedGuid.New();
      await EmitAsync(conn, _t0.AddMinutes(i), (stream, RETIRED, 0, null, null), (stream, KEEP, 0, null, null));
    }
    await _stampAsync(conn);

    await Assert.That(await _retireAsync(conn, batchStreams: 2)).IsEqualTo("2|2|0|ok");
    await Assert.That(await _retireAsync(conn, batchStreams: 2)).IsEqualTo("1|1|0|ok");
    await Assert.That(await _retireAsync(conn, batchStreams: 2)).IsEqualTo("0|0|0|ok");
  }

  [Test]
  public async Task Retire_ASupersedingType_UnfoldsEachKeysSlotOnce_AndASlotSharedWithAStayingTypeIsHandedOnAsync() {
    await using var conn = await _openAsync();
    var own = (Guid)TrackedGuid.New();
    var shared = (Guid)TrackedGuid.New();
    // A retired superseding type with two copies of one key, beside a type that stays.
    await EmitAsync(conn, _t0, (own, KEEP, 0, null, null), (own, RETIRED, SUPERSEDING, "k", null));
    await EmitAsync(conn, _t0.AddMinutes(1), (own, RETIRED, SUPERSEDING, "k", null));
    // A group spanning the retired type and one that stays: the retired copy came first, so it anchors the key's slot.
    await EmitAsync(conn, _t0.AddMinutes(2), (shared, RETIRED, SUPERSEDING, "k", GROUP));
    var staying = await EmitAsync(conn, _t0.AddMinutes(3), (shared, GROUPED_KEEP, SUPERSEDING, "k", GROUP));
    await _stampAsync(conn);

    await Assert.That(await _retireAsync(conn)).IsEqualTo("3|2|0|ok");

    await Assert.That(await _digestsDifferFromARecountAsync(conn, own)).IsEqualTo(0L)
      .Because("the key's slot is unfolded once, whichever copies were held");
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_stream_digests WHERE stream_id = @s AND event_type = @t",
      ("s", own), ("t", RETIRED))).IsEqualTo(0L);
    await Assert.That(await _storedAsync(conn, shared)).IsEquivalentTo(staying);
    await Assert.That(await _digestsDifferFromARecountAsync(conn, shared)).IsEqualTo(0L)
      .Because("the key still has a copy, so its slot stays counted, handed on to the copy that stays");
  }

  [Test]
  public async Task Retire_DebugMode_NoNames_OrNothingStored_ChangeNothingAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await EmitAsync(conn, _t0, (stream, RETIRED, 0, null, null));
    await _stampAsync(conn);

    await Assert.That(await _retireAsync(conn, names: [])).IsEqualTo("0|0|0|ok");
    await Assert.That(await _retireAsync(conn, names: ["Probe.Contracts.NeverStored"])).IsEqualTo("0|0|0|ok");
    await _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES ('debug_mode', 'true', 'boolean', 'test')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """);
    await Assert.That(await _retireAsync(conn)).IsEqualTo("0|0|0|skipped (debug_mode=true)");
    await Assert.That((await _storedAsync(conn, stream)).Count).IsEqualTo(1);

    await _executeAsync(conn, "UPDATE wh_settings SET setting_value = 'false' WHERE setting_key = 'debug_mode'");
    await Assert.That(await _retireAsync(conn)).IsEqualTo("1|1|0|ok").Because("control: the same store retires it otherwise");
  }

  // ===== Cursors =====

  [Test]
  public async Task Retire_ACursorOnARetiredEvent_StepsBackToTheEventBeforeIt_AndTheStreamIsPurgedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await EmitAsync(conn, _t0, (stream, KEEP, 0, null, null), (stream, RETIRED, 0, null, null));
    await _stampAsync(conn);
    await _cursorOnAsync(conn, stream, "CouponView", ids[1]);

    await Assert.That(await _retireAsync(conn)).IsEqualTo("1|1|0|ok");
    await Assert.That(await _cursorEventAsync(conn, stream, "CouponView")).IsEqualTo(ids[0])
      .Because("the perspective applied the event before the retired one too, so the cursor rests there");
  }

  [Test]
  public async Task Retire_ACursorWithNoEarlierEventToStepBackTo_KeepsTheStreamWaitingAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await EmitAsync(conn, _t0, (stream, RETIRED, 0, null, null), (stream, KEEP, 0, null, null));
    await _stampAsync(conn);
    await _cursorOnAsync(conn, stream, "CouponView", ids[0]);

    await Assert.That(await _retireAsync(conn)).IsEqualTo("0|0|1|ok (1 streams waiting)");
    await Assert.That(await _cursorEventAsync(conn, stream, "CouponView")).IsEqualTo(ids[0]);
    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo(ids);
  }

  [Test]
  public async Task Retire_ACursorWithOtherWorkPendingBeforeIt_KeepsTheStreamWaitingAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _associateAsync(conn, KEEP, "CouponView");
    var ids = await EmitAsync(conn, _t0, (stream, KEEP, 0, null, null), (stream, RETIRED, 0, null, null));
    await _stampAsync(conn);
    await _cursorOnAsync(conn, stream, "CouponView", ids[1]);
    await Assert.That(await _scalarAsync(conn,
      "SELECT COUNT(*) FROM wh_perspective_events WHERE event_id = @e AND processed_at IS NULL", ("e", ids[0]))).IsEqualTo(1L)
      .Because("precondition: the perspective's work on the earlier event is pending");

    await Assert.That(await _retireAsync(conn)).IsEqualTo("0|0|1|ok (1 streams waiting)");
    await Assert.That(await _cursorEventAsync(conn, stream, "CouponView")).IsEqualTo(ids[1]);

    await _executeAsync(conn, "UPDATE wh_perspective_events SET processed_at = NOW() WHERE event_id = @e", ("e", ids[0]));
    await Assert.That(await _retireAsync(conn)).IsEqualTo("1|1|0|ok").Because("control: once that work is done it steps back");
    await Assert.That(await _cursorEventAsync(conn, stream, "CouponView")).IsEqualTo(ids[0]);
  }

  [Test]
  public async Task Retire_TheStreamsFirstEvent_LeavesItsBusinessTimeOnTheLedgerAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await EmitAsync(conn, _t0, (stream, RETIRED, 0, null, null), (stream, KEEP, 0, null, null));
    await _stampAsync(conn);
    await _executeAsync(conn, "UPDATE wh_stream_lifecycle SET first_event_at = NULL WHERE stream_id = @s", ("s", stream));

    await _retireAsync(conn);

    await Assert.That(await _scalarAsync(conn,
      "SELECT COUNT(*) FROM wh_stream_lifecycle WHERE stream_id = @s AND first_event_at IS NOT NULL", ("s", stream))).IsEqualTo(1L)
      .Because("a rebuild that writes the stream's row afresh still dates it at the stream's start");
  }

  // ===== Helpers =====

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  // Queues the events in the outbox, with the superseding key and group in the metadata as the dispatcher writes them,
  // and runs the outbox emit chain over them as one batch. Ids are ordered, so a later event is also newer by id.
  internal static async Task<Guid[]> EmitAsync(
      NpgsqlConnection conn, DateTimeOffset now, params (Guid Stream, string Type, int Flags, string? Key, string? Group)[] events) {
    Guid[] ids = [.. Enumerable.Range(0, events.Length).Select(_ => (Guid)TrackedGuid.New()).Order()];
    var instanceId = (Guid)TrackedGuid.New();
    for (var i = 0; i < events.Length; i++) {
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'retired-topic', @type, 'env', jsonb_build_object('p', jsonb_build_object('n', 1)),
           jsonb_strip_nulls(jsonb_build_object('sk', @key::text, 'sg', @group::text)), NULL,
           1, 0, @inst, @lease, 0, @sid, true, @flags, @now)
        """,
        ("id", ids[i]), ("type", events[i].Type), ("key", (object?)events[i].Key ?? DBNull.Value),
        ("group", (object?)events[i].Group ?? DBNull.Value), ("inst", instanceId),
        ("lease", now.AddMinutes(5)), ("sid", events[i].Stream), ("flags", events[i].Flags), ("now", now));
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    emit.Parameters.AddWithValue("inst", instanceId);
    emit.Parameters.AddWithValue("lease", now.AddMinutes(5));
    emit.Parameters.AddWithValue(nameof(now), now);
    _ = await emit.ExecuteScalarAsync();
    return ids;
  }

  internal static Task StampAsync(NpgsqlConnection conn) => _stampAsync(conn);

  private static Task _stampAsync(NpgsqlConnection conn) => _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");

  private static async Task<string> _retireAsync(NpgsqlConnection conn, string[]? names = null, int batchStreams = 500) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT events_retired || '|' || streams_retired || '|' || streams_waiting || '|' || status FROM wh_retire_event_types(@n, @b)";
    cmd.Parameters.AddWithValue("n", NpgsqlDbType.Array | NpgsqlDbType.Text, names ?? [RETIRED_NAME]);
    cmd.Parameters.AddWithValue("b", batchStreams);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  // Seals the history: every bucket out of the settle window, epochs two positions wide, closed.
  private static async Task _closeEpochsAsync(NpgsqlConnection conn) {
    await _executeAsync(conn, "UPDATE wh_stream_digests SET updated_at = NOW() - INTERVAL '2 hours'");
    await _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES ('integrity_epoch_width', '2', 'integer', 'test')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """);
    await _executeAsync(conn, "SELECT close_digest_epochs(NOW(), 0, 1000)");
  }

  private static async Task _associateAsync(NpgsqlConnection conn, string eventType, string perspective) =>
    await _executeAsync(conn, """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @t, 'perspective', @p, 'test-service', @t, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """, ("t", eventType), ("p", perspective));

  private static Task _cursorOnAsync(NpgsqlConnection conn, Guid stream, string perspective, Guid eventId) =>
    _executeAsync(conn,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@s, @p, @e, 2, NOW())",
      ("s", stream), ("p", perspective), ("e", eventId));

  private static async Task<Guid> _cursorEventAsync(NpgsqlConnection conn, Guid stream, string perspective) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p";
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("p", perspective);
    return (Guid)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<List<Guid>> _storedAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id FROM wh_event_store WHERE stream_id = @s ORDER BY version";
    cmd.Parameters.AddWithValue("s", stream);
    var rows = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetGuid(0));
    }
    return rows;
  }

  private static async Task<string> _digestAsync(NpgsqlConnection conn, Guid stream, string type) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT digest_lo || '|' || digest_hi || '|' || event_count FROM wh_stream_digests WHERE stream_id = @s AND event_type = @t";
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("t", type);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static Task<long> _generationAsync(NpgsqlConnection conn) =>
    _scalarAsync(conn, "SELECT setting_value::bigint FROM wh_settings WHERE setting_key = 'integrity_origin_generation'");

  /// <summary>
  /// The buckets of the stream whose live part differs from a fresh fold over its events still eligible to count, a
  /// superseding key once as its slot in its first copy's type bucket (#2364), as the cleanup and integrity matrix reads it.
  /// </summary>
  private static Task<long> _digestsDifferFromARecountAsync(NpgsqlConnection conn, Guid stream) =>
    _scalarAsync(conn, """
      WITH eligible AS (
        SELECT COALESCE(es.origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid) AS origin_service_id,
               COALESCE(es.scope->>'t', '') AS scope_tenant,
               CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END AS event_type,
               es.stream_id,
               bit_xor(hashtextextended(x.id::text, 0)) AS digest_lo,
               bit_xor(hashtextextended(x.id::text, 1)) AS digest_hi,
               COUNT(*)::int AS event_count
        FROM wh_event_store es
        CROSS JOIN LATERAL (SELECT CASE WHEN es.supersede_key IS NULL THEN es.event_id
                                        ELSE md5(es.stream_id::text || '|' || COALESCE(es.supersede_group, es.event_type)
                                                 || '|' || es.supersede_key)::uuid END AS id) x
        WHERE es.stream_id = @s
          AND COALESCE(es.flags, 0) & 8 = 0
          AND COALESCE(es.flags, 0) & 32 = 0
          AND NOT (es.supersede_key IS NOT NULL AND EXISTS (
            SELECT 1 FROM wh_event_store o
            WHERE o.stream_id = es.stream_id
              AND COALESCE(o.supersede_group, o.event_type) = COALESCE(es.supersede_group, es.event_type)
              AND o.supersede_key = es.supersede_key
              AND o.event_id <> es.event_id
              AND o.version < es.version))
        GROUP BY 1, 2, 3, 4
      ),
      stored AS (
        SELECT origin_service_id, scope_tenant, event_type, stream_id,
               digest_lo # COALESCE(carried_lo, 0) AS digest_lo, digest_hi # COALESCE(carried_hi, 0) AS digest_hi,
               event_count - COALESCE(carried_count, 0) AS event_count
        FROM wh_stream_digests
        WHERE stream_id = @s)
      SELECT COUNT(*) FROM ((TABLE eligible EXCEPT TABLE stored) UNION ALL (TABLE stored EXCEPT TABLE eligible)) d
      """, ("s", stream));

  private static async Task<List<string>> _rowsAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var rows = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetString(0));
    }
    return rows;
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
