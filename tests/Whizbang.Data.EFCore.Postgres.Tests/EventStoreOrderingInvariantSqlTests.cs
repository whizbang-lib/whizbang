using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration-layer regression tests for the a consumer 2026-05-04 cursor-inversion symptom.
/// Asserts that <c>wh_event_store</c> preserves UUIDv7 ordering across concurrent inserts
/// for the same stream. RED here = ordering bug at the SQL/Postgres storage layer.
/// </summary>
[Category("Integration")]
[NotInParallel("PostgreSQL")]
public class EventStoreOrderingInvariantSqlTests : EFCoreTestBase {

  /// <summary>
  /// Sequential inserts via direct SQL: 500 events written one at a time with monotonic
  /// UUIDv7 IDs from a single thread. Reading back ordered by event_id ASC must match
  /// insertion order exactly.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task SequentialInserts_ReturnedInMonotonicEventIdOrderAsync(CancellationToken ct) {
    var streamId = Guid.NewGuid();
    var ids = new List<Guid>();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);

    for (var i = 0; i < 500; i++) {
      var id = (Guid)TrackedGuid.NewMedo();
      ids.Add(id);
      await using var cmd = new NpgsqlCommand(
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, created_at, version) " +
        "VALUES (@event_id, @stream_id, @stream_id, 'Test', 'Test', '{}', '{}', NOW(), @version)", conn);
      cmd.Parameters.AddWithValue("event_id", id);
      cmd.Parameters.AddWithValue("stream_id", streamId);
      cmd.Parameters.AddWithValue("version", i + 1);
      await cmd.ExecuteNonQueryAsync(ct);
    }

    await using var readCmd = new NpgsqlCommand(
      "SELECT event_id FROM wh_event_store WHERE stream_id = @stream_id ORDER BY event_id ASC", conn);
    readCmd.Parameters.AddWithValue("stream_id", streamId);
    var read = new List<Guid>();
    await using (var reader = await readCmd.ExecuteReaderAsync(ct)) {
      while (await reader.ReadAsync(ct)) {
        read.Add(reader.GetGuid(0));
      }
    }

    var expectedSorted = ids.OrderBy(g => g.ToString("D"), StringComparer.Ordinal).ToArray();
    await Assert.That(read.SequenceEqual(expectedSorted)).IsTrue()
      .Because("Reading back wh_event_store ORDER BY event_id ASC must produce the lex-sorted UUIDv7 sequence.");
    await Assert.That(read.SequenceEqual(ids)).IsTrue()
      .Because("Sequential single-thread NewMedo() generation produces monotonic IDs, so insertion order MUST equal event_id-sorted order.");
  }

  /// <summary>
  /// Concurrent inserts to the same stream: 16 tasks each insert 100 events.
  /// After all writes, the wh_event_store rows for this stream must be queryable
  /// in monotonic event_id order. The ORDER BY event_id ASC clause is what the
  /// drainer + perspective runner rely on — if generation produces non-monotonic
  /// IDs (TrackedGuid contention bug), this test goes RED to localize the issue.
  /// </summary>
  [Test, Timeout(60000)]
  public async Task ConcurrentInserts_ProducedIdsAreMonotonicByGenerationOrderAsync(CancellationToken ct) {
    var streamId = Guid.NewGuid();
    const int taskCount = 16;
    const int perTask = 100;
    var stamps = new List<(long Tick, Guid Id)>[taskCount];

    var startGate = new SemaphoreSlim(0, taskCount);
    var versionCounter = 0;
    var tasks = new Task[taskCount];
    for (var t = 0; t < taskCount; t++) {
      var localIdx = t;
      stamps[localIdx] = new List<(long, Guid)>(perTask);
      tasks[localIdx] = Task.Run(async () => {
        await using var localConn = new NpgsqlConnection(ConnectionString);
        await localConn.OpenAsync(ct);
        await startGate.WaitAsync(ct);
        for (var i = 0; i < perTask; i++) {
          var tick = Stopwatch.GetTimestamp();
          var id = (Guid)TrackedGuid.NewMedo();
          stamps[localIdx].Add((tick, id));
          var version = Interlocked.Increment(ref versionCounter);
          await using var cmd = new NpgsqlCommand(
            "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, created_at, version) " +
            "VALUES (@event_id, @stream_id, @stream_id, 'Test', 'Test', '{}', '{}', NOW(), @version)", localConn);
          cmd.Parameters.AddWithValue("event_id", id);
          cmd.Parameters.AddWithValue("stream_id", streamId);
          cmd.Parameters.AddWithValue("version", version);
          await cmd.ExecuteNonQueryAsync(ct);
        }
      }, ct);
    }
    startGate.Release(taskCount);
    await Task.WhenAll(tasks);

    // Combine all generated IDs in real-time tick order.
    var allByTime = stamps.SelectMany(s => s).OrderBy(t => t.Tick).Select(t => t.Id).ToArray();

    // Count inversions: for each adjacent pair in real-time order, the later one's lex value
    // should be > earlier's. Each violation is an inversion produced at the generator layer.
    var inversions = 0;
    for (var i = 1; i < allByTime.Length; i++) {
      if (string.Compare(allByTime[i].ToString("D"), allByTime[i - 1].ToString("D"), StringComparison.Ordinal) < 0) {
        inversions++;
      }
    }

    await Assert.That(inversions).IsEqualTo(0)
      .Because($"Concurrent NewMedo() calls must produce lex-monotonic IDs in real-time generation order. Found {inversions}/{allByTime.Length - 1} inversions. RED here reproduces the a consumer 2026-05-04 producer-layer bug at the integration boundary against real Postgres.");
  }

  /// <summary>
  /// End-to-end ordering invariant: even if the producer's IDs are non-monotonic,
  /// the wh_event_store ORDER BY event_id query produces a deterministic lex-sorted
  /// view. The runner's idempotency guard string-compare relies on this. Locks the
  /// SQL contract — RED here would mean Postgres ORDER BY itself is broken (very
  /// unlikely; this is a sanity test for the layer below the producer bug).
  /// </summary>
  [Test, Timeout(60000)]
  public async Task ReadByEventIdAsc_OrderingIsLexicallyDeterministicAsync(CancellationToken ct) {
    var streamId = Guid.NewGuid();
    var ids = Enumerable.Range(0, 50).Select(_ => (Guid)TrackedGuid.NewMedo()).ToList();
    // Shuffle so insertion order differs from sorted order
    var shuffled = ids.OrderBy(_ => Random.Shared.Next()).ToArray();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    var version = 0;
    foreach (var id in shuffled) {
      version++;
      await using var cmd = new NpgsqlCommand(
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, created_at, version) " +
        "VALUES (@event_id, @stream_id, @stream_id, 'Test', 'Test', '{}', '{}', NOW(), @version)", conn);
      cmd.Parameters.AddWithValue("event_id", id);
      cmd.Parameters.AddWithValue("stream_id", streamId);
      cmd.Parameters.AddWithValue("version", version);
      await cmd.ExecuteNonQueryAsync(ct);
    }

    await using var readCmd = new NpgsqlCommand(
      "SELECT event_id FROM wh_event_store WHERE stream_id = @stream_id ORDER BY event_id ASC", conn);
    readCmd.Parameters.AddWithValue("stream_id", streamId);
    var read = new List<Guid>();
    await using (var reader = await readCmd.ExecuteReaderAsync(ct)) {
      while (await reader.ReadAsync(ct)) {
        read.Add(reader.GetGuid(0));
      }
    }

    var expectedSorted = ids.OrderBy(g => g.ToString("D"), StringComparer.Ordinal).ToArray();
    await Assert.That(read.SequenceEqual(expectedSorted)).IsTrue()
      .Because("Postgres ORDER BY event_id ASC on UUID column must produce lex-sorted results regardless of insertion order.");
  }
}
