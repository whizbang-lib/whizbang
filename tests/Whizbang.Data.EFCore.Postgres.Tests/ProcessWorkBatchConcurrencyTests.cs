using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Concurrency regression tests for the wh_active_streams UPSERT inside
/// store_outbox_messages / store_inbox_messages. Calls those functions directly rather
/// than going through the full process_work_batch request shape — the deadlock site is
/// the row-lock ordering on wh_active_streams inside those functions, and the full
/// request/envelope contract has grown heavy enough that end-to-end reproduction is
/// more setup than signal.</para>
///
/// <para>Simulates N synthetic service instances each storing outbox messages for an overlapping
/// set of streams in a shuffled order. Without deterministic stream_id-sorted UPSERT
/// ordering, two concurrent calls can lock wh_active_streams rows A→B and B→A and deadlock
/// (40P01) — matching the JDNext BFF symptom we observed in production.</para>
///
/// <para>LOCK-IN: once the fix lands (ORDER BY stream_id inside store_outbox_messages and
/// store_inbox_messages loops), zero 40P01 surfaces across N concurrent callers.</para>
/// </summary>
public class ProcessWorkBatchConcurrencyTests : EFCoreTestBase {

  private const int InstanceCount = 12;
  private const int SharedStreamCount = 8;
  private const int MessagesPerInstance = 16;

  private async Task<PostgresException?> _callStoreOutboxMessagesAsync(
      Guid instanceId,
      string messagesJson,
      CancellationToken ct) {
    try {
      await using var conn = new NpgsqlConnection(ConnectionString);
      await conn.OpenAsync(ct);
      await using var cmd = conn.CreateCommand();
      cmd.CommandTimeout = 60;
      cmd.CommandText = """
        SELECT * FROM public.store_outbox_messages(
          @p_messages::jsonb,
          @p_instance_id,
          NOW() + INTERVAL '30 seconds',
          NOW(),
          4
        )
        """;
      cmd.Parameters.Add(new NpgsqlParameter("p_messages", NpgsqlDbType.Jsonb) { Value = messagesJson });
      cmd.Parameters.Add(new NpgsqlParameter("p_instance_id", NpgsqlDbType.Uuid) { Value = instanceId });

      await using var reader = await cmd.ExecuteReaderAsync(ct);
      while (await reader.ReadAsync(ct)) { /* drain */ }
      return null;
    } catch (PostgresException pex) when (pex.SqlState == "40P01") {
      return pex;
    }
  }

  private static string _buildOutboxJson(Guid[] sharedStreamIds, int seed) {
    var rng = new Random(seed);
    var shuffled = sharedStreamIds.OrderBy(_ => rng.Next()).ToArray();
    var sb = new StringBuilder();
    sb.Append('[');
    for (var i = 0; i < MessagesPerInstance; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      var streamId = shuffled[i % shuffled.Length];
      var msgId = TrackedGuid.NewMedo().Value;
      // is_event=false so wh_event_store is bypassed — the deadlock is on wh_active_streams,
      // not the event store path.
      sb.Append('{')
        .Append("\"MessageId\":\"").Append(msgId).Append("\",")
        .Append("\"StreamId\":\"").Append(streamId).Append("\",")
        .Append("\"MessageType\":\"ConcurrencyProbeMessage\",")
        .Append("\"EnvelopeType\":\"ConcurrencyProbeEnvelope\",")
        .Append("\"Envelope\":{},")
        .Append("\"Metadata\":{},")
        .Append("\"Scope\":null,")
        .Append("\"IsEvent\":false,")
        .Append("\"Destination\":null")
        .Append('}');
    }
    sb.Append(']');
    return sb.ToString();
  }

  // ==================== CONTRACT: N concurrent store_outbox_messages calls don't deadlock ====================

