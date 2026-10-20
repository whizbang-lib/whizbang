// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// An at-most-once schedule occurrence, fired through the real spawn path
/// (<c>wh_trigger_schedule_now</c> into <c>_wh_spawn_occurrence</c>, <c>store_outbox_messages</c> and the emit chain),
/// stays out of everything that would count it as owed or deliver it a second time: the stream digest the emit chain
/// folds, the full-sweep digest recompute, the sealed epoch fold, the integrity checkpoint count and the re-delivery
/// selection (#1484).
/// </summary>
/// <remarks>
/// The guarantee is declared on the schedule and written to the outbox row's metadata. Every exclusion used to read it
/// from the stored event body's metadata, which the emit chain builds from the message id and hops alone, so no
/// exclusion ever matched. The integrity tests that cover these exclusions seed the body metadata by hand and so could
/// not see it. These tests produce the occurrence the way production does, and pair it with an at-least-once
/// occurrence produced the same way, so an assertion cannot pass by excluding every occurrence.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/214_AtMostOnceEventsCarryAFlag.sql</code-under-test>
/// <docs>resilience/stream-integrity</docs>
[Category("Integration")]
[NotInParallel("IntegrityCheckpoint")]
[Category("Shard3")]
public class AtMostOnceOccurrenceIntegrityTests : EFCoreTestBase {

  /// <summary>The event-store flag bit an at-most-once occurrence carries (<c>EventFlags.AtMostOnce</c>).</summary>
  private const int AT_MOST_ONCE_FLAG = 32;

  private const string TENANT = "tenant-amo";
  private const string AT_MOST_ONCE_TYPE = "Whizbang.Tests.AtMostOnceOccurrence";
  private const string AT_LEAST_ONCE_TYPE = "Whizbang.Tests.AtLeastOnceOccurrence";
  private const string ZERO_UUID = "00000000-0000-0000-0000-000000000000";

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  // ── the real write path ───────────────────────────────────────────────────

  [Test]
  public async Task AtMostOnceOccurrence_StoredByTheEmitChain_CarriesTheAtMostOnceFlagAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var atMostOnce = await _fireAsync(conn, deliveryGuarantee: 1, AT_MOST_ONCE_TYPE);
    var atLeastOnce = await _fireAsync(conn, deliveryGuarantee: 0, AT_LEAST_ONCE_TYPE);

