// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A cut (truncate, close the books, archive, purge, end of life) carries the digest of what it removes instead of
/// subtracting it (#2363). The stream's bucket keeps its comparable digest and records the removed part as carried; the
/// sealed epoch the removed events sat in keeps its seal and records the removed part beside it. Nothing is refolded, no
/// seal moves and the origin generation stays where it is, so a service that cut its history and one that did not still
/// answer alike. A cut waits until the history it removes is sealed, and a purged stream refuses to be restored by a
/// redelivery.
/// </summary>
/// <docs>resilience/stream-integrity#cuts-carry-their-digest</docs>
[Category("Integration")]
[Category("Shard2")]
public class CutsCarryTheirDigestSqlTests : EFCoreTestBase {
  private const string TYPE = "Whizbang.Tests.CutEvent, Whizbang.Tests";
  private const string ZERO = "00000000-0000-0000-0000-000000000000";

  // ===== truncate =====

  [Test]
  public async Task Truncate_CarriesWhatItRemoves_TheDigestStaysAndNothingIsRefoldedOrBumpedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _emitAsync(conn, stream, 5);
    await _sealAsync(conn, width: 2);
    var before = await _bucketAsync(conn, stream);
    var epochsBefore = await _epochsAsync(conn);
    var generation = await _generationAsync(conn);

    var status = await _truncateAsync(conn, stream, await _versionOfAsync(conn, ids[1]));

