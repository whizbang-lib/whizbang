// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using Whizbang.Core.Priority;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The claim re-offers only the held streams the drain can fetch (#2117, #2118).
/// </summary>
/// <remarks>
/// <para>
/// A received event that sorts after an earlier pending event of its stream, which this instance does not hold, is
/// held back by the store pass (230): it stays unstored, and <c>fetch_inbox_batch</c> never returns an unstored event.
/// The re-offer walked every held row regardless, most urgent bucket first, so such a stream was handed to the drain
/// on every poll, fetched as nothing, and kept its place at the head of the batch.
/// </para>
/// <para>
/// Under load the outstanding budget narrows the re-offer to a single stream, and that stream was always the held-back
/// one. Every other row the claim acquired was charged an attempt and never offered, so it was never fetched, never
/// dispatched, and never reached the dispatcher's attempt ceiling: its lease lapsed, the next claim took it again, and
/// it climbed attempts for days without completing or dead-lettering. The rows starved this way included received
/// integrity checkpoints, so the consumer never learned who owns the types it consumes.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/349_ReOfferOnlyFetchableStreams.sql</code-under-test>
/// <docs>fundamentals/work-coordinator/claim-loop#the-re-offer-hands-on-only-what-the-drain-can-fetch</docs>
[Category("Shard5")]
public class HeldBackEventsDoNotCrowdTheReOfferTests : EFCoreTestBase {