    await Assert.That(await _eventFlagsAsync(conn, atMostOnce.EventId) & AT_MOST_ONCE_FLAG).IsEqualTo(AT_MOST_ONCE_FLAG)
      .Because("the guarantee has to reach the event-store row: every exclusion reads it there, and the stored body " +
               "metadata, where they used to look, never carries it.");
    await Assert.That(await _eventFlagsAsync(conn, atLeastOnce.EventId) & AT_MOST_ONCE_FLAG).IsEqualTo(0)
      .Because("an at-least-once occurrence is owed like any other event.");
  }

  [Test]
  public async Task AtMostOnceOccurrence_StoredByTheEmitChain_IsNotFoldedIntoTheStreamDigestAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var atMostOnce = await _fireAsync(conn, deliveryGuarantee: 1, AT_MOST_ONCE_TYPE);
    var atLeastOnce = await _fireAsync(conn, deliveryGuarantee: 0, AT_LEAST_ONCE_TYPE);

    await Assert.That(await _digestCountAsync(conn, atMostOnce.StreamId)).IsNull()
      .Because("the emit chain's fold leaves at-most-once occurrences out of the digest a consumer is compared " +
               "against, or every consumer that never received one reads as missing an event.");
    await Assert.That(await _digestCountAsync(conn, atLeastOnce.StreamId)).IsEqualTo(1);
  }

  [Test]
  public async Task AtMostOnceOccurrence_StoredByTheEmitChain_IsLeftOutOfTheFullDigestRecomputeAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var atMostOnce = await _fireAsync(conn, deliveryGuarantee: 1, AT_MOST_ONCE_TYPE);
    var atLeastOnce = await _fireAsync(conn, deliveryGuarantee: 0, AT_LEAST_ONCE_TYPE);
    await _ageAsync(conn);

    var digests = await _coordinator(ctx).ComputeStreamDigestsAsync(
      null, [AT_MOST_ONCE_TYPE, AT_LEAST_ONCE_TYPE], TimeSpan.Zero);

    var streams = digests.Select(d => d.StreamId).ToList();
    await Assert.That(streams.Contains(atMostOnce.StreamId)).IsFalse()
      .Because("the full recompute the sweep heals the digest table against uses the same exclusion as the fold.");
    await Assert.That(streams).Contains(atLeastOnce.StreamId);
  }

  [Test]
  public async Task AtMostOnceOccurrence_StoredByTheEmitChain_IsNotHealedIntoTheDigestTableAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var atMostOnce = await _fireAsync(conn, deliveryGuarantee: 1, AT_MOST_ONCE_TYPE);
    var atLeastOnce = await _fireAsync(conn, deliveryGuarantee: 0, AT_LEAST_ONCE_TYPE);
    await _ageAsync(conn);

    await _coordinator(ctx).VerifyDigestTableAsync(TimeSpan.Zero);

    await Assert.That(await _digestCountAsync(conn, atMostOnce.StreamId)).IsNull()
      .Because("the trust-but-verify sweep recomputes with the fold's exclusion, so it never inserts the occurrence " +
               "back as a missing bucket.");
    await Assert.That(await _digestCountAsync(conn, atLeastOnce.StreamId)).IsEqualTo(1);
  }

  [Test]
  public async Task AtMostOnceOccurrence_StoredByTheEmitChain_IsLeftOutOfTheTypeAndWindowedDigestsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coordinator(ctx);
    var atMostOnce = await _fireAsync(conn, deliveryGuarantee: 1, AT_MOST_ONCE_TYPE);
    var atLeastOnce = await _fireAsync(conn, deliveryGuarantee: 0, AT_LEAST_ONCE_TYPE);
    await _stampAsync(conn);
    await _ageAsync(conn);
    string[] types = [AT_MOST_ONCE_TYPE, AT_LEAST_ONCE_TYPE];

    var typeDigests = await coordinator.ComputeTypeDigestsAsync(null, types, TimeSpan.Zero);
    var typeWindow = await coordinator.ComputeTypeDigestsWindowedAsync(null, types, 0, null, TimeSpan.Zero);
    var streamWindow = await coordinator.ComputeStreamDigestsWindowedAsync(null, types, 0, null, null, 100, TimeSpan.Zero);

    await Assert.That(typeDigests.Any(d => d.EventType.Contains(AT_MOST_ONCE_TYPE, StringComparison.Ordinal))).IsFalse()
      .Because("the type-level answer folds the open window with the same exclusion as the sealed epochs.");
    await Assert.That(typeDigests.Any(d => d.EventType.Contains(AT_LEAST_ONCE_TYPE, StringComparison.Ordinal))).IsTrue();
    await Assert.That(typeWindow!.Digests.Any(d => d.EventType.Contains(AT_MOST_ONCE_TYPE, StringComparison.Ordinal))).IsFalse()
      .Because("a negotiated window folds its fringe live with the same exclusion.");
    await Assert.That(typeWindow.Digests.Any(d => d.EventType.Contains(AT_LEAST_ONCE_TYPE, StringComparison.Ordinal))).IsTrue();
    var windowStreams = streamWindow!.Digests.Select(d => d.StreamId).ToList();
    await Assert.That(windowStreams.Contains(atMostOnce.StreamId)).IsFalse()
      .Because("the per-stream window a consumer drills into after a type mismatch uses the same exclusion.");
    await Assert.That(windowStreams).Contains(atLeastOnce.StreamId);
  }

  [Test]
  public async Task AtMostOnceOccurrence_StoredByTheEmitChain_IsLeftOutOfTheEpochFoldAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _fireAsync(conn, deliveryGuarantee: 1, AT_MOST_ONCE_TYPE);
    await _fireAsync(conn, deliveryGuarantee: 0, AT_LEAST_ONCE_TYPE);
    await _stampAsync(conn);

    var types = new List<string>();
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = $"SELECT b_type FROM _wh_epoch_buckets('{ZERO_UUID}'::uuid, 0, 1000000000)";
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        types.Add(reader.GetString(0));
      }
    }

    await Assert.That(types.Any(t => t.Contains(AT_MOST_ONCE_TYPE, StringComparison.Ordinal))).IsFalse()
      .Because("a sealed epoch is an immutable fold of what the lane owes; an at-most-once occurrence is not owed.");
    await Assert.That(types.Any(t => t.Contains(AT_LEAST_ONCE_TYPE, StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task AtMostOnceOccurrence_StoredByTheEmitChain_IsNotCountedInTheIntegrityCheckpointAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coordinator(ctx);
    await coordinator.AdvanceIntegrityCheckpointAsync();   // baseline: the next window starts here
    await _fireAsync(conn, deliveryGuarantee: 1, AT_MOST_ONCE_TYPE);
    await _fireAsync(conn, deliveryGuarantee: 0, AT_LEAST_ONCE_TYPE);
    await _stampAsync(conn);

    var window = await coordinator.AdvanceIntegrityCheckpointAsync();

    await Assert.That(window).IsNotNull();
    var counted = window!.Buckets.Select(b => b.EventType).ToList();
    await Assert.That(counted.Any(t => t.Contains(AT_MOST_ONCE_TYPE, StringComparison.Ordinal))).IsFalse()
      .Because("a checkpoint declares what consumers should have received; an at-most-once occurrence that never " +
               "arrived is its declared behavior, not a gap.");
    await Assert.That(counted.Any(t => t.Contains(AT_LEAST_ONCE_TYPE, StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task AtMostOnceOccurrence_StoredByTheEmitChain_IsNeverSelectedForRedeliveryAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var atMostOnce = await _fireAsync(conn, deliveryGuarantee: 1, AT_MOST_ONCE_TYPE);
    var atLeastOnce = await _fireAsync(conn, deliveryGuarantee: 0, AT_LEAST_ONCE_TYPE);

    var selected = await _coordinator(ctx).SelectRedeliveryEventsAsync(
      new RedeliveryRequest { StreamIds = [atMostOnce.StreamId, atLeastOnce.StreamId] });

    var ids = selected.Select(e => e.EventId).ToList();
    await Assert.That(ids.Contains(atMostOnce.EventId)).IsFalse()
      .Because("re-delivering an at-most-once occurrence is the one thing its guarantee forbids.");
    await Assert.That(ids).Contains(atLeastOnce.EventId);
  }

  // ── rows stored before the flag existed ───────────────────────────────────

  [Test]
  public async Task FlagAtMostOnceOccurrences_FlagsRowsStoredWithoutTheFlag_AndTakesThemOutOfDigestsAndEpochsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _setEpochWidthAsync(conn, 100);

    // Three at-most-once occurrences and one at-least-once, stored as a release without the flag stored them:
    // flag clear, folded into the stream digest, sealed into epoch 0.
    var withOutboxRow = await _fireAsync(conn, deliveryGuarantee: 1, AT_MOST_ONCE_TYPE);
    var outboxRowGone = await _fireAsync(conn, deliveryGuarantee: 1, AT_MOST_ONCE_TYPE);
    var guaranteeChangedLater = await _fireAsync(conn, deliveryGuarantee: 0, AT_LEAST_ONCE_TYPE);
    var atLeastOnce = await _fireAsync(conn, deliveryGuarantee: 0, AT_LEAST_ONCE_TYPE);
    var occurrences = new[] { withOutboxRow, outboxRowGone, guaranteeChangedLater, atLeastOnce };
    await _asStoredBeforeTheFlagAsync(conn, occurrences);
    await _deleteOutboxRowAsync(conn, outboxRowGone.EventId);
    await _setScheduleGuaranteeAsync(conn, guaranteeChangedLater.ScheduleId, 1);
    await _setCommitSequencesAsync(conn, occurrences, [5, 6, 7, 8]);
    var lift = await _fireAsync(conn, deliveryGuarantee: 0, AT_LEAST_ONCE_TYPE);   // puts the settled max in epoch 1
    await _setCommitSequencesAsync(conn, [lift], [105]);
    await _ageAsync(conn);
    await _closeEpochsAsync(conn);
    await Assert.That(await _epochCountAsync(conn, withOutboxRow.EventId)).IsEqualTo(2)
      .Because("precondition: the sealed epoch counted both at-most-once occurrences, as it did before the flag.");
    var generationBefore = await _originGenerationAsync(conn);

    var flagged = await _flagAtMostOnceOccurrencesAsync(conn);

    await Assert.That(flagged).IsEqualTo(2);
    await Assert.That(await _eventFlagsAsync(conn, withOutboxRow.EventId) & AT_MOST_ONCE_FLAG).IsEqualTo(AT_MOST_ONCE_FLAG)
      .Because("the guarantee the outbox row recorded at fire time identifies the occurrence.");
    await Assert.That(await _outboxFlagsAsync(conn, withOutboxRow.EventId) & AT_MOST_ONCE_FLAG).IsEqualTo(AT_MOST_ONCE_FLAG)
      .Because("an outbox row the emit chain has not yet stored is re-emitted later and must carry the flag then.");
    await Assert.That(await _eventFlagsAsync(conn, outboxRowGone.EventId) & AT_MOST_ONCE_FLAG).IsEqualTo(AT_MOST_ONCE_FLAG)
      .Because("with the outbox row gone, the schedule's guarantee is the only record left.");
    await Assert.That(await _eventFlagsAsync(conn, guaranteeChangedLater.EventId) & AT_MOST_ONCE_FLAG).IsEqualTo(0)
      .Because("the outbox row records the guarantee at fire time, which outranks a schedule changed since.");
    await Assert.That(await _eventFlagsAsync(conn, atLeastOnce.EventId) & AT_MOST_ONCE_FLAG).IsEqualTo(0);

    await Assert.That(await _digestCountAsync(conn, withOutboxRow.StreamId)).IsNull()
      .Because("the occurrence's hash is XOR-ed back out of the bucket the old fold put it in.");
    await Assert.That(await _digestCountAsync(conn, outboxRowGone.StreamId)).IsNull();
    await Assert.That(await _digestCountAsync(conn, atLeastOnce.StreamId)).IsEqualTo(1);
    await Assert.That(await _epochCountAsync(conn, withOutboxRow.EventId)).IsNull()
      .Because("the sealed epoch is refolded without them.");
    await Assert.That(await _epochCountAsync(conn, atLeastOnce.EventId)).IsEqualTo(2)
      .Because("the at-least-once occurrences stay sealed, including the one whose schedule changed afterwards.");
    await Assert.That(await _originGenerationAsync(conn)).IsEqualTo(generationBefore + 1)
      .Because("folded history changed deliberately, so consumers reset their seals instead of alarming.");

    var again = await _flagAtMostOnceOccurrencesAsync(conn);
    await Assert.That(again).IsEqualTo(0)
      .Because("a re-run of the migration finds nothing left to flag.");
    await Assert.That(await _originGenerationAsync(conn)).IsEqualTo(generationBefore + 1)
      .Because("and changes nothing a consumer has to re-verify.");
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  private sealed record Occurrence(Guid ScheduleId, Guid StreamId, Guid EventId);

  /// <summary>Creates a schedule and fires one occurrence of it through <c>wh_trigger_schedule_now</c>.</summary>
  private static async Task<Occurrence> _fireAsync(NpgsqlConnection conn, short deliveryGuarantee, string eventType) {
    var scheduleId = TrackedGuid.New().Value;
    var streamId = TrackedGuid.New().Value;
    await using (var insert = conn.CreateCommand()) {
      insert.CommandText = """
        INSERT INTO wh_schedules
          (schedule_id, stream_id, recurrence_kind, interval_ms, next_fire_at, status, event_type, event_data, scope,
           delivery_guarantee)
        VALUES (@id, @stream, 1, 3600000, NOW() + INTERVAL '1 hour', 0, @type, '{}'::jsonb, @scope::jsonb, @dg)
        """;
      insert.Parameters.AddWithValue("id", scheduleId);
      insert.Parameters.AddWithValue("stream", streamId);
      insert.Parameters.AddWithValue("type", eventType);
      insert.Parameters.AddWithValue("scope", $"{{\"t\":\"{TENANT}\"}}");
      insert.Parameters.Add(new NpgsqlParameter("dg", NpgsqlDbType.Smallint) { Value = deliveryGuarantee });
      await insert.ExecuteNonQueryAsync();
    }
    await using var fire = conn.CreateCommand();
    fire.CommandText = "SELECT o_occurrence_id FROM wh_trigger_schedule_now(@id, @instance, NOW() + INTERVAL '5 minutes', 4)";
    fire.Parameters.AddWithValue("id", scheduleId);
    fire.Parameters.AddWithValue("instance", TrackedGuid.New().Value);
    var occurrenceId = (Guid)(await fire.ExecuteScalarAsync())!;
    return new Occurrence(scheduleId, streamId, occurrenceId);
  }

  /// <summary>Puts occurrences back in the state a release without the flag stored them in.</summary>
  private static async Task _asStoredBeforeTheFlagAsync(NpgsqlConnection conn, IEnumerable<Occurrence> occurrences) {
    var ids = occurrences.Select(o => o.EventId).ToArray();
    await using (var clear = conn.CreateCommand()) {
      clear.CommandText = $"""
        UPDATE wh_event_store SET flags = flags & ~{AT_MOST_ONCE_FLAG} WHERE event_id = ANY(@ids);
        UPDATE wh_outbox SET flags = flags & ~{AT_MOST_ONCE_FLAG} WHERE message_id = ANY(@ids);
        DELETE FROM wh_stream_digests d USING wh_event_store es
        WHERE es.event_id = ANY(@ids) AND d.stream_id = es.stream_id;
        """;
      clear.Parameters.AddWithValue("ids", ids);
      await clear.ExecuteNonQueryAsync();
    }
    foreach (var id in ids) {
      await EmitChainDigestFold.FoldAsync(conn, id);
    }
  }

  private static async Task _deleteOutboxRowAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM wh_outbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _setScheduleGuaranteeAsync(NpgsqlConnection conn, Guid scheduleId, short deliveryGuarantee) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_schedules SET delivery_guarantee = @dg WHERE schedule_id = @id";
    cmd.Parameters.AddWithValue("id", scheduleId);
    cmd.Parameters.Add(new NpgsqlParameter("dg", NpgsqlDbType.Smallint) { Value = deliveryGuarantee });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _setCommitSequencesAsync(
      NpgsqlConnection conn, IReadOnlyList<Occurrence> occurrences, IReadOnlyList<long> sequences) {
    for (var i = 0; i < occurrences.Count; i++) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "UPDATE wh_event_store SET commit_sequence = @seq WHERE event_id = @id";
      cmd.Parameters.AddWithValue("seq", sequences[i]);
      cmd.Parameters.AddWithValue("id", occurrences[i].EventId);
      await cmd.ExecuteNonQueryAsync();
    }
  }

  /// <summary>Stamps every unstamped row in commit order, as the commit-sequence stamper would.</summary>
  private static async Task _stampAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      UPDATE wh_event_store es SET commit_sequence = s.seq
      FROM (SELECT event_id, nextval('wh_commit_seq') AS seq FROM wh_event_store
            WHERE commit_sequence IS NULL ORDER BY event_id) s
      WHERE es.event_id = s.event_id
      """;
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Moves every stored event outside any settle window, on the database clock.</summary>
  private static async Task _ageAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      UPDATE wh_event_store SET created_at = NOW() - INTERVAL '2 hours';
      UPDATE wh_stream_digests SET updated_at = NOW() - INTERVAL '2 hours';
      """;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _setEpochWidthAsync(NpgsqlConnection conn, long width) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('integrity_epoch_width', @w, 'integer', 'test epoch width')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """;
    cmd.Parameters.AddWithValue("w", width.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _closeEpochsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT close_digest_epochs(NOW(), 3600, 100)";
    await cmd.ExecuteScalarAsync();
  }

  /// <summary>The sealed epoch-0 count of the local-lane bucket holding <paramref name="eventId"/>'s type.</summary>
  private static async Task<int?> _epochCountAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      SELECT de.event_count FROM wh_digest_epochs de
      JOIN wh_event_store es ON es.event_type = de.event_type
      WHERE es.event_id = @id AND de.origin_service_id = '{ZERO_UUID}'::uuid AND de.epoch_id = 0
      """;
    cmd.Parameters.AddWithValue("id", eventId);
    var value = await cmd.ExecuteScalarAsync();
    return value is null or DBNull ? null : (int)value;
  }

  private static async Task<long> _originGenerationAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT setting_value::bigint FROM wh_settings WHERE setting_key = 'integrity_origin_generation'";
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<int> _flagAtMostOnceOccurrencesAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT _wh_flag_at_most_once_occurrences()";
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<int> _eventFlagsAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT flags FROM wh_event_store WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", eventId);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<int> _outboxFlagsAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT flags FROM wh_outbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<int?> _digestCountAsync(NpgsqlConnection conn, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_count FROM wh_stream_digests WHERE stream_id = @sid";
    cmd.Parameters.AddWithValue("sid", streamId);
    var value = await cmd.ExecuteScalarAsync();
    return value is null or DBNull ? null : (int)value;
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }
}