  [Test]
  public async Task StoreOutboxMessages_NInstancesWithOverlappingStreams_NoDeadlocksAsync() {
    var sharedStreams = Enumerable.Range(0, SharedStreamCount)
      .Select(_ => TrackedGuid.NewMedo().Value)
      .ToArray();

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var tasks = new Task<PostgresException?>[InstanceCount];
    for (var i = 0; i < InstanceCount; i++) {
      var taskIndex = i;
      var instanceId = TrackedGuid.NewMedo().Value;
      var json = _buildOutboxJson(sharedStreams, seed: taskIndex * 37 + 7);
      tasks[i] = Task.Run(() => _callStoreOutboxMessagesAsync(instanceId, json, cts.Token), cts.Token);
    }

    var results = await Task.WhenAll(tasks);
    var deadlocks = results.Where(r => r is not null).Cast<PostgresException>().ToList();

    await Assert.That(deadlocks.Count).IsEqualTo(0)
      .Because(
        $"LOCK-IN: {InstanceCount} concurrent store_outbox_messages calls writing to " +
        $"{SharedStreamCount} overlapping streams in shuffled order must not deadlock. " +
        $"Saw {deadlocks.Count} 40P01 error(s). Fix: ORDER BY stream_id in the loop " +
        $"query so the wh_active_streams UPSERTs acquire row locks in deterministic " +
        $"order across all concurrent callers.");

    // LOCK-IN: after the refactor, store_outbox_messages does NOT touch wh_active_streams.
    // Stream ownership tracking moves to the end-of-tick UPSERT inside process_work_batch.
    // That is covered explicitly by the dedicated _DoesNotTouch_wh_active_streams_Async
    // test below — here we only assert the absence of deadlocks during the hot write path.
    await using var verifyCtx = new WorkCoordinationDbContext(DbContextOptions);
    var conn = verifyCtx.Database.GetDbConnection();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = ANY(@ids)";
    cmd.Parameters.Add(new NpgsqlParameter("ids", sharedStreams));
    var activeStreamRows = Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(activeStreamRows).IsEqualTo(0L)
      .Because("LOCK-IN: store_outbox_messages must not touch wh_active_streams post-refactor.");
  }

  // ==================== CONTRACT: single-stream contention serializes ====================

  [Test]
  public async Task StoreOutboxMessages_NInstancesAllWritingSameStream_SerializeCleanlyAsync() {
    var sharedStream = TrackedGuid.NewMedo().Value;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

    var tasks = new Task<PostgresException?>[InstanceCount];
    for (var i = 0; i < InstanceCount; i++) {
      var instanceId = TrackedGuid.NewMedo().Value;
      var messageId = TrackedGuid.NewMedo().Value;
      var json = "[{" +
        $"\"MessageId\":\"{messageId}\"," +
        $"\"StreamId\":\"{sharedStream}\"," +
        "\"MessageType\":\"ProbeMessage\"," +
        "\"EnvelopeType\":\"ProbeEnvelope\"," +
        "\"Envelope\":{}," +
        "\"Metadata\":{}," +
        "\"Scope\":null," +
        "\"IsEvent\":false," +
        "\"Destination\":null}]";
      tasks[i] = Task.Run(() => _callStoreOutboxMessagesAsync(instanceId, json, cts.Token), cts.Token);
    }

    var results = await Task.WhenAll(tasks);
    var deadlocks = results.Where(r => r is not null).Cast<PostgresException>().ToList();
    await Assert.That(deadlocks.Count).IsEqualTo(0)
      .Because("Single-stream contention must serialize without deadlock.");
  }

  // ==================== CONTRACT: store_* functions do not touch wh_active_streams ====================

  [Test]
  public async Task StoreOutboxMessages_DoesNotTouch_wh_active_streams_Async() {
    // Post-refactor, store_outbox_messages is pure wh_outbox I/O. wh_active_streams is
    // refreshed later by process_work_batch in its end-of-tick batched UPSERT.
    var streamId = TrackedGuid.NewMedo().Value;
    var instanceId = TrackedGuid.NewMedo().Value;
    var messageId = TrackedGuid.NewMedo().Value;
    var json = "[{" +
      $"\"MessageId\":\"{messageId}\"," +
      $"\"StreamId\":\"{streamId}\"," +
      "\"MessageType\":\"ProbeMessage\"," +
      "\"EnvelopeType\":\"ProbeEnvelope\"," +
      "\"Envelope\":{}," +
      "\"Metadata\":{}," +
      "\"Scope\":null," +
      "\"IsEvent\":false," +
      "\"Destination\":null}]";

    var failure = await _callStoreOutboxMessagesAsync(instanceId, json, CancellationToken.None);
    await Assert.That(failure).IsNull();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = @stream_id";
    cmd.Parameters.Add(new NpgsqlParameter("stream_id", streamId));
    var rowCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(rowCount).IsEqualTo(0L)
      .Because("LOCK-IN: store_outbox_messages must not UPSERT into wh_active_streams. " +
               "Stream ownership is refreshed at end-of-tick by process_work_batch only.");
  }

