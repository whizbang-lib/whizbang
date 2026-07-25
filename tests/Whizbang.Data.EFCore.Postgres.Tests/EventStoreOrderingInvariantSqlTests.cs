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
/// Integration-layer regression tests for a production cursor-inversion symptom.
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
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, created_at, version) " +
        "VALUES (@event_id, @stream_id, @stream_id, 'Test', 'Test', NOW(), @version)", conn);
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
  /// Concurrent inserts to the same stream: 16 tasks each generate 100 IDs and insert into
  /// wh_event_store. After all writes complete, reading ORDER BY event_id ASC must return
  /// every ID without duplicates. AND each thread's local sequence of generated IDs must be
  /// monotonic (the lock fix's per-thread guarantee).
  ///
  /// <para>
  /// Production reproduction at the integration boundary: without TrackedGuid's internal
  /// lock, concurrent receptors generating events for the same stream produce non-monotonic
  /// IDs that get inserted into wh_event_store. The runner's cursor-inversion detector then
  /// fires repeated full replays. With the lock, each thread's sequence is monotonic and
  /// no duplicates can occur — so the SQL ORDER BY event_id sort lines up with intent.
  /// </para>
  /// </summary>
  [Test, Timeout(60000)]
  public async Task ConcurrentInserts_PerThreadMonotonic_NoDuplicates_SqlSortLinesUpAsync(CancellationToken ct) {
    var streamId = Guid.NewGuid();
    const int taskCount = 16;
    const int perTask = 100;
    var perThreadIds = new List<Guid>[taskCount];

    var startGate = new SemaphoreSlim(0, taskCount);
    var versionCounter = 0;
    var tasks = new Task[taskCount];
    for (var t = 0; t < taskCount; t++) {
      var localIdx = t;
      perThreadIds[localIdx] = new List<Guid>(perTask);
      tasks[localIdx] = Task.Run(async () => {
        await using var localConn = new NpgsqlConnection(ConnectionString);
        await localConn.OpenAsync(ct);
        await startGate.WaitAsync(ct);
        for (var i = 0; i < perTask; i++) {
          var id = (Guid)TrackedGuid.NewMedo();
          perThreadIds[localIdx].Add(id);
          var version = Interlocked.Increment(ref versionCounter);
          await using var cmd = new NpgsqlCommand(
            "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, created_at, version) " +
            "VALUES (@event_id, @stream_id, @stream_id, 'Test', 'Test', NOW(), @version)", localConn);
          cmd.Parameters.AddWithValue("event_id", id);
          cmd.Parameters.AddWithValue("stream_id", streamId);
          cmd.Parameters.AddWithValue("version", version);
          await cmd.ExecuteNonQueryAsync(ct);
        }
      }, ct);
    }
    startGate.Release(taskCount);
    await Task.WhenAll(tasks);

    // Invariant 1: each thread's local sequence is strictly monotonic.
    for (var t = 0; t < taskCount; t++) {
      for (var i = 1; i < perThreadIds[t].Count; i++) {
        var prev = perThreadIds[t][i - 1].ToString("D");
        var curr = perThreadIds[t][i].ToString("D");
        await Assert.That(string.Compare(curr, prev, StringComparison.Ordinal) > 0).IsTrue()
          .Because($"Thread {t}: id at index {i} ({curr}) must be > id at index {i - 1} ({prev}). The TrackedGuid.NewMedo() lock guarantees this even under cross-thread contention.");
      }
    }

    // Invariant 2: no duplicates across all threads.
    var allIds = perThreadIds.SelectMany(s => s).ToArray();
    await Assert.That(allIds.Distinct().Count()).IsEqualTo(taskCount * perTask)
      .Because("Concurrent NewMedo() must not produce duplicate IDs.");

    // Invariant 3: SQL ORDER BY event_id returns all rows in lex-sorted order matching
    // the unique set of IDs we generated.
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var readCmd = new NpgsqlCommand(
      "SELECT event_id FROM wh_event_store WHERE stream_id = @stream_id ORDER BY event_id ASC", conn);
    readCmd.Parameters.AddWithValue("stream_id", streamId);
    var read = new List<Guid>();
    await using (var reader = await readCmd.ExecuteReaderAsync(ct)) {
      while (await reader.ReadAsync(ct)) {
        read.Add(reader.GetGuid(0));
      }
    }
    var expectedSorted = allIds.OrderBy(g => g.ToString("D"), StringComparer.Ordinal).ToArray();
    await Assert.That(read.SequenceEqual(expectedSorted)).IsTrue()
      .Because("Postgres ORDER BY event_id ASC must yield the same lex-sorted sequence as the union of all thread-generated IDs.");
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
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, created_at, version) " +
        "VALUES (@event_id, @stream_id, @stream_id, 'Test', 'Test', NOW(), @version)", conn);
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
