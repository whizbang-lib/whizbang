// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
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
/// A superseding (latest-wins) type counts its key, not each copy (#2364). Every digest folds a key's slot, an identity
/// made of the stream, the group and the key, once, at the place its first copy was stored, whichever copies are still
/// held. The reaper deletes older copies physically and never touches a digest, a seal or the origin generation, so the
/// order two services reap in never makes either look short.
/// </summary>
/// <docs>fundamentals/events/superseding-events#the-digest-counts-the-key</docs>
[Category("Integration")]
[Category("Shard3")]
public class SupersedingCountsTheKeySqlTests : EFCoreTestBase {
  private const string TYPE = "Probe.Contracts.KeySlotUpdated, Probe.Contracts";
  private const string OTHER_IN_GROUP = "Probe.Contracts.KeySlotReplaced, Probe.Contracts";
  private const int SUPERSEDING = 128;
  private const string ZERO = "00000000-0000-0000-0000-000000000000";

  [Test]
  public async Task Emit_CopiesOfAKey_AreCountedOnce_AsTheKeysSlotAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();

    await _emitAsync(conn, stream, (TYPE, "key-a", null), (TYPE, "key-b", null));
    await _emitAsync(conn, stream, (TYPE, "key-a", null));
    await _emitAsync(conn, stream, (TYPE, "key-a", null));