  [Test]
  public async Task StoreInboxMessages_DoesNotTouch_wh_active_streams_Async() {
    var streamId = TrackedGuid.NewMedo().Value;
    var instanceId = TrackedGuid.NewMedo().Value;
    var messageId = TrackedGuid.NewMedo().Value;
    // Minimal envelope shape accepted by store_inbox_messages.
    var json = "[{" +
      $"\"MessageId\":\"{messageId}\"," +
      $"\"StreamId\":\"{streamId}\"," +
      "\"MessageType\":\"ProbeMessage\"," +
      "\"HandlerName\":\"test\"," +
      "\"EnvelopeType\":\"ProbeEnvelope\"," +
      "\"Envelope\":{}," +
      "\"Metadata\":{}," +
      "\"Scope\":null," +
      "\"IsEvent\":false}]";

    await using var callConn = new NpgsqlConnection(ConnectionString);
    await callConn.OpenAsync();
    await using var callCmd = callConn.CreateCommand();
    callCmd.CommandText = """
      SELECT * FROM public.store_inbox_messages(
        @p_messages::jsonb,
        NULL::uuid,
        NOW() + INTERVAL '30 seconds',
        NOW(),
        4
      )
      """;
    callCmd.Parameters.Add(new NpgsqlParameter("p_messages", NpgsqlDbType.Jsonb) { Value = json });
    await using (var r = await callCmd.ExecuteReaderAsync()) {
      while (await r.ReadAsync()) { /* drain */ }
    }

    await using var verifyConn = new NpgsqlConnection(ConnectionString);
    await verifyConn.OpenAsync();
    await using var verifyCmd = verifyConn.CreateCommand();
    verifyCmd.CommandText = "SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = @stream_id";
    verifyCmd.Parameters.Add(new NpgsqlParameter("stream_id", streamId));
    var rowCount = Convert.ToInt64(await verifyCmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(rowCount).IsEqualTo(0L)
      .Because("LOCK-IN: store_inbox_messages must not UPSERT into wh_active_streams.");
  }

  // ==================== CONTRACT: process_work_batch refreshes wh_active_streams for every source ====================

  [Test]
  public async Task ProcessWorkBatch_RefreshesActiveStreamsForNewOutbox_Async() {
    // process_work_batch's end-of-tick batched UPSERT should make each stream_id from
    // NewOutboxMessages appear in wh_active_streams with assigned_instance_id = caller.
    var streamIds = Enumerable.Range(0, 4).Select(_ => TrackedGuid.NewMedo().Value).ToArray();
    var instanceId = TrackedGuid.NewMedo().Value;
    var sb = new StringBuilder();
    sb.Append('[');
    for (var i = 0; i < streamIds.Length; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      var msgId = TrackedGuid.NewMedo().Value;
      sb.Append('{')
        .Append("\"MessageId\":\"").Append(msgId).Append("\",")
        .Append("\"StreamId\":\"").Append(streamIds[i]).Append("\",")
        .Append("\"MessageType\":\"ProbeMessage\",")
        .Append("\"EnvelopeType\":\"ProbeEnvelope\",")
        .Append("\"Envelope\":{},")
        .Append("\"Metadata\":{},")
        .Append("\"Scope\":null,")
        .Append("\"IsEvent\":false,")
        .Append("\"Destination\":null}");
    }
    sb.Append(']');

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandTimeout = 60;
    cmd.CommandText = """
      SELECT * FROM public.process_work_batch(
        @p_instance_id,
        'RefreshTest',
        'refresh-host',
        1234,
        '{}'::jsonb,
        NOW(),
        30,
        4,
        '[]'::jsonb, '[]'::jsonb, '[]'::jsonb, '[]'::jsonb,
        '[]'::jsonb, '[]'::jsonb, '[]'::jsonb, '[]'::jsonb,
        @p_new_outbox,
        '[]'::jsonb,
        '[]'::jsonb,
        '[]'::jsonb, '[]'::jsonb, '[]'::jsonb,
        0,
        300,
        '[]'::jsonb,
        300
      )
      """;
    cmd.Parameters.Add(new NpgsqlParameter("p_instance_id", NpgsqlDbType.Uuid) { Value = instanceId });
    cmd.Parameters.Add(new NpgsqlParameter("p_new_outbox", NpgsqlDbType.Jsonb) { Value = sb.ToString() });
    await using (var r = await cmd.ExecuteReaderAsync()) {
      while (await r.ReadAsync()) { /* drain */ }
    }

    await using var verifyCmd = conn.CreateCommand();
    verifyCmd.CommandText = "SELECT stream_id, assigned_instance_id FROM wh_active_streams WHERE stream_id = ANY(@ids)";
    verifyCmd.Parameters.Add(new NpgsqlParameter("ids", streamIds));
    var rowsByStream = new Dictionary<Guid, Guid?>();
    await using (var reader = await verifyCmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        rowsByStream[reader.GetGuid(0)] = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
      }
    }

    await Assert.That(rowsByStream.Count).IsEqualTo(streamIds.Length)
      .Because("LOCK-IN: process_work_batch must upsert wh_active_streams for every new-outbox stream at end-of-tick.");
    foreach (var (streamId, assigned) in rowsByStream) {
      await Assert.That(assigned).IsEqualTo(instanceId)
        .Because($"Stream {streamId} was in NewOutboxMessages → this instance should be assigned owner.");
    }
  }
}
