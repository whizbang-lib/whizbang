// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2529: the cut gate (<c>_wh_cut_awaits_seal</c>) never holds a cut for a received event that has no position on its
/// origin's lane (<c>origin_commit_sequence</c> NULL). The digest folds such an event only in the open window, never in
/// a sealed epoch, so no seal will ever cover it, and a cut carries it into the open window's removed row, where the
/// answer stays exactly what it was. Holding the cut for it would hold the stream forever. A received event with a real
/// position above the seal, and a local event the stamper has not sequenced yet, still hold the cut.
/// </summary>
/// <docs>resilience/stream-integrity#a-cut-waits-for-the-seal</docs>
[Category("Integration")]
[Category("Shard2")]
public class CutOfUnpositionedReceivedHistorySqlTests : EFCoreTestBase {
  private const string TYPE = "Whizbang.Tests.UnpositionedCutEvent, Whizbang.Tests";

  [Test]
  public async Task Truncate_OfReceivedHistoryWithNoOriginSequence_IsNotHeld_EvenWithNothingSealedAsync() {
    await using var conn = await _openAsync();
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _receiveAsync(conn, origin, stream, [null, null, null]);
    await _setAsync(conn, "integrity_cut_waits_for_seal", "true", "boolean");

    var status = await _truncateAsync(conn, stream, await _versionOfAsync(conn, ids[1]));

    await Assert.That(status).IsEqualTo("truncated")
      .Because("an event with no position on its origin's lane is never sealed, so the gate has nothing to wait for");
    await Assert.That(await _countAsync(conn, stream)).IsEqualTo(1L);
  }

  [Test]
  public async Task ArchiveWhole_OfReceivedHistoryWithNoOriginSequence_IsNotHeld_AndTheOriginsAnswerStaysAsync() {
    await using var conn = await _openAsync();
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    _ = await _receiveAsync(conn, origin, stream, [1, 2, null, null]);
    // The origin's later positions, on another stream, so the lane seals past this stream's positioned events.
    _ = await _receiveAsync(conn, origin, (Guid)TrackedGuid.New(), [3, 4, 5, 6, 7, 8]);
    await _sealAsync(conn, width: 2);
    await _executeAsync(conn,
      "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) VALUES (@o, 3, NOW())", ("o", origin));
    await _setAsync(conn, "integrity_cut_waits_for_seal", "true", "boolean");
    var before = await _typeAnswerAsync(conn, origin);

    var status = await _archiveAsync(conn, stream, null);

    await Assert.That(status).IsEqualTo("archived")
      .Because("positions 1 and 2 are sealed and verified, and the unpositioned events are never sealed: nothing holds the stream");
    await Assert.That(await _countAsync(conn, stream)).IsEqualTo(0L);
    await Assert.That(await _typeAnswerAsync(conn, origin)).IsEqualTo(before)
      .Because("the unpositioned events are carried into the open window's removed row, so the origin's answer is unchanged");
  }

  [Test]
  public async Task Truncate_OfReceivedHistoryWithAPositionAboveTheSeal_IsStillHeldAsync() {
    await using var conn = await _openAsync();
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _receiveAsync(conn, origin, stream, [null, 1, 2, 3, 4, 5]);
    await _sealAsync(conn, width: 2);
    await _executeAsync(conn,
      "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) VALUES (@o, 1, NOW())", ("o", origin));
    // #2544: the seal holds a cut only for an origin this service audits: known, with a request address.
    await _executeAsync(conn, """
      INSERT INTO wh_integrity_known_origins (origin_service_id, origin_service_name, request_topic, last_checkpoint_at)
      VALUES (@o, 'origin-svc', 'origin.requests', NOW())
      """, ("o", origin));
    await _setAsync(conn, "integrity_cut_waits_for_seal", "true", "boolean");

    await Assert.That(await _truncateAsync(conn, stream, await _versionOfAsync(conn, ids[2])))
      .IsEqualTo(ArchiveStatus.AWAITING_SEAL)
      .Because("position 2 is above this service's verified seal for the origin (1), whatever the unpositioned event does");
    await Assert.That(await _archiveAsync(conn, stream, null)).IsEqualTo(ArchiveStatus.AWAITING_SEAL)
      .Because("the whole stream reaches position 5, above the lane's sealed frontier");
    await Assert.That(await _countAsync(conn, stream)).IsEqualTo(6L).Because("a cut that waits removes nothing");
  }

  [Test]
  public async Task Truncate_OfLocalHistoryTheStamperHasNotSequenced_IsStillHeldAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _emitAsync(conn, stream, 3);
    await _sealAsync(conn, width: 2);
    await _executeAsync(conn, "UPDATE wh_event_store SET commit_sequence = NULL WHERE event_id = @id", ("id", ids[0]));
    await _setAsync(conn, "integrity_cut_waits_for_seal", "true", "boolean");