  private const string CHECKPOINT_TYPE = "Whizbang.Core.Messaging.IntegrityCheckpoint, Whizbang.Core";

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task _heartbeatAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@i, 'ReOfferSvc', 'host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    cmd.Parameters.AddWithValue("i", instance);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Seeds one pending inbox row. A non-null <paramref name="heldBy"/> leases it to that instance under a live lease;
  /// <paramref name="notDueForMinutes"/> keeps an unleased row out of the claim's reach without making it processed.
  /// </summary>
  private static async Task _seedAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, string messageType, bool isEvent, int priority,
      int attempts, int ageMinutes, Guid? heldBy, int notDueForMinutes = 0) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at,
           stream_id, is_event, priority)
        VALUES (@msg, 'TestHandler', @type, '{"p": {}}', '{}',
                NOW() - (@age * INTERVAL '1 minute'), @stream, @isEvent, @priority)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts,
         partition_number, instance_id, lease_expiry, scheduled_for)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, @attempts, 0,
             @heldBy, CASE WHEN @heldBy::uuid IS NULL THEN NULL ELSE NOW() + INTERVAL '5 minutes' END,
             CASE WHEN @notDue = 0 THEN NULL ELSE NOW() + (@notDue * INTERVAL '1 minute') END
      FROM m
      """;
    cmd.Parameters.AddWithValue("msg", messageId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("type", messageType);
    cmd.Parameters.AddWithValue(nameof(isEvent), isEvent);
    cmd.Parameters.AddWithValue("age", ageMinutes);
    cmd.Parameters.AddWithValue(nameof(priority), priority);
    cmd.Parameters.AddWithValue(nameof(attempts), attempts);
    cmd.Parameters.AddWithValue("notDue", notDueForMinutes);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(heldBy), NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)heldBy ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Seeds a held-back interactive event: the stream's earlier event is pending, unleased and not yet due, so the
  /// store pass leaves the held one unstored. Returns the stream.
  /// </summary>
  private static async Task<Guid> _seedHeldBackStreamAsync(NpgsqlConnection conn, Guid instance) {
    var streamId = Guid.CreateVersion7();
    // The store pass orders a stream by message id, as Postgres compares uuids (their text, byte by byte). Two version 7
    // ids minted in the same millisecond are not ordered by minting, so the pair is sorted the way the database sorts it.
    var (predecessor, held) = _ordered(Guid.CreateVersion7(), Guid.CreateVersion7());
    await _seedAsync(conn, predecessor, streamId, "Test.Domain.ThingHappened, Test", isEvent: true,
      priority: WorkPriority.BACKGROUND, attempts: 0, ageMinutes: 10, heldBy: null, notDueForMinutes: 60);
    await _seedAsync(conn, held, streamId, "Test.Domain.ThingChanged, Test", isEvent: true,
      priority: WorkPriority.INTERACTIVE, attempts: 1, ageMinutes: 5, heldBy: instance);
    return streamId;
  }

  private static (Guid First, Guid Second) _ordered(Guid a, Guid b) =>
    string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? (a, b) : (b, a);

  /// <summary>Runs one claim whose re-offer the outstanding budget has narrowed to one stream.</summary>
  private static async Task<HashSet<Guid>> _claimOneStreamAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT work_stream_id FROM claim_work(
        p_instance_id => @i,
        p_service_name => 'ReOfferSvc',
        p_host_name => 'host',
        p_process_id => 1,
        p_max_streams => 1,
        p_partition_count => 10000,
        p_lease_seconds => 300,
        p_max_rows => 1)
      WHERE source = 'inbox' AND work_stream_id IS NOT NULL
      """;
    cmd.Parameters.AddWithValue("i", instance);
    cmd.CommandTimeout = 120;
    var offered = new HashSet<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      offered.Add(reader.GetGuid(0));
    }
    return offered;
  }

  private static async Task<int> _fetchCountAsync(NpgsqlConnection conn, Guid streamId, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*)::int FROM fetch_inbox_batch(@s, @i, 100, NULL)";
    cmd.Parameters.AddWithValue("s", new[] { streamId });
    cmd.Parameters.AddWithValue("i", instance);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>
  /// The reported shape: a held-back interactive event and a received integrity checkpoint, both held, and a re-offer
  /// of one stream. The checkpoint's stream is the one the drain can fetch, so it is the one handed on.
  /// </summary>
  [Test]
  public async Task OneStreamReOffer_PassesOverAHeldBackEvent_ToAFetchableCheckpointAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.NewGuid();
    await _heartbeatAsync(conn, instance);
    var heldBackStream = await _seedHeldBackStreamAsync(conn, instance);
    var checkpointStream = Guid.CreateVersion7();
    await _seedAsync(conn, Guid.CreateVersion7(), checkpointStream, CHECKPOINT_TYPE, isEvent: false,
      priority: WorkPriority.BACKGROUND, attempts: 1, ageMinutes: 1, heldBy: instance);

    var offered = await _claimOneStreamAsync(conn, instance);
    var heldBackFetched = await _fetchCountAsync(conn, heldBackStream, instance);

    await Assert.That(heldBackFetched).IsEqualTo(0)
      .Because("the held-back event is unstored after the claim's store pass, and the drain's fetch never returns an "
        + "unstored event; that is the precondition this case is about");
    await Assert.That(offered.Contains(checkpointStream)).IsTrue()
      .Because("a re-offer narrowed to one stream must spend it on a stream the drain can fetch; spending it on the "
        + "held-back stream left the checkpoint leased, charged and never dispatched, for ever");
    await Assert.That(offered.Contains(heldBackStream)).IsFalse()
      .Because("a stream whose held rows the drain cannot fetch is never handed to it");
  }

  /// <summary>
  /// Once the held-back event's predecessor is stored, the event is stored too and its stream is fetchable again, so it
  /// is re-offered like any other: the filter passes over a stream only while the drain cannot fetch it.
  /// </summary>
  [Test]
  public async Task HeldBackStream_OnceItsPredecessorIsHeldToo_IsStoredAndReOfferedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.NewGuid();
    await _heartbeatAsync(conn, instance);
    var heldBackStream = await _seedHeldBackStreamAsync(conn, instance);
    await using (var cmd = conn.CreateCommand()) {
      // The predecessor becomes due and is leased to the same instance: nothing is held back any longer.
      cmd.CommandText = """
        UPDATE wh_inbox_state SET scheduled_for = NULL, instance_id = @i, lease_expiry = NOW() + INTERVAL '5 minutes'
        WHERE stream_id = @s AND instance_id IS NULL
        """;
      cmd.Parameters.AddWithValue("i", instance);
      cmd.Parameters.AddWithValue("s", heldBackStream);
      await cmd.ExecuteNonQueryAsync();
    }

    var offered = await _claimOneStreamAsync(conn, instance);
    var fetched = await _fetchCountAsync(conn, heldBackStream, instance);

    await Assert.That(fetched).IsEqualTo(2)
      .Because("with its predecessor held by the same instance the store pass stores both events in order");
    await Assert.That(offered.Contains(heldBackStream)).IsTrue()
      .Because("a stream the drain can fetch is re-offered whatever it held back before");
  }
}