    await Assert.That(status).IsEqualTo("truncated");
    var after = await _bucketAsync(conn, stream);
    await Assert.That((after.Lo, after.Hi, after.Count)).IsEqualTo((before.Lo, before.Hi, before.Count))
      .Because("the comparable digest is the live part plus the carried part, which is what it was before the cut");
    var removed = await _foldAsync(conn, ids[0], ids[1]);
    await Assert.That((after.CarriedLo, after.CarriedHi, after.CarriedCount)).IsEqualTo((removed.Lo, removed.Hi, 2))
      .Because("the two removed events are carried by the stream's bucket");
    await Assert.That(await _epochsAsync(conn)).IsEqualTo(epochsBefore)
      .Because("no sealed epoch is refolded by a cut");
    await Assert.That(await _generationAsync(conn)).IsEqualTo(generation)
      .Because("a cut rewrites nothing a consumer verified, so the origin generation does not move");
    await Assert.That(await _removedAsync(conn)).IsEqualTo((removed.Lo, removed.Hi, 2L))
      .Because("the sealed epochs the removed events sat in record them as removed, beside their seal");
  }

  [Test]
  public async Task Truncate_WritesTheStreamsFloorAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _emitAsync(conn, stream, 5);
    await _sealAsync(conn, width: 2);
    var through = await _versionOfAsync(conn, ids[1]);

    await _truncateAsync(conn, stream, through);

    await Assert.That(await _scalarAsync(conn,
      "SELECT truncated_through_version FROM wh_stream_lifecycle WHERE stream_id = @s", ("s", stream)))
      .IsEqualTo(through)
      .Because("the floor below which nothing of the stream is held here, and below which no repair is asked or stored");
  }

  [Test]
  public async Task Truncate_OfHistoryAboveTheSeal_WaitsForIt_WhenTheGateIsOn_AndProceedsWhenItIsOffAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _emitAsync(conn, stream, 5);
    var through = await _versionOfAsync(conn, ids[1]);
    await _setAsync(conn, "integrity_cut_waits_for_seal", "true", "boolean");

    await Assert.That(await _truncateAsync(conn, stream, through)).IsEqualTo(Whizbang.Core.Archival.ArchiveStatus.AWAITING_SEAL)
      .Because("nothing is sealed yet, and a cut removes only history this service has sealed");
    await Assert.That(await _countAsync(conn, stream)).IsEqualTo(5L).Because("a cut that waits removes nothing");

    await _sealAsync(conn, width: 2);
    await Assert.That(await _truncateAsync(conn, stream, through)).IsEqualTo("truncated")
      .Because("once the history is sealed the same cut proceeds");

    var other = (Guid)TrackedGuid.New();
    var more = await _emitAsync(conn, other, 3);
    await _setAsync(conn, "integrity_cut_waits_for_seal", "false", "boolean");
    await Assert.That(await _truncateAsync(conn, other, await _versionOfAsync(conn, more[0]))).IsEqualTo("truncated")
      .Because("with sealing off nothing is sealed, so the gate is off too and a cut never waits forever");
  }

  [Test]
  public async Task Truncate_OfReceivedHistory_WaitsForTheConsumersSeal_WhenItAuditsThatOriginAsync() {
    await using var conn = await _openAsync();
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _receiveAsync(conn, origin, stream, 5);
    await _sealAsync(conn, width: 2);
    await _setAsync(conn, "integrity_cut_waits_for_seal", "true", "boolean");
    await _executeAsync(conn,
      "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) VALUES (@o, 1, NOW())", ("o", origin));
    var through = await _versionOfAsync(conn, ids[1]);

    await Assert.That(await _truncateAsync(conn, stream, through)).IsEqualTo(Whizbang.Core.Archival.ArchiveStatus.AWAITING_SEAL)
      .Because("this service has verified the origin's history only through position 1, and the cut reaches 2");

    await _executeAsync(conn, "UPDATE wh_integrity_seals SET sealed_through = 3 WHERE origin_service_id = @o", ("o", origin));
    await Assert.That(await _truncateAsync(conn, stream, through)).IsEqualTo("truncated")
      .Because("verified with the origin and sealed here: the cut proceeds");
  }

  [Test]
  public async Task Truncate_OfLocalHistory_WaitsForEveryConsumersReportedSeal_OnlyWhenThatSettingIsOnAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _emitAsync(conn, stream, 5);
    await _sealAsync(conn, width: 2);
    await _setAsync(conn, "integrity_cut_waits_for_seal", "true", "boolean");
    await _setAsync(conn, "integrity_cut_waits_for_consumers", "true", "boolean");
    await _executeAsync(conn,
      "SELECT wh_record_consumer_seal('consumer-a', 1, NOW())");
    var through = await _versionOfAsync(conn, ids[1]);

    await Assert.That(await _truncateAsync(conn, stream, through)).IsEqualTo(Whizbang.Core.Archival.ArchiveStatus.AWAITING_SEAL)
      .Because("a consumer has verified this history only through position 1, and the setting waits for it");

    await _executeAsync(conn, "SELECT wh_record_consumer_seal('consumer-a', 4, NOW())");
    await Assert.That(await _truncateAsync(conn, stream, through)).IsEqualTo("truncated")
      .Because("every consumer that reported has verified past the cut");
  }

  // ===== the answers and the checks see the cut as nothing =====

  [Test]
  public async Task TypeAnswer_IsTheSameBeforeAndAfterACut_SealedOrOpenAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _emitAsync(conn, stream, 5);
    await _sealAsync(conn, width: 2);
    var before = await _typeAnswerAsync(conn);

    await _truncateAsync(conn, stream, await _versionOfAsync(conn, ids[1]));
    await Assert.That(await _typeAnswerAsync(conn)).IsEqualTo(before)
      .Because("the sealed epochs keep the removed events in their seal");

    var other = (Guid)TrackedGuid.New();
    var open = await _emitAsync(conn, other, 3);
    var withOpen = await _typeAnswerAsync(conn);
    await _setAsync(conn, "integrity_cut_waits_for_seal", "false", "boolean");
    await _truncateAsync(conn, other, await _versionOfAsync(conn, open[0]));
    await Assert.That(await _typeAnswerAsync(conn)).IsEqualTo(withOpen)
      .Because("a cut above the frontier (allowed with the gate off) is carried into the open window's answer too");
  }

  [Test]
  public async Task Heal_FindsACarriedBucketTrue_AndStillFindsAnUnaccountedLossAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _emitAsync(conn, stream, 5);
    await _sealAsync(conn, width: 2);
    await _truncateAsync(conn, stream, await _versionOfAsync(conn, ids[1]));

    await Assert.That(await _healAsync(conn)).IsEqualTo(0L)
      .Because("live events plus the carried part equal the stored digest: no drift");

    await _executeAsync(conn, "DELETE FROM wh_event_body WHERE event_id = @id", ("id", ids[4]));
    await _executeAsync(conn, "DELETE FROM wh_event_store WHERE event_id = @id", ("id", ids[4]));
    await Assert.That(await _healAsync(conn)).IsEqualTo(1L)
      .Because("positive control: an event lost without being carried is drift");
  }

  [Test]
  public async Task Heal_ABucketWhoseWholeStreamWasCut_IsKept_NotReportedAsAPhantomAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitAsync(conn, stream, 3);
    await _emitAsync(conn, (Guid)TrackedGuid.New(), 4);
    await _sealAsync(conn, width: 2);
    await _archiveAsync(conn, stream, null);

    await Assert.That(await _healAsync(conn)).IsEqualTo(0L)
      .Because("a bucket with no live events but a carried part is the stream's digest, not a phantom");
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_stream_digests WHERE stream_id = @s", ("s", stream)))
      .IsEqualTo(1L);
  }

  [Test]
  public async Task EpochRecheck_AfterACut_FindsNoDrift_AndStillFindsADamagedSealAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _emitAsync(conn, stream, 5);
    await _sealAsync(conn, width: 2);
    await _truncateAsync(conn, stream, await _versionOfAsync(conn, ids[1]));

    var (checkedEpochs, drifted) = await _verifyEpochsAsync(conn);
    await Assert.That(checkedEpochs).IsGreaterThan(0);
    await Assert.That(drifted).IsEqualTo(0)
      .Because("the survivors plus the removed part recompute to the seal");

    await _executeAsync(conn, "UPDATE wh_digest_epochs SET digest_lo = digest_lo # 1 WHERE epoch_id = 0");
    await _executeAsync(conn, "UPDATE wh_digest_epoch_seals SET rechecked_at = NULL");
    await Assert.That((await _verifyEpochsAsync(conn)).Drifted).IsEqualTo(1)
      .Because("positive control: a damaged seal is still found");
  }

  [Test]
  public async Task Refold_OfAnEpochACutTouched_KeepsTheRemovedPartInTheSealAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _emitAsync(conn, stream, 5);
    await _sealAsync(conn, width: 2);
    var before = await _epochsAsync(conn);
    await _truncateAsync(conn, stream, await _versionOfAsync(conn, ids[1]));

    await _executeAsync(conn, "SELECT refold_digest_epochs(@l::uuid, 0, 1)", ("l", ZERO));
    await Assert.That(await _epochsAsync(conn)).IsEqualTo(before)
      .Because("a refold folds the survivors and the removed part, so it rewrites the seal it found");
  }

  // ===== archive and restore =====

  [Test]
  public async Task ArchiveWhole_KeepsTheBucketCarried_AndARestoreTakesItBack_NothingBumpedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _emitAsync(conn, stream, 3);
    await _emitAsync(conn, (Guid)TrackedGuid.New(), 4);
    await _sealAsync(conn, width: 2);
    var before = await _bucketAsync(conn, stream);
    var generation = await _generationAsync(conn);
    var epochs = await _epochsAsync(conn);

    await _archiveAsync(conn, stream, null);
    var archived = await _bucketAsync(conn, stream);
    await Assert.That((archived.Lo, archived.Hi, archived.Count)).IsEqualTo((before.Lo, before.Hi, before.Count));
    await Assert.That(archived.CarriedCount).IsEqualTo(3).Because("the whole stream is carried");

    await _executeAsync(conn, "SELECT * FROM wh_restore_archived_stream(@s)", ("s", stream));
    var restored = await _bucketAsync(conn, stream);
    await Assert.That((restored.Lo, restored.Hi, restored.Count, restored.CarriedCount))
      .IsEqualTo((before.Lo, before.Hi, before.Count, 0))
      .Because("the restored events are live again, and nothing of them is carried");
    await Assert.That(await _removedAsync(conn)).IsEqualTo((0L, 0L, 0L))
      .Because("nothing is removed from any epoch once the stream is back");
    await Assert.That(await _epochsAsync(conn)).IsEqualTo(epochs);
    await Assert.That(await _generationAsync(conn)).IsEqualTo(generation)
      .Because("neither the archive nor the restore changed anything a consumer verified");
    _ = ids;
  }

  [Test]
  public async Task Restore_OfAStreamArchivedBeforeCutsCarried_FoldsItBackAsBeforeAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitAsync(conn, stream, 3);
    await _sealAsync(conn, width: 2);
    var before = await _bucketAsync(conn, stream);
    await _archiveAsync(conn, stream, null);
    // An archive written by an earlier release removed the stream's buckets outright and carried nothing.
    await _executeAsync(conn, "DELETE FROM wh_stream_digests WHERE stream_id = @s", ("s", stream));
    await _executeAsync(conn, "DELETE FROM wh_digest_epoch_removed");

    await _executeAsync(conn, "SELECT * FROM wh_restore_archived_stream(@s)", ("s", stream));
    var restored = await _bucketAsync(conn, stream);
    await Assert.That((restored.Lo, restored.Hi, restored.Count)).IsEqualTo((before.Lo, before.Hi, before.Count))
      .Because("with nothing carried, the restored events fold back in, as before");
  }

  // ===== purge =====

  [Test]
  public async Task Purge_KeepsTheBucketCarried_AndARedeliveryOfThePurgedStreamIsRefusedAsync() {
    await using var conn = await _openAsync();
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _receiveAsync(conn, origin, stream, 3);
    await _sealAsync(conn, width: 2);
    var before = await _bucketAsync(conn, stream, origin.ToString());

    await _executeAsync(conn, "SELECT * FROM wh_purge_streams(@s, false, @p, 0, 'test', 'test')",
      ("s", new[] { stream }), ("p", (Guid)TrackedGuid.New()));
    var purged = await _bucketAsync(conn, stream, origin.ToString());
    await Assert.That((purged.Lo, purged.Hi, purged.Count, purged.CarriedCount))
      .IsEqualTo((before.Lo, before.Hi, before.Count, 3))
      .Because("the purged stream's digest stays, carried, so the service still answers what it received");

    await _storeInboxAsync(conn, origin, stream, ids[0], restoreIfMissing: true);
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_event_store WHERE event_id = @id", ("id", ids[0])))
      .IsEqualTo(0L).Because("a redelivery never restores a purged stream");
    await Assert.That(await _textAsync(conn, "SELECT error FROM wh_inbox_state WHERE message_id = @id", ("id", ids[0])))
      .IsEqualTo("repair refused: stream purged")
      .Because("the refusal is recorded so the redelivery's reconciliation reads it");
  }

  // ===== the resting period =====

  [Test]
  public async Task CarriedDetail_PastItsRestingPeriod_RollsOff_AndTheHealThenLeavesTheBucketAloneAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var fresh = (Guid)TrackedGuid.New();
    var ids = await _emitAsync(conn, stream, 5);
    var freshIds = await _emitAsync(conn, fresh, 5);
    await _sealAsync(conn, width: 2);
    await _truncateAsync(conn, stream, await _versionOfAsync(conn, ids[1]));
    await _truncateAsync(conn, fresh, await _versionOfAsync(conn, freshIds[1]));
    await _executeAsync(conn, "UPDATE wh_stream_digests SET carried_at = NOW() - INTERVAL '15 days' WHERE stream_id = @s", ("s", stream));
    var before = await _bucketAsync(conn, stream);

    await Assert.That(await _scalarAsync(conn, "SELECT wh_roll_off_carried_detail(INTERVAL '14 days', 100)")).IsEqualTo(1L)
      .Because("only the detail past its resting period rolls off");
    var rolled = await _bucketAsync(conn, stream);
    await Assert.That((rolled.Lo, rolled.Hi, rolled.Count)).IsEqualTo((before.Lo, before.Hi, before.Count))
      .Because("the comparable digest stays: the stream still answers as it did");
    await Assert.That(await _scalarAsync(conn,
      "SELECT COUNT(*) FROM wh_stream_digests WHERE stream_id = @s AND carried_lo IS NULL", ("s", stream))).IsEqualTo(1L)
      .Because("the per-stream detail is gone; the epoch keeps the removed part");
    await Assert.That((await _bucketAsync(conn, fresh)).CarriedCount).IsEqualTo(2)
      .Because("a cut inside its resting period keeps its detail");
    await Assert.That(await _healAsync(conn)).IsEqualTo(0L)
      .Because("a bucket whose detail rolled off cannot be recomputed from its events and is left alone");
  }

  // ===== helpers =====

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>Stores <paramref name="n"/> events of one local stream through the outbox emit chain and stamps them.</summary>
  private static async Task<Guid[]> _emitAsync(NpgsqlConnection conn, Guid stream, int n) {
    Guid[] ids = [.. Enumerable.Range(0, n).Select(_ => (Guid)TrackedGuid.New()).Order()];
    var instance = (Guid)TrackedGuid.New();
    foreach (var id in ids) {
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES (@id, 'cut-topic', @type, 'env', '{"p":{}}'::jsonb, '{}'::jsonb, NULL,
                1, 0, @inst, NOW() + INTERVAL '5 minutes', 0, @sid, true, 0, NOW() - INTERVAL '1 hour')
        """, ("id", id), ("type", TYPE), ("inst", instance), ("sid", stream));
    }
    await using (var emit = conn.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, NOW() + INTERVAL '5 minutes', NOW() - INTERVAL '1 hour', 10000)";
      emit.Parameters.AddWithValue("ids", ids);
      emit.Parameters.AddWithValue("inst", instance);
      _ = await emit.ExecuteScalarAsync();
    }
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    await _executeAsync(conn, "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '1 hour' WHERE event_id = ANY(@ids)", ("ids", ids));
    return ids;
  }

  /// <summary>Stores <paramref name="n"/> events received from an origin, at its positions 1..n, through the inbox.</summary>
  private static async Task<Guid[]> _receiveAsync(NpgsqlConnection conn, Guid origin, Guid stream, int n) {
    Guid[] ids = [.. Enumerable.Range(0, n).Select(_ => (Guid)TrackedGuid.New()).Order()];
    for (var i = 0; i < n; i++) {
      await _storeInboxAsync(conn, origin, stream, ids[i], restoreIfMissing: false, sequence: i + 1);
    }
    var instance = (Guid)TrackedGuid.New();
    await _executeAsync(conn,
      "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)",
      ("inst", instance), ("ids", ids));
    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW() - INTERVAL '1 hour', 4)",
      ("inst", instance));
    await _executeAsync(conn, "DELETE FROM wh_inbox_state WHERE message_id = ANY(@ids)", ("ids", ids));
    await _executeAsync(conn, "DELETE FROM wh_inbox WHERE message_id = ANY(@ids)", ("ids", ids));
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    await _executeAsync(conn, "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '1 hour' WHERE event_id = ANY(@ids)", ("ids", ids));
    return ids;
  }

  private static Task _storeInboxAsync(NpgsqlConnection conn, Guid origin, Guid stream, Guid id, bool restoreIfMissing, int sequence = 1) =>
    _executeAsync(conn, """
      SELECT * FROM store_inbox_messages(
        jsonb_build_array(jsonb_build_object(
          'MessageId', @id::text, 'HandlerName', 'CutHandler', 'MessageType', @type::text,
          'Envelope', jsonb_build_object('MessageId', @id::text, 'Payload', '{}'::jsonb, 'Hops', '[]'::jsonb),
          'EnvelopeType', 'env', 'StreamId', @sid::text, 'IsEvent', true, 'Flags', 0,
          'Metadata', '{}'::jsonb, 'SourceServiceId', @o::text, 'SourceCommitSequence', @seq,
          'RestoreIfMissing', @restore)),
        @inst, NOW() + INTERVAL '5 minutes', NOW(), 100)
      """, ("id", id), ("type", TYPE), ("sid", stream), ("o", origin), ("seq", sequence), ("restore", restoreIfMissing),
        ("inst", (Guid)TrackedGuid.New()));

  private static async Task _sealAsync(NpgsqlConnection conn, int width) {
    await _setAsync(conn, "integrity_epoch_width", width.ToString(CultureInfo.InvariantCulture), "integer");
    await _executeAsync(conn, "UPDATE wh_stream_digests SET updated_at = NOW() - INTERVAL '2 hours'");
    await _executeAsync(conn, "SELECT close_digest_epochs(NOW(), 0, 1000)");
  }

  private static Task _setAsync(NpgsqlConnection conn, string key, string value, string type) =>
    _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES (@k, @v, @t, 'test')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """, ("k", key), ("v", value), ("t", type));

  private static async Task<string> _truncateAsync(NpgsqlConnection conn, Guid stream, long through) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT truncate_status FROM wh_truncate_stream_prefix(@s, @v)";
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("v", through);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _archiveAsync(NpgsqlConnection conn, Guid stream, long? through) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT archive_status FROM wh_archive_stream(@s, @v, 'test://archive')";
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)through ?? DBNull.Value });
    var status = (string)(await cmd.ExecuteScalarAsync())!;
    if (status != "archived") {
      throw new InvalidOperationException($"the setup's archive did not run: {status}");
    }
  }

  private static async Task<long> _versionOfAsync(NpgsqlConnection conn, Guid id) =>
    await _scalarAsync(conn, "SELECT version FROM wh_event_store WHERE event_id = @id", ("id", id));

  private static Task<long> _countAsync(NpgsqlConnection conn, Guid stream) =>
    _scalarAsync(conn, "SELECT COUNT(*) FROM wh_event_store WHERE stream_id = @s", ("s", stream));

  private static Task<long> _generationAsync(NpgsqlConnection conn) =>
    _scalarAsync(conn, "SELECT COALESCE((SELECT setting_value::bigint FROM wh_settings WHERE setting_key = 'integrity_origin_generation'), 0)");

  private static Task<long> _healAsync(NpgsqlConnection conn) =>
    _scalarAsync(conn, "SELECT COUNT(*) FROM _wh_heal_stream_digests(NULL, INTERVAL '0 seconds', false) WHERE kind <> 'filled'");

  private static async Task<(int Checked, int Drifted)> _verifyEpochsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT epochs_checked, epochs_drifted FROM verify_digest_epochs(NOW(), 0, 1000)";
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt32(0), reader.GetInt32(1));
  }

  private static async Task<string> _epochsAsync(NpgsqlConnection conn) =>
    await _textAsync(conn, """
      SELECT COALESCE(string_agg(epoch_id || ':' || digest_lo || ':' || digest_hi || ':' || event_count, ',' ORDER BY epoch_id), '')
      FROM wh_digest_epochs
      """);

  private static async Task<string> _typeAnswerAsync(NpgsqlConnection conn) =>
    await _textAsync(conn, """
      SELECT COALESCE(string_agg(event_type || ':' || digest_lo || ':' || digest_hi || ':' || event_count, ','), '')
      FROM compute_type_digests_epoch(NULL, NULL, NOW(), 0)
      """);

  private static async Task<(long Lo, long Hi, long Count)> _removedAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT COALESCE(bit_xor(removed_lo), 0), COALESCE(bit_xor(removed_hi), 0), COALESCE(SUM(removed_count), 0)::bigint
      FROM wh_digest_epoch_removed
      """;
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
  }

  private static async Task<(long Lo, long Hi)> _foldAsync(NpgsqlConnection conn, params Guid[] ids) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT bit_xor(hashtextextended(x::text, 0)), bit_xor(hashtextextended(x::text, 1)) FROM unnest(@ids::uuid[]) x";
    cmd.Parameters.AddWithValue(nameof(ids), ids);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1));
  }

  private sealed record Bucket(long Lo, long Hi, int Count, long CarriedLo, long CarriedHi, int CarriedCount);

  private static async Task<Bucket> _bucketAsync(NpgsqlConnection conn, Guid stream, string origin = ZERO) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT digest_lo, digest_hi, event_count, COALESCE(carried_lo, 0), COALESCE(carried_hi, 0), COALESCE(carried_count, 0)
      FROM wh_stream_digests WHERE stream_id = @s AND origin_service_id = @o::uuid
      """;
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("o", origin);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"no digest bucket for stream {stream}");
    }
    return new Bucket(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5));
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      // Drained: run for its effect, every row read so a failure surfaces.
    }
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<string> _textAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
  }
}