    await Assert.That(await _truncateAsync(conn, stream, await _versionOfAsync(conn, ids[1])))
      .IsEqualTo(ArchiveStatus.AWAITING_SEAL)
      .Because("a local event is always stamped in the end, and the local answer leaves it out until then");
  }

  // ===== helpers =====

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>Stores events received from an origin through the inbox, each at the given origin position (null: none).</summary>
  private static async Task<Guid[]> _receiveAsync(NpgsqlConnection conn, Guid origin, Guid stream, long?[] sequences) {
    Guid[] ids = [.. Enumerable.Range(0, sequences.Length).Select(_ => (Guid)TrackedGuid.New()).Order()];
    var instance = (Guid)TrackedGuid.New();
    for (var i = 0; i < ids.Length; i++) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = """
        SELECT * FROM store_inbox_messages(
          jsonb_build_array(jsonb_build_object(
            'MessageId', @id::text, 'HandlerName', 'CutHandler', 'MessageType', @type::text,
            'Envelope', jsonb_build_object('MessageId', @id::text, 'Payload', '{}'::jsonb, 'Hops', '[]'::jsonb),
            'EnvelopeType', 'env', 'StreamId', @sid::text, 'IsEvent', true, 'Flags', 0,
            'Metadata', '{}'::jsonb, 'SourceServiceId', @o::text, 'SourceCommitSequence', @seq)),
          @inst, NOW() + INTERVAL '5 minutes', NOW(), 100)
        """;
      cmd.Parameters.AddWithValue("id", ids[i]);
      cmd.Parameters.AddWithValue("type", TYPE);
      cmd.Parameters.AddWithValue("sid", stream);
      cmd.Parameters.AddWithValue("o", origin);
      cmd.Parameters.Add(new NpgsqlParameter("seq", NpgsqlDbType.Bigint) { Value = (object?)sequences[i] ?? DBNull.Value });
      cmd.Parameters.AddWithValue("inst", (Guid)TrackedGuid.New());
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        // Drained.
      }
    }
    await _executeAsync(conn,
      "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)",
      ("inst", instance), ("ids", ids));
    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW() - INTERVAL '1 hour', 10)",
      ("inst", instance));
    await _executeAsync(conn, "DELETE FROM wh_inbox_state WHERE message_id = ANY(@ids)", ("ids", ids));
    await _executeAsync(conn, "DELETE FROM wh_inbox WHERE message_id = ANY(@ids)", ("ids", ids));
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    await _executeAsync(conn, "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '1 hour' WHERE event_id = ANY(@ids)", ("ids", ids));
    var stored = await _scalarAsync(conn,
      "SELECT COUNT(*) FROM wh_event_store WHERE event_id = ANY(@ids) AND origin_service_id = @o", ("ids", ids), ("o", origin));
    if (stored != ids.Length) {
      throw new InvalidOperationException($"the setup stored {stored} of {ids.Length} received events");
    }
    var unpositioned = await _scalarAsync(conn,
      "SELECT COUNT(*) FROM wh_event_store WHERE event_id = ANY(@ids) AND origin_commit_sequence IS NULL", ("ids", ids));
    if (unpositioned != sequences.Count(s => s is null)) {
      throw new InvalidOperationException($"the setup stored {unpositioned} events with no origin position");
    }
    return ids;
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
    await _executeAsync(conn, "SELECT _emit_event_store_chain(@ids, @inst, NOW() + INTERVAL '5 minutes', NOW() - INTERVAL '1 hour', 10000)",
      ("ids", ids), ("inst", instance));
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    await _executeAsync(conn, "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '1 hour' WHERE event_id = ANY(@ids)", ("ids", ids));
    return ids;
  }

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

  private static async Task<string> _archiveAsync(NpgsqlConnection conn, Guid stream, long? through) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT archive_status FROM wh_archive_stream(@s, @v, 'test://archive')";
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Bigint) { Value = (object?)through ?? DBNull.Value });
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static Task<long> _versionOfAsync(NpgsqlConnection conn, Guid id) =>
    _scalarAsync(conn, "SELECT version FROM wh_event_store WHERE event_id = @id", ("id", id));

  private static Task<long> _countAsync(NpgsqlConnection conn, Guid stream) =>
    _scalarAsync(conn, "SELECT COUNT(*) FROM wh_event_store WHERE stream_id = @s", ("s", stream));

  private static async Task<string> _typeAnswerAsync(NpgsqlConnection conn, Guid origin) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT COALESCE(string_agg(event_type || ':' || digest_lo || ':' || digest_hi || ':' || event_count, ','), '')
      FROM compute_type_digests_epoch(@o, NULL, NOW(), 0)
      """;
    cmd.Parameters.AddWithValue("o", origin);
    return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
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
}
