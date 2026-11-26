// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Sequence integrity in the outbox (#2542): the store writes each event's hold, the drain's fetch and continuation cut
/// a stream at its first event held for its commit-order stamp, the stamp rings the outbox owner so the held event goes
/// out as soon as it is stamped, and the held-age reading finds the oldest held event an instance holds.
/// </summary>
/// <remarks>
/// Every instant a test depends on is either the database's own (a hold set far in the future or in the past relative to
/// <c>NOW()</c>) or a parameter, so nothing races the clock.
/// </remarks>
/// <docs>fundamentals/messaging/sequence-integrity</docs>
[Category("Shard2")]
public class SequenceIntegritySqlTests : EFCoreTestBase {
  private const string TOPIC = "orders";
  private const string FAR_FUTURE = "NOW() + INTERVAL '1 hour'";
  private const string PAST = "NOW() - INTERVAL '1 hour'";
  private const string UNTIL_STAMPED = "'infinity'::timestamptz";

  // ============================================================================
  // store_outbox_messages
  // ============================================================================

  [Test]
  public async Task Store_WritesEachModesHoldOntoAnEventsRow_AndNeverOntoACommandsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    var stream = (Guid)TrackedGuid.New();

    var none = _event(stream, 0);
    var untilStamped = _event(stream, SequenceHold.UNTIL_STAMPED_MILLISECONDS);
    var bounded = _event(stream, 1500);
    var command = CreateTestOutboxMessage((Guid)TrackedGuid.New(), TOPIC, (Guid)TrackedGuid.New()) with {
      SequenceHoldMilliseconds = SequenceHold.UNTIL_STAMPED_MILLISECONDS
    };
    var storedOnly = _event(stream, 1500) with { Destination = null };
    var options = JsonContextRegistry.CreateCombinedOptions();
    var json = JsonSerializer.Serialize(new[] { none, untilStamped, bounded, command, storedOnly }, options.GetTypeInfo(typeof(OutboxMessage[])));

    await using (var store = conn.CreateCommand()) {
      store.CommandText = "SELECT count(*) FROM store_outbox_messages(@messages::jsonb, NULL::uuid, NULL::timestamptz, @now, 10000)";
      store.Parameters.AddWithValue("messages", json);
      store.Parameters.Add(new NpgsqlParameter(nameof(now), NpgsqlDbType.TimestampTz) { Value = now });
      _ = await store.ExecuteScalarAsync();
    }

