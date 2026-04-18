using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Concurrency regression tests for the wh_active_streams UPSERT inside
/// store_outbox_messages / store_inbox_messages. Calls those functions directly rather
/// than going through the full process_work_batch request shape — the deadlock site is
/// the row-lock ordering on wh_active_streams inside those functions, and the full
/// request/envelope contract has grown heavy enough that end-to-end reproduction is
/// more setup than signal.
///
/// Simulates N synthetic service instances each storing outbox messages for an overlapping
/// set of streams in a shuffled order. Without deterministic stream_id-sorted UPSERT
/// ordering, two concurrent calls can lock wh_active_streams rows A→B and B→A and deadlock
/// (40P01) — matching the JDNext BFF symptom we observed in production.
///
/// LOCK-IN: once the fix lands (ORDER BY stream_id inside store_outbox_messages and
/// store_inbox_messages loops), zero 40P01 surfaces across N concurrent callers.
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

    // LOCK-IN: after the race, every shared stream has a row in wh_active_streams with
    // a valid assigned_instance_id (one of the N instances won it).
    await using var verifyCtx = new WorkCoordinationDbContext(DbContextOptions);
    var conn = verifyCtx.Database.GetDbConnection();
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stream_id, assigned_instance_id FROM wh_active_streams WHERE stream_id = ANY(@ids)";
    cmd.Parameters.Add(new NpgsqlParameter("ids", sharedStreams));
    var rowsByStream = new Dictionary<Guid, Guid?>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        rowsByStream[reader.GetGuid(0)] = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
      }
    }

    await Assert.That(rowsByStream.Count).IsEqualTo(SharedStreamCount)
      .Because("Every shared stream must appear exactly once in wh_active_streams.");
    foreach (var (streamId, assignedInstance) in rowsByStream) {
      await Assert.That(assignedInstance).IsNotNull()
        .Because($"Stream {streamId} must have a winning assigned_instance_id after the race.");
    }
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
}
