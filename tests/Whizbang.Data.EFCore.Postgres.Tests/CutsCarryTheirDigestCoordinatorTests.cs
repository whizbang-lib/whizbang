// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The work coordinator's side of cuts carrying their digest (#2363): the cut gate's settings, the seal a consumer reports,
/// the carried detail's resting period, and the recomputed answers that add what a cut carried.
/// </summary>
/// <docs>resilience/stream-integrity#cuts-carry-their-digest</docs>
[Category("Integration")]
[Category("Shard2")]
[NotInParallel("IntegrityManifestGates")]   // the manifest receptors' per-process gates are shared state
public class CutsCarryTheirDigestCoordinatorTests : EFCoreTestBase {
  private const string TYPE = "Whizbang.Tests.CutCoordinatorEvent, Whizbang.Tests";

  [Test]
  public async Task Coordinator_SetCutGatesAsync_WritesBothSettings_AndAChangeAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, _json());

    await coordinator.SetCutGatesAsync(waitForSeal: true, waitForConsumers: false);
    await Assert.That(await _settingAsync("integrity_cut_waits_for_seal")).IsEqualTo("true");
    await Assert.That(await _settingAsync("integrity_cut_waits_for_consumers")).IsEqualTo("false");

    await coordinator.SetCutGatesAsync(waitForSeal: false, waitForConsumers: true);
    await Assert.That(await _settingAsync("integrity_cut_waits_for_seal")).IsEqualTo("false")
      .Because("turning sealing off must reach the store, or a cut keeps waiting for a seal that cannot come");
    await Assert.That(await _settingAsync("integrity_cut_waits_for_consumers")).IsEqualTo("true");
  }

  [Test]
  public async Task Coordinator_RecordConsumerSealAsync_RecordsWhatTheConsumerVerifiedAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, _json());

    await coordinator.RecordConsumerSealAsync("consumer-a", 42);
    await coordinator.RecordConsumerSealAsync("consumer-a", 17);

    await Assert.That(await _scalarAsync("SELECT sealed_through FROM wh_integrity_consumer_seals WHERE requester_service = 'consumer-a'"))
      .IsEqualTo(17L)
      .Because("the seal a consumer reports is what it has verified now; a reset seal is reported lower");
    await Assert.That(async () => await coordinator.RecordConsumerSealAsync("", 1)).Throws<ArgumentException>();
  }

  [Test]
  public async Task Coordinator_RollOffCarriedDetailAsync_ClearsOnlyDetailPastItsRestAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, _json());
    var old = (Guid)TrackedGuid.New();
    var recent = (Guid)TrackedGuid.New();
    await _executeAsync("""
      INSERT INTO wh_stream_digests
        (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at,
         carried_lo, carried_hi, carried_count, carried_id_lo, carried_id_hi, carried_at)
      VALUES ('00000000-0000-0000-0000-000000000000'::uuid, '', @t, @old, 1, 2, 3, NOW(), 1, 2, 3, 4, 5, NOW() - INTERVAL '20 days'),
             ('00000000-0000-0000-0000-000000000000'::uuid, '', @t, @recent, 1, 2, 3, NOW(), 1, 2, 3, 4, 5, NOW() - INTERVAL '1 day')
      """, ("t", TYPE), ("old", old), ("recent", recent));

    var cleared = await coordinator.RollOffCarriedDetailAsync(TimeSpan.FromDays(14), batchSize: 10);

    await Assert.That(cleared).IsEqualTo(1);
    await Assert.That(await _scalarAsync("SELECT COUNT(*) FROM wh_stream_digests WHERE carried_lo IS NULL AND stream_id = @s", ("s", old)))
      .IsEqualTo(1L);
    await Assert.That(await _scalarAsync("SELECT carried_count FROM wh_stream_digests WHERE stream_id = @s", ("s", recent)))
      .IsEqualTo(3L).Because("detail inside its resting period stays");
  }

  [Test]
  public async Task ComputeStreamDigestsAsync_AStreamThisServiceCut_AnswersWhatItAnsweredBeforeTheCutAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, _json());
    var stream = (Guid)TrackedGuid.New();
    var ids = await _emitAsync(stream, 4);
    var before = await coordinator.ComputeStreamDigestsAsync(null, [TYPE], TimeSpan.Zero);

    await _carryAsync(stream, through: ids[1]);

    var after = await coordinator.ComputeStreamDigestsAsync(null, [TYPE], TimeSpan.Zero);
    await Assert.That(_describe(after)).IsEqualTo(_describe(before))
      .Because("the recomputed answer is the live fold plus what the cut carried, as the bucket's digest is");
    await Assert.That(await coordinator.ComputeStreamDigestsAsync(null, ["Whizbang.Tests.Other, Whizbang.Tests"], TimeSpan.Zero))
      .IsEmpty().Because("a carried part of another type is not added to a type the answer was not asked for");
  }

  [Test]
  public async Task ComputeStreamDigestsForChunkAsync_FullHistoryAddsTheCarriedPart_AWindowDoesNotAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, _json());
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _receiveAsync(origin, stream, 4);
    var before = await coordinator.ComputeStreamDigestsForChunkAsync(origin, [stream], null, null, TimeSpan.Zero);
    await Assert.That(before!.Single().EventCount).IsEqualTo(4).Because("precondition: the four received events are stored");

    await _carryAsync(stream, through: ids[1]);

    var full = await coordinator.ComputeStreamDigestsForChunkAsync(origin, [stream], null, null, TimeSpan.Zero);
    await Assert.That(_describe(full!)).IsEqualTo(_describe(before!))
      .Because("a full-history fold is the live fold plus what the cut carried");
    var windowed = await coordinator.ComputeStreamDigestsForChunkAsync(origin, [stream], 3, 5, TimeSpan.Zero);
    await Assert.That(windowed!.Single().EventCount).IsEqualTo(2)
      .Because("a window above the cut folds only the events in it; a cut never reaches into a window");
  }

  [Test]
  public async Task AWindowedTypeAsk_RecordsTheConsumersSeal_OnTheOriginAsync() {
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, new Dictionary<string, EventFlags>());
    var stream = (Guid)TrackedGuid.New();
    var ids = await h.EmitAsync(new TwoServiceEmit(stream, TYPE, 0, null, """{"k":"v"}"""),
      new TwoServiceEmit(stream, TYPE, 0, null, """{"k":"v"}"""));
    await h.DeliverAsync(ids);

    await h.AuditAsync(TwoServiceAudit.TypesWindowed, TYPE);
    var seal = await h.SealAsync();
    await h.AuditAsync(TwoServiceAudit.TypesWindowed, TYPE);

    await Assert.That(seal).IsGreaterThan(0L).Because("precondition: the first audit verified the history and sealed it");
    await Assert.That(await TwoServiceHarness.CountAsync(h.Origin,
      "SELECT sealed_through FROM wh_integrity_consumer_seals WHERE requester_service = @r", ("r", TwoServiceHarness.CONSUMER_NAME)))
      .IsEqualTo(seal)
      .Because("#2363: each audit asks from the consumer's seal, so the origin learns how far that consumer has verified");
  }

  // ===== helpers =====

  private static JsonSerializerOptions _json() => JsonContextRegistry.CreateCombinedOptions();

  private static string _describe(IReadOnlyList<StreamDigest> digests) =>
    string.Join(",", digests.Select(d => $"{d.StreamId}:{d.EventType}:{d.DigestLo}:{d.DigestHi}:{d.EventCount}:{d.IdLo}:{d.IdHi}"));

  /// <summary>Carries, as a cut does, the stream's events at or below the given one, then removes them.</summary>
  private async Task _carryAsync(Guid stream, Guid through) {
    await _executeAsync("SELECT _wh_carry_cut(ARRAY[@s], (SELECT version FROM wh_event_store WHERE event_id = @e), NOW())",
      ("s", stream), ("e", through));
    await _executeAsync("""
      DELETE FROM wh_event_store WHERE stream_id = @s AND version <= (SELECT version FROM wh_event_store WHERE event_id = @e)
      """, ("s", stream), ("e", through));
  }

  private async Task<Guid[]> _emitAsync(Guid stream, int n) {
    Guid[] ids = [.. Enumerable.Range(0, n).Select(_ => (Guid)TrackedGuid.New()).Order()];
    var instance = (Guid)TrackedGuid.New();
    foreach (var id in ids) {
      await _executeAsync("""
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES (@id, 'cut-topic', @type, 'env', '{"p":{}}'::jsonb, '{}'::jsonb, NULL,
                1, 0, @inst, NOW() + INTERVAL '5 minutes', 0, @sid, true, 0, NOW() - INTERVAL '1 hour')
        """, ("id", id), ("type", TYPE), ("inst", instance), ("sid", stream));
    }
    await _executeAsync("SELECT _emit_event_store_chain(@ids, @inst, NOW() + INTERVAL '5 minutes', NOW() - INTERVAL '1 hour', 10000)",
      ("ids", ids), ("inst", instance));
    await _executeAsync("SELECT stamp_pending_commit_sequences(1000)");
    await _executeAsync("UPDATE wh_event_store SET created_at = NOW() - INTERVAL '1 hour' WHERE event_id = ANY(@ids)", ("ids", ids));
    return ids;
  }

  private async Task<Guid[]> _receiveAsync(Guid origin, Guid stream, int n) {
    Guid[] ids = [.. Enumerable.Range(0, n).Select(_ => (Guid)TrackedGuid.New()).Order()];
    var instance = (Guid)TrackedGuid.New();
    for (var i = 0; i < n; i++) {
      await _executeAsync("""
        SELECT * FROM store_inbox_messages(
          jsonb_build_array(jsonb_build_object(
            'MessageId', @id::text, 'HandlerName', 'CutHandler', 'MessageType', @type::text,
            'Envelope', jsonb_build_object('MessageId', @id::text, 'Payload', '{}'::jsonb, 'Hops', '[]'::jsonb),
            'EnvelopeType', 'env', 'StreamId', @sid::text, 'IsEvent', true, 'Flags', 0,
            'Metadata', '{}'::jsonb, 'SourceServiceId', @o::text, 'SourceCommitSequence', @seq)),
          @inst, NOW() + INTERVAL '5 minutes', NOW(), 100)
        """, ("id", ids[i]), ("type", TYPE), ("sid", stream), ("o", origin), ("seq", i + 1), ("inst", instance));
    }
    await _executeAsync(
      "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)",
      ("inst", instance), ("ids", ids));
    await _executeAsync("SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW() - INTERVAL '1 hour', 4)",
      ("inst", instance));
    await _executeAsync("SELECT stamp_pending_commit_sequences(1000)");
    await _executeAsync("UPDATE wh_event_store SET created_at = NOW() - INTERVAL '1 hour' WHERE event_id = ANY(@ids)", ("ids", ids));
    return ids;
  }

  private async Task<string?> _settingAsync(string key) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT setting_value FROM wh_settings WHERE setting_key = @k";
    cmd.Parameters.AddWithValue("k", key);
    return (string?)await cmd.ExecuteScalarAsync();
  }

  private async Task _executeAsync(string sql, params (string Name, object Value)[] parameters) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
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

  private async Task<long> _scalarAsync(string sql, params (string Name, object Value)[] parameters) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }
}