    await Assert.That(await _holdAsync(conn, none.MessageId, now)).IsEqualTo("none");
    await Assert.That(await _holdAsync(conn, untilStamped.MessageId, now)).IsEqualTo("until-stamped");
    await Assert.That(await _holdAsync(conn, bounded.MessageId, now)).IsEqualTo("1500")
      .Because("a bounded hold runs out its milliseconds after the store's own clock");
    await Assert.That(await _holdAsync(conn, command.MessageId, now)).IsEqualTo("none")
      .Because("only an event is stamped, so only an event can wait for its stamp");
    await Assert.That(await _holdAsync(conn, storedOnly.MessageId, now)).IsEqualTo("none")
      .Because("an event that is only stored is never published, so there is nothing to hold");
  }

  // ============================================================================
  // fetch_outbox_batch
  // ============================================================================

  [Test]
  public async Task Fetch_AnUnstampedEventWithNoHold_IsPublishedAtOnceAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _instanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var racing = await _rowAsync(conn, instance, stream, hold: null, stored: true, stamped: false);

    await Assert.That(await _fetchAsync(conn, instance, stream)).IsEquivalentTo([racing])
      .Because("an event with no hold races the stamp, as every event did before");
  }

  [Test]
  public async Task Fetch_AnUnstampedEventWithinItsHold_IsHeld_AndPublishedOnceStampedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _instanceAsync(conn);
    var boundedStream = (Guid)TrackedGuid.New();
    var stampedStream = (Guid)TrackedGuid.New();
    var bounded = await _rowAsync(conn, instance, boundedStream, FAR_FUTURE, stored: true, stamped: false);
    var untilStamped = await _rowAsync(conn, instance, stampedStream, UNTIL_STAMPED, stored: true, stamped: false);

    await Assert.That(await _fetchAsync(conn, instance, boundedStream, stampedStream)).IsEmpty()
      .Because("neither event has a commit sequence yet, and neither hold has run out");

    await _stampAsync(conn, bounded);
    await _stampAsync(conn, untilStamped);

    await Assert.That(await _fetchAsync(conn, instance, boundedStream, stampedStream)).IsEquivalentTo([bounded, untilStamped])
      .Because("the stamp releases the hold; the event now carries its origin position");
  }

  [Test]
  public async Task Fetch_ABoundedHoldThatRanOut_IsPublishedUnstampedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _instanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var expired = await _rowAsync(conn, instance, stream, PAST, stored: true, stamped: false);

    await Assert.That(await _fetchAsync(conn, instance, stream)).IsEquivalentTo([expired])
      .Because("a bounded hold publishes anyway once its time has passed");
  }

  [Test]
  public async Task Fetch_ARowBehindAHeldEvent_IsHeldWhateverItsOwnSettingAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _instanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var other = (Guid)TrackedGuid.New();
    var first = await _rowAsync(conn, instance, stream, FAR_FUTURE, stored: true, stamped: true);
    _ = await _rowAsync(conn, instance, stream, UNTIL_STAMPED, stored: true, stamped: false);
    _ = await _rowAsync(conn, instance, stream, hold: null, stored: true, stamped: false);
    _ = await _rowAsync(conn, instance, stream, hold: null, stored: false, stamped: false, isEvent: false);
    var neighbor = await _rowAsync(conn, instance, other, hold: null, stored: true, stamped: false);

    await Assert.That(await _fetchAsync(conn, instance, stream, other)).IsEquivalentTo([first, neighbor])
      .Because("a racing event and a command behind a held event wait with it, or the receiver would see the stream out of "
             + "order; another stream is not held back");
  }

  [Test]
  public async Task Fetch_AHoldWithNoStoredEvent_HoldsNothingAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _instanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var unstored = await _rowAsync(conn, instance, stream, UNTIL_STAMPED, stored: false, stamped: false);

    await Assert.That(await _fetchAsync(conn, instance, stream)).IsEquivalentTo([unstored])
      .Because("a row with no event-store row is never stamped, so holding it until stamped would hold it forever");
  }

  [Test]
  public async Task Fetch_UnderAByteBudget_StillCutsAtTheHeldEventAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _instanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var first = await _rowAsync(conn, instance, stream, hold: null, stored: true, stamped: true);
    _ = await _rowAsync(conn, instance, stream, UNTIL_STAMPED, stored: true, stamped: false);

    await Assert.That(await _fetchAsync(conn, instance, maxBytes: 1_000_000, stream)).IsEquivalentTo([first]);
  }

  // ============================================================================
  // wh_continue_outbox_streams
  // ============================================================================

  [Test]
  public async Task Continue_StopsAStreamAtItsHeldEvent_AsTheFetchDoesAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _instanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    await _ownStreamAsync(conn, stream, instance);
    var first = await _rowAsync(conn, instance, stream, hold: null, stored: true, stamped: true);
    _ = await _rowAsync(conn, instance, stream, FAR_FUTURE, stored: true, stamped: false);
    _ = await _rowAsync(conn, instance, stream, hold: null, stored: true, stamped: false);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT message_id FROM wh_continue_outbox_streams(@inst, @streams, @after, 10)";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue("streams", new[] { stream });
    cmd.Parameters.AddWithValue("after", new[] { Guid.Empty });
    var continued = new List<Guid>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        continued.Add(reader.GetGuid(0));
      }
    }

    await Assert.That(continued).IsEquivalentTo([first])
      .Because("the drain continues a stream through this function, so it must hold what the fetch holds");
  }

  // ============================================================================
  // stamp_pending_commit_sequences: the stamp releases the hold
  // ============================================================================

  [Test]
  public async Task Stamp_AStreamWithPendingOutboxRows_RingsItsOutboxOwnerAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _instanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    await _ownStreamAsync(conn, stream, instance);
    _ = await _rowAsync(conn, instance, stream, UNTIL_STAMPED, stored: true, stamped: false);

    var received = await _captureDoorbellsAsync(conn, instance, async () => await Assert.That(await _stampPendingAsync(conn)).IsEqualTo(1));

    await Assert.That(received).Contains("outbox")
      .Because("the stamp is what releases a held event, so it must wake the drain that holds it, not leave it to the "
             + "hold's deadline or the next poll");
  }

  [Test]
  public async Task Stamp_AStreamWithNoPendingOutboxRows_DoesNotRingTheOutboxAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _instanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    await _ownStreamAsync(conn, stream, instance);
    await _eventStoreRowAsync(conn, (Guid)TrackedGuid.New(), stream, stamped: false);

    var received = await _captureDoorbellsAsync(conn, instance, async () => await Assert.That(await _stampPendingAsync(conn)).IsEqualTo(1));

    await Assert.That(received).Contains("perspective");
    await Assert.That(received).DoesNotContain("outbox")
      .Because("a stream with nothing waiting in the outbox has nothing for the drain to release");
  }

  // ============================================================================
  // wh_oldest_sequence_held_outbox
  // ============================================================================

  [Test]
  public async Task OldestHeld_IsTheArrivalOfTheOldestEventThisInstanceHoldsForItsStampAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _instanceAsync(conn);
    var peer = await _instanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var at = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    await Assert.That(await _oldestHeldAsync(conn, instance, 1000)).IsNull()
      .Because("an instance holding nothing reports no age");

    _ = await _rowAsync(conn, peer, (Guid)TrackedGuid.New(), UNTIL_STAMPED, stored: true, stamped: false, createdAt: at.AddMinutes(-10));
    _ = await _rowAsync(conn, instance, stream, UNTIL_STAMPED, stored: true, stamped: true, createdAt: at.AddMinutes(-5));
    _ = await _rowAsync(conn, instance, (Guid)TrackedGuid.New(), PAST, stored: true, stamped: false, createdAt: at.AddMinutes(-4));
    _ = await _rowAsync(conn, instance, (Guid)TrackedGuid.New(), hold: null, stored: true, stamped: false, createdAt: at.AddMinutes(-3));
    _ = await _rowAsync(conn, instance, stream, FAR_FUTURE, stored: true, stamped: false, createdAt: at.AddMinutes(-2));
    _ = await _rowAsync(conn, instance, (Guid)TrackedGuid.New(), UNTIL_STAMPED, stored: true, stamped: false, createdAt: at.AddMinutes(-1));

    await Assert.That(await _oldestHeldAsync(conn, instance, 1000)).IsEqualTo(at.AddMinutes(-2))
      .Because("a peer's held event, a stamped event, a hold that ran out and an event with no hold are not held here");
  }

  [Test]
  public async Task OldestHeld_StopsAtItsBoundAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _instanceAsync(conn);
    var at = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    _ = await _rowAsync(conn, instance, (Guid)TrackedGuid.New(), hold: null, stored: true, stamped: false, createdAt: at.AddMinutes(-2));
    _ = await _rowAsync(conn, instance, (Guid)TrackedGuid.New(), UNTIL_STAMPED, stored: true, stamped: false, createdAt: at.AddMinutes(-1));

    await Assert.That(await _oldestHeldAsync(conn, instance, 1)).IsNull()
      .Because("the reading walks at most its bound of the instance's oldest rows, whatever the instance holds");
    await Assert.That(await _oldestHeldAsync(conn, instance, 2)).IsEqualTo(at.AddMinutes(-1));
  }

  [Test]
  public async Task Coordinator_ReadsTheOldestHeldThroughTheFunctionAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _instanceAsync(conn);
    var at = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    IWorkCoordinator coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonContextRegistry.CreateCombinedOptions());
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1,
      logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorGate>.Instance);
    IWorkCoordinator gated = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonContextRegistry.CreateCombinedOptions(), gate: gate);

    await Assert.That(await coordinator.GetOldestSequenceHeldOutboxAsync(instance, 10)).IsNull();

    _ = await _rowAsync(conn, instance, (Guid)TrackedGuid.New(), UNTIL_STAMPED, stored: true, stamped: false, createdAt: at);

    await Assert.That(await coordinator.GetOldestSequenceHeldOutboxAsync(instance, 10)).IsEqualTo(at);
    await Assert.That(await gated.GetOldestSequenceHeldOutboxAsync(instance, 10)).IsEqualTo(at)
      .Because("a coordinator with a gate takes a permit for the reading, as for every other statement");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static OutboxMessage _event(Guid stream, int holdMilliseconds) =>
    CreateTestOutboxMessage((Guid)TrackedGuid.New(), TOPIC, stream, isEvent: true) with {
      SequenceHoldMilliseconds = holdMilliseconds
    };

  private static async Task<string> _holdAsync(NpgsqlConnection conn, Guid messageId, DateTimeOffset now) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT CASE
               WHEN sequence_hold_until IS NULL THEN 'none'
               WHEN sequence_hold_until = 'infinity'::timestamptz THEN 'until-stamped'
               ELSE round(EXTRACT(EPOCH FROM (sequence_hold_until - @now)) * 1000)::TEXT
             END
      FROM wh_outbox WHERE message_id = @id
      """;
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(now), NpgsqlDbType.TimestampTz) { Value = now });
    return (string?)await cmd.ExecuteScalarAsync()
      ?? throw new InvalidOperationException("the message was not stored; the probe proves nothing");
  }

  private static async Task<Guid> _rowAsync(
      NpgsqlConnection conn, Guid instance, Guid stream, string? hold, bool stored, bool stamped,
      bool isEvent = true, DateTimeOffset? createdAt = null) {
    var id = (Guid)TrackedGuid.New();
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = $$"""
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
           created_at, stream_id, partition_number, instance_id, lease_expiry, is_event, sequence_hold_until)
        VALUES (@id, '{{TOPIC}}', 'TestEvent', 'TestEnvelope', '{"payload":1}', '{"hop":1}', 1, 1,
                COALESCE(@created, NOW()), @stream, 0, @inst, NOW() + INTERVAL '5 minutes', @is_event, {{hold ?? "NULL"}})
        """;
      ins.Parameters.AddWithValue(nameof(id), id);
      ins.Parameters.AddWithValue(nameof(stream), stream);
      ins.Parameters.AddWithValue("inst", instance);
      ins.Parameters.AddWithValue("is_event", isEvent);
      ins.Parameters.Add(new NpgsqlParameter("created", NpgsqlDbType.TimestampTz) { Value = (object?)createdAt ?? DBNull.Value });
      await ins.ExecuteNonQueryAsync();
    }
    if (stored) {
      await _eventStoreRowAsync(conn, id, stream, stamped);
    }
    return id;
  }

  private static async Task _eventStoreRowAsync(NpgsqlConnection conn, Guid eventId, Guid stream, bool stamped) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = $"""
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at, commit_sequence)
      VALUES
        (@id, @stream, @stream, 'TestAggregate',
         (SELECT COALESCE(MAX(version), 0) + 1 FROM wh_event_store WHERE stream_id = @stream),
         'TestEvent', NULL, NOW(), {(stamped ? "nextval('wh_commit_seq')" : "NULL")})
      """;
    ins.Parameters.AddWithValue("id", eventId);
    ins.Parameters.AddWithValue(nameof(stream), stream);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _stampAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_event_store SET commit_sequence = nextval('wh_commit_seq') WHERE event_id = @id AND commit_sequence IS NULL";
    cmd.Parameters.AddWithValue("id", eventId);
    if (await cmd.ExecuteNonQueryAsync() != 1) {
      throw new InvalidOperationException("the event was not unstamped; the probe proves nothing");
    }
  }

  private static async Task<int> _stampPendingAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stamp_pending_commit_sequences(100)";
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static Task<List<Guid>> _fetchAsync(NpgsqlConnection conn, Guid instance, params Guid[] streams) =>
    _fetchAsync(conn, instance, null, streams);

  private static async Task<List<Guid>> _fetchAsync(NpgsqlConnection conn, Guid instance, long? maxBytes, params Guid[] streams) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT message_id FROM fetch_outbox_batch(@streams, @inst, 100, @max_bytes)";
    cmd.Parameters.Add(new NpgsqlParameter(nameof(streams), NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = streams });
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.Add(new NpgsqlParameter("max_bytes", NpgsqlDbType.Bigint) { Value = (object?)maxBytes ?? DBNull.Value });
    var rows = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetGuid(0));
    }
    return rows;
  }

  private static async Task<DateTimeOffset?> _oldestHeldAsync(NpgsqlConnection conn, Guid instance, int maxRows) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_oldest_sequence_held_outbox(@inst, @max)";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue("max", maxRows);
    var result = await cmd.ExecuteScalarAsync();
    return result is DateTime value ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : null;
  }

  private static async Task<Guid> _instanceAsync(NpgsqlConnection conn) {
    var id = (Guid)TrackedGuid.New();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()
      """;
    cmd.Parameters.AddWithValue(nameof(id), id);
    await cmd.ExecuteNonQueryAsync();
    return id;
  }

  private static async Task _ownStreamAsync(NpgsqlConnection conn, Guid stream, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry, last_activity_at)
      VALUES (@stream, 0, @inst, NOW() + INTERVAL '5 minutes', NOW())
      ON CONFLICT (stream_id) DO UPDATE SET assigned_instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes'
      """;
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    cmd.Parameters.AddWithValue("inst", instance);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<List<string>> _captureDoorbellsAsync(NpgsqlConnection conn, Guid instance, Func<Task> emit) {
    var channel = $"wh_work_i_{instance}";
    var received = new List<string>();
    void handler(object sender, NpgsqlNotificationEventArgs args) {
      if (string.Equals(args.Channel, channel, StringComparison.Ordinal)) {
        received.Add(args.Payload);
      }
    }
    conn.Notification += handler;
    try {
      await using (var listen = conn.CreateCommand()) {
        listen.CommandText = $"LISTEN \"{channel}\"";
        await listen.ExecuteNonQueryAsync();
      }

      await emit();
      // The stamp queues its doorbells; the driver rings them after the commit (146), as DoorbellRinger does here.
      _ = await DoorbellRinger.RingAsync(conn, DoorbellRinger.FUNCTION_NAME, null, CancellationToken.None);

      // A round trip, so every NOTIFY the ring sent is dispatched to the handler before the list is read.
      await using var ping = conn.CreateCommand();
      ping.CommandText = "SELECT 1";
      _ = await ping.ExecuteScalarAsync();
    } finally {
      conn.Notification -= handler;
      await using var unlisten = conn.CreateCommand();
      unlisten.CommandText = $"UNLISTEN \"{channel}\"";
      await unlisten.ExecuteNonQueryAsync();
    }
    return received;
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }
}