    var bucket = await _bucketAsync(conn, stream, TYPE);
    var expected = await _slotFoldAsync(conn, (stream, TYPE, "key-a"), (stream, TYPE, "key-b"));
    await Assert.That(bucket).IsEqualTo((expected.Lo, expected.Hi, 2))
      .Because("three copies of key-a and one of key-b are two keys: each slot once, whatever its copies");
  }

  [Test]
  public async Task Reap_DeletesOlderCopies_AndLeavesTheDigestTheSealsAndTheGenerationAsTheyWereAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitAsync(conn, stream, (TYPE, "key-a", null), (TYPE, "key-b", null));
    await _emitAsync(conn, stream, (TYPE, "key-a", null));
    await _emitAsync(conn, (Guid)TrackedGuid.New(), (TYPE, "filler-1", null), (TYPE, "filler-2", null), (TYPE, "filler-3", null));
    await _sealAsync(conn);
    var bucket = await _bucketAsync(conn, stream, TYPE);
    var epochs = await _epochsAsync(conn);
    var answer = await _typeAnswerAsync(conn);
    var generation = await _generationAsync(conn);

    await _reapAsync(conn);

    await Assert.That(await _countAsync(conn, stream)).IsEqualTo(2L).Because("precondition: the older copy of key-a is gone");
    await Assert.That(await _bucketAsync(conn, stream, TYPE)).IsEqualTo(bucket)
      .Because("the key is still held, so what the digest counts did not change");
    await Assert.That(await _epochsAsync(conn)).IsEqualTo(epochs).Because("no seal moves on a reap");
    await Assert.That(await _typeAnswerAsync(conn)).IsEqualTo(answer);
    await Assert.That(await _generationAsync(conn)).IsEqualTo(generation)
      .Because("a reap rewrites nothing a consumer verified");
  }

  [Test]
  public async Task AfterAReap_TheHealAndTheSealRecheckFindNoDrift_AndStillFindALossAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitAsync(conn, stream, (TYPE, "key-a", null), (TYPE, "key-b", null));
    await _emitAsync(conn, stream, (TYPE, "key-a", null));
    await _emitAsync(conn, (Guid)TrackedGuid.New(), (TYPE, "filler-1", null), (TYPE, "filler-2", null), (TYPE, "filler-3", null));
    await _sealAsync(conn);

    await _reapAsync(conn);

    await Assert.That(await _healAsync(conn)).IsEqualTo(0L)
      .Because("a recompute counts the key's slot from the copy still held, exactly as the fold did");
    var (checkedEpochs, drifted) = await _verifyEpochsAsync(conn);
    await Assert.That(checkedEpochs).IsGreaterThan(0);
    await Assert.That(drifted).IsEqualTo(0)
      .Because("the slot is counted where its first copy sat, even once only a later copy is held");
    await _executeAsync(conn, "SELECT refold_digest_epochs(@l::uuid, 0, 10)", ("l", ZERO));
    await Assert.That((await _verifyEpochsAsync(conn)).Drifted).IsEqualTo(0)
      .Because("everything was re-checked; a refold rewrote nothing it then disagrees with");

    await _executeAsync(conn, "DELETE FROM wh_event_store WHERE stream_id = @s AND supersede_key = 'key-b'", ("s", stream));
    await Assert.That(await _healAsync(conn)).IsEqualTo(1L)
      .Because("positive control: a key whose every copy is lost without a cut is drift");
  }

  [Test]
  public async Task AKeyOfAGroup_StaysCountedUnderItsFirstCopysType_WhenANewerCopyIsAnotherTypeAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitAsync(conn, stream, (TYPE, "key-a", "profile"));
    await _emitAsync(conn, stream, (OTHER_IN_GROUP, "key-a", "profile"));
    var first = await _bucketAsync(conn, stream, TYPE);

    await _executeAsync(conn, "UPDATE wh_event_store SET created_at = created_at - INTERVAL '1 day'");
    await _reapAsync(conn);

    await Assert.That(await _countAsync(conn, stream)).IsEqualTo(1L).Because("precondition: only the newer copy is held");
    await Assert.That(await _bucketAsync(conn, stream, TYPE)).IsEqualTo(first)
      .Because("the slot belongs to its first copy's type bucket for as long as the key is held");
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_stream_digests WHERE stream_id = @s AND event_type = @t",
      ("s", stream), ("t", OTHER_IN_GROUP))).IsEqualTo(0L)
      .Because("the newer copy of another type adds no second count of the same key");
    await Assert.That(await _healAsync(conn)).IsEqualTo(0L);
  }

  // ===== helpers =====

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>One batch through the outbox emit chain, stamped and settled.</summary>
  private static async Task _emitAsync(NpgsqlConnection conn, Guid stream, params (string Type, string Key, string? Group)[] events) {
    Guid[] ids = [.. Enumerable.Range(0, events.Length).Select(_ => (Guid)TrackedGuid.New()).Order()];
    var instance = (Guid)TrackedGuid.New();
    for (var i = 0; i < events.Length; i++) {
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES (@id, 'slot-topic', @type, 'env', '{}'::jsonb,
                CASE WHEN @grp::text IS NULL THEN jsonb_build_object('sk', @key::text)
                     ELSE jsonb_build_object('sk', @key::text, 'sg', @grp::text) END,
                NULL, 1, 0, @inst, NOW() + INTERVAL '5 minutes', 0, @sid, true, @flags, NOW() - INTERVAL '1 hour')
        """, ("id", ids[i]), ("type", events[i].Type), ("key", events[i].Key), ("grp", (object?)events[i].Group ?? DBNull.Value),
        ("inst", instance), ("sid", stream), ("flags", SUPERSEDING));
    }
    await _executeAsync(conn, "SELECT _emit_event_store_chain(@ids, @inst, NOW() + INTERVAL '5 minutes', NOW() - INTERVAL '1 hour', 10000)",
      ("ids", ids), ("inst", instance));
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    await _executeAsync(conn, "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '1 hour' WHERE event_id = ANY(@ids)", ("ids", ids));
  }

  private static async Task _sealAsync(NpgsqlConnection conn) {
    await _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES ('integrity_epoch_width', '2', 'integer', 'test')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """);
    await _executeAsync(conn, "UPDATE wh_stream_digests SET updated_at = NOW() - INTERVAL '2 hours'");
    await _executeAsync(conn, "SELECT close_digest_epochs(NOW(), 0, 1000)");
  }

  private static Task _reapAsync(NpgsqlConnection conn) => _executeAsync(conn, "SELECT * FROM wh_reap_superseded_events(5000)");

  private static async Task<(long Lo, long Hi, int Count)> _bucketAsync(NpgsqlConnection conn, Guid stream, string type) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT digest_lo, digest_hi, event_count FROM wh_stream_digests WHERE stream_id = @s AND event_type = @t";
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("t", type);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"no bucket for {type} in {stream}");
    }
    return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2));
  }

  private static async Task<(long Lo, long Hi)> _slotFoldAsync(NpgsqlConnection conn, params (Guid Stream, string Group, string Key)[] slots) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT bit_xor(hashtextextended(_wh_slot_id(s, g, k)::text, 0)), bit_xor(hashtextextended(_wh_slot_id(s, g, k)::text, 1))
      FROM unnest(@s::uuid[], @g::text[], @k::text[]) AS x(s, g, k)
      """;
    cmd.Parameters.AddWithValue("s", slots.Select(x => x.Stream).ToArray());
    cmd.Parameters.AddWithValue("g", slots.Select(x => x.Group).ToArray());
    cmd.Parameters.AddWithValue("k", slots.Select(x => x.Key).ToArray());
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1));
  }

  private static Task<long> _countAsync(NpgsqlConnection conn, Guid stream) =>
    _scalarAsync(conn, "SELECT COUNT(*) FROM wh_event_store WHERE stream_id = @s", ("s", stream));

  private static Task<long> _generationAsync(NpgsqlConnection conn) =>
    _scalarAsync(conn, "SELECT COALESCE((SELECT setting_value::bigint FROM wh_settings WHERE setting_key = 'integrity_origin_generation'), 0)");

  private static Task<long> _healAsync(NpgsqlConnection conn) =>
    _scalarAsync(conn, "SELECT COUNT(*) FROM _wh_heal_stream_digests(NULL, INTERVAL '0 seconds', false) WHERE kind <> 'filled'");

  private static async Task<(int Checked, int Drifted)> _verifyEpochsAsync(NpgsqlConnection conn) {
    await _executeAsync(conn, "UPDATE wh_digest_epoch_seals SET rechecked_at = NULL");
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT epochs_checked, epochs_drifted FROM verify_digest_epochs(NOW(), 0, 1000)";
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt32(0), reader.GetInt32(1));
  }

  private static async Task<string> _epochsAsync(NpgsqlConnection conn) =>
    await _textAsync(conn, """
      SELECT COALESCE(string_agg(epoch_id || ':' || event_type || ':' || digest_lo || ':' || event_count, ',' ORDER BY epoch_id, event_type), '')
      FROM wh_digest_epochs
      """);

  private static async Task<string> _typeAnswerAsync(NpgsqlConnection conn) =>
    await _textAsync(conn, """
      SELECT COALESCE(string_agg(event_type || ':' || digest_lo || ':' || digest_hi || ':' || event_count, ','), '')
      FROM compute_type_digests_epoch(NULL, NULL, NOW(), 0)
      """);

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

  private static async Task<string> _textAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
  }
}
