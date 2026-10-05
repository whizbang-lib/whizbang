// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Concurrent outbox drains over overlapping streams never deadlock (#936, migration 172).
/// </summary>
/// <remarks>
/// <para>
/// With stream runs (171) an instance's claim and its drain's continuation both wrote the same
/// <c>wh_active_streams</c> rows, each as one multi-row UPDATE whose lock order was whatever the join
/// produced: the claim in the order it leased rows, the continuation in the order it was asked. The
/// two orders differ as soon as two streams are involved, and the server log of the reproduction
/// below showed exactly that pair: <c>claim_orphaned_outbox</c> (inside <c>claim_work</c>) updating
/// one ledger row while <c>wh_continue_outbox_streams</c> updated another, each waiting on the other.
/// </para>
/// <para>
/// The rule 172 enforces: tables in the order <c>wh_outbox</c> then <c>wh_active_streams</c>, outbox
/// rows in <c>(stream_id, created_at, message_id)</c> order and ledger rows in <c>stream_id</c> order,
/// and a statement that has no need to wait (the continuation) never waits. The first tests pin each
/// statement's order deterministically: another session holds the FIRST row in that order, the
/// statement is started, and once the server reports it waiting on a lock the test reads which of
/// the other rows it already holds. Taken in order, it holds none of them. The last test runs every
/// statement together, released in lockstep, over the same streams for many rounds.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/172_OutboxLockOrder.sql</code-under-test>
[Category("Shard2")]
public class OutboxStreamRunDeadlockSqlTests : EFCoreTestBase {

  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(30);

  private const string OUTBOX = "wh_outbox";
  private const string LEDGER = "wh_active_streams";

  // --- the continuation never waits ----------------------------------------------------------------

  [Test]
  public async Task ContinueOutboxStreams_WhileAnotherSessionHoldsTheStreamLedger_NeverWaitsAsync() {
    // The continuation's ledger write only moves last_activity_at, which the session holding the row
    // (the claim refreshing it, the lease renewal) is writing anyway. It skips a locked row rather
    // than waiting for it, so while it runs it waits on nothing and cannot be part of a cycle.
    // lock_timeout turns "waited" into an error; the ledger rows stay locked for the whole call, so
    // the outcome does not depend on timing.
    var instance = Guid.CreateVersion7();
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };
    await using var setup = await _openAsync();
    await _registerAsync(setup, instance);
    var cursors = new Dictionary<Guid, Guid>();
    var next = new List<Guid>();
    foreach (var stream in streams) {
      await _ledgerAsync(setup, stream, instance);
      var ids = await _rowsAsync(setup, stream, 3, leasedTo: instance, leasedCount: 1);
      cursors[stream] = ids[0];
      next.AddRange(ids.Skip(1));
    }

    await using var holder = await _openAsync();
    await using var hold = await holder.BeginTransactionAsync();
    await using (var lockLedger = holder.CreateCommand()) {
      lockLedger.Transaction = hold;
      lockLedger.CommandText = "SELECT stream_id FROM wh_active_streams WHERE stream_id = ANY(@streams) FOR UPDATE";
      lockLedger.Parameters.AddWithValue(nameof(streams), streams);
      await lockLedger.ExecuteNonQueryAsync();
    }

    await using var drain = await _openAsync();
    await using (var timeout = drain.CreateCommand()) {
      timeout.CommandText = "SET lock_timeout = '250ms'";
      await timeout.ExecuteNonQueryAsync();
    }
    var continued = await _continueAsync(drain, instance, cursors);

    await Assert.That(continued.Order()).IsEquivalentTo(next.Order(), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("both streams continue from their cursors while their ledger rows are locked elsewhere");
    await hold.RollbackAsync();
  }

  // --- every waiting statement takes its rows in the one order -------------------------------------

  [Test]
  public async Task ClaimOrphanedOutbox_RefreshesTheStreamLedgerInStreamOrderAsync() {
    // Three streams this instance owns, each with a claimable row. Their arrival order is the reverse
    // of their stream order, and so is the ledger's physical order, so a refresh that follows either
    // one reaches the highest stream first. The first stream in stream order is held elsewhere.
    var instance = Guid.CreateVersion7();
    var streams = _ascending(3);
    await using var setup = await _openAsync();
    await _registerAsync(setup, instance);
    foreach (var stream in streams.Reverse()) {
      await _ledgerAsync(setup, stream, instance);
      _ = await _rowsAsync(setup, stream, 1, leasedTo: null, leasedCount: 0);
    }

    var free = await _whileBlockedOnTheFirstAsync(LEDGER, "stream_id", streams, async claim => {
      await using var cmd = claim.CreateCommand();
      cmd.CommandText = @"SELECT count(*) FROM claim_orphaned_outbox(
        @inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '1 minute', 100, 1, 25)";
      cmd.Parameters.AddWithValue("inst", instance);
      await cmd.ExecuteScalarAsync();
    });

    await Assert.That(free).IsEquivalentTo(streams.Skip(1))
      .Because("waiting on the first ledger row in stream order, the claim must hold none of the later ones");
  }

  [Test]
  public async Task CompleteOutboxPublished_DeletesItsRowsInStreamOrderAsync() {
    var instance = Guid.CreateVersion7();
    await using var setup = await _openAsync();
    await _registerAsync(setup, instance);
    var rows = await _rowsInReverseKeyOrderAsync(setup, instance);

    var free = await _whileBlockedOnTheFirstAsync(OUTBOX, "message_id", [.. rows], async flush => {
      await using var cmd = flush.CreateCommand();
      cmd.CommandText = "SELECT complete_outbox_published(@ids)";
      cmd.Parameters.AddWithValue("ids", rows.AsEnumerable().Reverse().ToArray());
      await cmd.ExecuteScalarAsync();
    });

    await Assert.That(free).IsEquivalentTo(rows.Skip(1))
      .Because("waiting on the first row in (stream_id, created_at, message_id) order, it must hold none of the later ones");
    await Assert.That(await _countAsync(setup, rows)).IsEqualTo(0)
      .Because("released, the completion deletes every row it was given");
  }

  [Test]
  public async Task CompleteOutboxPublished_InDebugMode_UpdatesItsRowsInStreamOrderAsync() {
    var instance = Guid.CreateVersion7();
    await using var setup = await _openAsync();
    await _registerAsync(setup, instance);
    var rows = await _rowsInReverseKeyOrderAsync(setup, instance);

    var free = await _whileBlockedOnTheFirstAsync(OUTBOX, "message_id", [.. rows], async flush => {
      await using var cmd = flush.CreateCommand();
      cmd.CommandText = "SELECT complete_outbox_published(@ids, TRUE)";
      cmd.Parameters.AddWithValue("ids", rows.AsEnumerable().Reverse().ToArray());
      await cmd.ExecuteScalarAsync();
    });

    await Assert.That(free).IsEquivalentTo(rows.Skip(1));
    await Assert.That(await _countAsync(setup, rows, "published_at IS NOT NULL")).IsEqualTo(3)
      .Because("debug mode retains every row, stamped published");
  }

  [Test]
  public async Task ProcessOutboxFailures_TakesItsRowsInStreamOrderAsync() {
    // The failures arrive in the reverse of stream order, as a flush that collected them from several
    // drains may deliver them.
    var instance = Guid.CreateVersion7();
    await using var setup = await _openAsync();
    await _registerAsync(setup, instance);
    var rows = await _rowsInReverseKeyOrderAsync(setup, instance);

    var free = await _whileBlockedOnTheFirstAsync(OUTBOX, "message_id", [.. rows], async flush => {
      await using var cmd = flush.CreateCommand();
      cmd.CommandText = "SELECT process_outbox_failures(@f::jsonb, NOW())";
      cmd.Parameters.AddWithValue("f", _failuresJson(rows.AsEnumerable().Reverse()));
      await cmd.ExecuteScalarAsync();
    });

    await Assert.That(free).IsEquivalentTo(rows.Skip(1));
    await Assert.That(await _countAsync(setup, rows, "instance_id IS NULL AND scheduled_for > NOW()")).IsEqualTo(3)
      .Because("released, every failure is recorded and deferred for its retry");
  }

  [Test]
  public async Task RenewLeases_ForTheOutbox_TakesItsRowsInStreamOrderAsync() {
    var instance = Guid.CreateVersion7();
    await using var setup = await _openAsync();
    await _registerAsync(setup, instance);
    var rows = await _rowsInReverseKeyOrderAsync(setup, instance);

    var free = await _whileBlockedOnTheFirstAsync(OUTBOX, "message_id", [.. rows], async renew => {
      await using var cmd = renew.CreateCommand();
      cmd.CommandText = "SELECT renew_leases('outbox', @ids, 3000)";
      cmd.Parameters.AddWithValue("ids", rows.AsEnumerable().Reverse().ToArray());
      await cmd.ExecuteScalarAsync();
    });

    await Assert.That(free).IsEquivalentTo(rows.Skip(1));
    await Assert.That(await _countAsync(setup, rows, "lease_expiry > NOW() + INTERVAL '10 minutes'")).IsEqualTo(3);
  }

  [Test]
  public async Task RenewLeases_ForTheOutbox_RenewsTheStreamLedgerInStreamOrderAsync() {
    // The outbox rows are free; the first ledger row in stream order is held elsewhere.
    var instance = Guid.CreateVersion7();
    var streams = _ascending(3);
    await using var setup = await _openAsync();
    await _registerAsync(setup, instance);
    var ids = new List<Guid>();
    foreach (var stream in streams.Reverse()) {
      await _ledgerAsync(setup, stream, instance);
      ids.AddRange(await _rowsAsync(setup, stream, 1, leasedTo: instance, leasedCount: 1));
    }

    var free = await _whileBlockedOnTheFirstAsync(LEDGER, "stream_id", streams, async renew => {
      await using var cmd = renew.CreateCommand();
      cmd.CommandText = "SELECT renew_leases('outbox', @ids, 3000)";
      cmd.Parameters.AddWithValue("ids", ids.ToArray());
      await cmd.ExecuteScalarAsync();
    });

    await Assert.That(free).IsEquivalentTo(streams.Skip(1));
  }

  // --- all of it at once ---------------------------------------------------------------------------

  [Test]
  public async Task ConcurrentClaimsDrainsContinuationsCompletionsAndFailureReleases_NeverDeadlockAsync() {
    // Four instances drain one outbox. Each round releases every instance's claim, drain fetch,
    // continuation and completion flush (with its failure releases) together on one signal, over
    // streams the instances share, and the round ends when all of them have returned. A deadlock is
    // recorded rather than thrown so the rounds carry on, and the assertion is that there was none.
    const int iterations = 8;
    const int roundCap = 600;
    var actors = Enumerable.Range(0, 4).Select(_ => new Actor(Guid.CreateVersion7())).ToArray();
    var deadlocks = new ConcurrentBag<string>();
    var failOnce = new ConcurrentDictionary<Guid, byte>();
    await using var control = await _openAsync();
    foreach (var actor in actors) {
      await actor.OpenAsync(ConnectionString);
    }
    try {
      var rounds = 0;
      for (var iteration = 0; iteration < iterations; iteration++) {
        foreach (var id in await _seedIterationAsync(control, iteration)) {
          failOnce[id] = 0;
        }
        while (await _pendingAsync(control) > 0 && rounds < roundCap) {
          rounds++;
          var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          var steps = actors.SelectMany(a => a.Round(go.Task, failOnce, deadlocks)).ToList();
          go.SetResult();
          await Task.WhenAll(steps);
          foreach (var actor in actors) {
            actor.EndRound();
          }
        }
      }

      await Assert.That(deadlocks).IsEmpty()
        .Because($"no pair of these statements may deadlock; seen: {string.Join(" || ", deadlocks.Take(3))}");
      await Assert.That(await _pendingAsync(control)).IsEqualTo(0)
        .Because($"every row drains within {roundCap} rounds");
    } finally {
      foreach (var actor in actors) {
        await actor.DisposeAsync();
      }
    }
  }

  /// <summary>
  /// One instance's four sessions, and what passes between its steps from one round to the next: the
  /// streams its claim offered, the cursors its drain published through, and what its flush owes.
  /// </summary>
  private sealed class Actor(Guid instanceId) : IAsyncDisposable {
    private readonly NpgsqlConnection[] _sessions = new NpgsqlConnection[4];
    private readonly HashSet<Guid> _published = [];
    private readonly Lock _lock = new();
    private List<Guid> _offered = [];
    private Dictionary<Guid, Guid> _cursors = [];
    private List<Guid> _completed = [];
    private List<Guid> _failed = [];
    private List<Guid> _nextOffered = [];
    private readonly Dictionary<Guid, Guid> _nextCursors = [];
    private readonly List<Guid> _nextCompleted = [];
    private readonly List<Guid> _nextFailed = [];

    public async Task OpenAsync(string connectionString) {
      for (var i = 0; i < _sessions.Length; i++) {
        _sessions[i] = new NpgsqlConnection(connectionString);
        await _sessions[i].OpenAsync();
      }
    }

    public IEnumerable<Task> Round(Task go, ConcurrentDictionary<Guid, byte> failOnce, ConcurrentBag<string> deadlocks) {
      yield return _stepAsync(go, deadlocks, _claimAsync);
      yield return _stepAsync(go, deadlocks, () => _drainAsync(failOnce));
      yield return _stepAsync(go, deadlocks, () => _continueStreamsAsync(failOnce));
      yield return _stepAsync(go, deadlocks, _flushAsync);
    }

    public void EndRound() {
      _offered = _nextOffered;
      _nextOffered = [];
      _cursors = new Dictionary<Guid, Guid>(_nextCursors);
      _nextCursors.Clear();
      _completed = [.. _nextCompleted];
      _nextCompleted.Clear();
      _failed = [.. _nextFailed];
      _nextFailed.Clear();
    }

    private static async Task _stepAsync(Task go, ConcurrentBag<string> deadlocks, Func<Task> step) {
      await go;
      try {
        await step();
      } catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected) {
        var at = ex.Where?.Split('\n').LastOrDefault(l => l.StartsWith("PL/pgSQL", StringComparison.Ordinal));
        deadlocks.Add($"{at}: {ex.Detail}");
      }
    }

    private async Task _claimAsync() {
      await using var cmd = _sessions[0].CreateCommand();
      cmd.CommandText = @"
        SELECT work_stream_id FROM claim_work(
          p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
          p_max_streams => 25, p_partition_count => 10000, p_lease_seconds => 300,
          p_max_outbox_rows => 60, p_outbox_run_length => 3)
        WHERE source = 'outbox' AND work_stream_id IS NOT NULL";
      cmd.Parameters.AddWithValue("inst", instanceId);
      var offered = new HashSet<Guid>();
      await using (var reader = await cmd.ExecuteReaderAsync()) {
        while (await reader.ReadAsync()) {
          offered.Add(reader.GetGuid(0));
        }
      }
      lock (_lock) {
        _nextOffered = [.. offered];
      }
    }

    private async Task _drainAsync(ConcurrentDictionary<Guid, byte> failOnce) {
      if (_offered.Count == 0) {
        return;
      }
      await using var cmd = _sessions[1].CreateCommand();
      cmd.CommandText = "SELECT message_id, stream_id FROM fetch_outbox_batch(@streams, @inst, 3)";
      cmd.Parameters.AddWithValue("streams", _offered.ToArray());
      cmd.Parameters.AddWithValue("inst", instanceId);
      _publish(await _readRowsAsync(cmd), failOnce);
    }

    private async Task _continueStreamsAsync(ConcurrentDictionary<Guid, byte> failOnce) {
      if (_cursors.Count == 0) {
        return;
      }
      await using var cmd = _sessions[2].CreateCommand();
      cmd.CommandText = "SELECT message_id, stream_id FROM wh_continue_outbox_streams(@inst, @streams, @after, 3)";
      cmd.Parameters.AddWithValue("inst", instanceId);
      cmd.Parameters.AddWithValue("streams", _cursors.Keys.ToArray());
      cmd.Parameters.AddWithValue("after", _cursors.Values.ToArray());
      _publish(await _readRowsAsync(cmd), failOnce);
    }

    private async Task _flushAsync() {
      if (_completed.Count == 0 && _failed.Count == 0) {
        return;
      }
      // One transaction, completions then failures, the order flush_completions runs them in. The
      // retry is due at once (p_now an hour back) so a failed stream keeps moving inside the test.
      await using var tx = await _sessions[3].BeginTransactionAsync();
      await using (var complete = _sessions[3].CreateCommand()) {
        complete.Transaction = tx;
        complete.CommandText = "SELECT complete_outbox_published(@ids)";
        complete.Parameters.AddWithValue("ids", _completed.ToArray());
        await complete.ExecuteNonQueryAsync();
      }
      await using (var fail = _sessions[3].CreateCommand()) {
        fail.Transaction = tx;
        fail.CommandText = "SELECT process_outbox_failures(@f::jsonb, NOW() - INTERVAL '1 hour')";
        fail.Parameters.AddWithValue("f", _failuresJson(_failed));
        await fail.ExecuteNonQueryAsync();
      }
      await tx.CommitAsync();
    }

    /// <summary>Publishes rows in stream order; a row marked to fail stops its stream.</summary>
    private void _publish(List<(Guid Id, Guid Stream)> rows, ConcurrentDictionary<Guid, byte> failOnce) {
      lock (_lock) {
        var stopped = new HashSet<Guid>();
        foreach (var (id, stream) in rows.OrderBy(r => r.Stream).ThenBy(r => r.Id)) {
          if (stopped.Contains(stream) || !_published.Add(id)) {
            continue;
          }
          if (failOnce.TryRemove(id, out _)) {
            _published.Remove(id);
            stopped.Add(stream);
            _nextCursors.Remove(stream);
            _nextFailed.Add(id);
            continue;
          }
          _nextCursors[stream] = id;
          _nextCompleted.Add(id);
        }
      }
    }

    private static async Task<List<(Guid, Guid)>> _readRowsAsync(NpgsqlCommand cmd) {
      var rows = new List<(Guid, Guid)>();
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        rows.Add((reader.GetGuid(0), reader.GetGuid(1)));
      }
      return rows;
    }

    public async ValueTask DisposeAsync() {
      foreach (var session in _sessions.Where(s => s is not null)) {
        await session.DisposeAsync();
      }
    }
  }

  // --- helpers -------------------------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task _registerAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES (@inst, 'test', 'test-host', 1, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>A ledger row: the stream is assigned to the instance under a live stream lease.</summary>
  private static async Task _ledgerAsync(NpgsqlConnection conn, Guid stream, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
      VALUES (@stream, 0, @inst, NOW() - INTERVAL '1 minute', NOW() + INTERVAL '5 minutes')";
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Appends rows to a stream in arrival order, the first <paramref name="leasedCount"/> leased to
  /// <paramref name="leasedTo"/> under a live lease and the rest unowned.
  /// </summary>
  private static async Task<List<Guid>> _rowsAsync(NpgsqlConnection conn, Guid stream, int count, Guid? leasedTo, int leasedCount) {
    var ids = Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).Order().ToList();
    for (var i = 0; i < ids.Count; i++) {
      await _insertRowAsync(conn, ids[i], stream, i < leasedCount ? leasedTo : null);
    }
    return ids;
  }

  private static async Task _insertRowAsync(NpgsqlConnection conn, Guid messageId, Guid stream, Guid? leasedTo) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id,
         partition_number, instance_id, lease_expiry)
      VALUES (@id, 'test-topic', 'TestEvent', '{}', '{}', 0, 1, clock_timestamp(), @stream, 0,
              @inst, CASE WHEN @inst IS NULL THEN NULL ELSE NOW() + INTERVAL '5 minutes' END)";
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    cmd.Parameters.Add(new NpgsqlParameter("inst", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)leasedTo ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Three leased rows on three streams, returned in (stream_id, created_at, message_id) order. Their
  /// message ids and their physical order both run the other way, so a statement that follows the
  /// primary key or the heap reaches the last row first.
  /// </summary>
  private static async Task<List<Guid>> _rowsInReverseKeyOrderAsync(NpgsqlConnection conn, Guid instanceId) {
    var streams = _ascending(3);
    var ids = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).Order().Reverse().ToList();
    for (var i = ids.Count - 1; i >= 0; i--) {
      await _insertRowAsync(conn, ids[i], streams[i], instanceId);
    }
    return ids;
  }

  /// <summary>Stream ids in the order Postgres sorts uuids: bytewise, which is their text order.</summary>
  private static Guid[] _ascending(int count) =>
    [.. Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).OrderBy(g => g.ToString(), StringComparer.Ordinal)];

  private static async Task<long> _countAsync(NpgsqlConnection conn, List<Guid> ids, string predicate = "TRUE") {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT count(*) FROM wh_outbox WHERE message_id = ANY(@ids) AND {predicate}";
    cmd.Parameters.AddWithValue(nameof(ids), ids.ToArray());
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<List<Guid>> _continueAsync(NpgsqlConnection conn, Guid instanceId, Dictionary<Guid, Guid> cursors) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT message_id FROM wh_continue_outbox_streams(@inst, @streams, @after, 10)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.AddWithValue("streams", cursors.Keys.ToArray());
    cmd.Parameters.AddWithValue("after", cursors.Values.ToArray());
    var rows = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetGuid(0));
    }
    return rows;
  }

  private static string _failuresJson(IEnumerable<Guid> ids) =>
    JsonSerializer.Serialize(ids.Select(id => new Dictionary<string, object> {
      ["MessageId"] = id,
      ["CompletedStatus"] = 0,
      ["Error"] = "transport refused",
      ["Reason"] = 0,
    }).ToList());

  private static async Task<long> _pendingAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_outbox WHERE processed_at IS NULL";
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// One iteration's backlog: a hundred streams of one row and a third as many of eight, interleaved,
  /// with stream ids unrelated to arrival order. Every ninth row fails its first publish.
  /// </summary>
  /// <returns>The rows that fail once.</returns>
  private static async Task<List<Guid>> _seedIterationAsync(NpgsqlConnection conn, int iteration) {
    var random = new Random(936 + iteration);
    var streams = Enumerable.Range(0, 100).Select(_ => {
      var bytes = new byte[16];
      random.NextBytes(bytes);
      return new Guid(bytes);
    }).ToArray();
    var messageIds = new List<Guid>();
    var streamIds = new List<Guid>();
    for (var r = 0; r < 8; r++) {
      for (var s = 0; s < streams.Length; s++) {
        if (r == 0 || s % 3 == 0) {
          messageIds.Add(Guid.CreateVersion7());
          streamIds.Add(streams[s]);
        }
      }
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
      SELECT m.id, 'test-topic', 'TestEvent', '{}', '{}', 0, 0,
             clock_timestamp() + (m.ord * INTERVAL '1 microsecond'), m.stream, abs(hashtext(m.stream::text)) % 10000
      FROM unnest(@ids, @streams) WITH ORDINALITY AS m(id, stream, ord)";
    cmd.Parameters.AddWithValue("ids", messageIds.ToArray());
    cmd.Parameters.AddWithValue("streams", streamIds.ToArray());
    await cmd.ExecuteNonQueryAsync();
    return [.. messageIds.Where((_, i) => i % 9 == 4)];
  }

  /// <summary>
  /// Holds the first of <paramref name="keys"/> from another session, starts the statement, waits for
  /// the server to report the statement's session waiting on a lock, and reads which of the other keys
  /// are still free. Then releases the hold and lets the statement finish.
  /// </summary>
  private async Task<List<Guid>> _whileBlockedOnTheFirstAsync(
      string table, string keyColumn, Guid[] keys, Func<NpgsqlConnection, Task> statement) {
    await using var holder = await _openAsync();
    await using var hold = await holder.BeginTransactionAsync();
    await using (var first = holder.CreateCommand()) {
      first.Transaction = hold;
      first.CommandText = $"SELECT {keyColumn} FROM {table} WHERE {keyColumn} = @key FOR UPDATE";
      first.Parameters.AddWithValue("key", keys[0]);
      await first.ExecuteNonQueryAsync();
    }

    await using var subject = await _openAsync();
    var running = statement(subject);
    try {
      await _waitUntilWaitingOnALockAsync(subject.ProcessID, running);

      await using var probe = await _openAsync();
      await using var probing = await probe.BeginTransactionAsync();
      await using var cmd = probe.CreateCommand();
      cmd.Transaction = probing;
      cmd.CommandText = $"SELECT {keyColumn} FROM {table} WHERE {keyColumn} = ANY(@keys) FOR UPDATE SKIP LOCKED";
      cmd.Parameters.AddWithValue(nameof(keys), keys);
      var free = new List<Guid>();
      await using (var reader = await cmd.ExecuteReaderAsync()) {
        while (await reader.ReadAsync()) {
          free.Add(reader.GetGuid(0));
        }
      }
      await probing.RollbackAsync();
      return [.. keys.Where(free.Contains)];
    } finally {
      await hold.RollbackAsync();
      await running;
    }
  }

  /// <summary>
  /// The signal the statement cannot give itself: the server's own report that its session is waiting
  /// on a lock. Bounded; a statement that finishes without ever waiting breaks the test's premise.
  /// </summary>
  private async Task _waitUntilWaitingOnALockAsync(int pid, Task running) {
    await using var watch = await _openAsync();
    await using var cmd = watch.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE pid = @pid AND wait_event_type = 'Lock'";
    cmd.Parameters.AddWithValue(nameof(pid), pid);
    var deadline = DateTimeOffset.UtcNow + _signalDeadline;
    while (DateTimeOffset.UtcNow < deadline) {
      if (running.IsCompleted) {
        await running;
        throw new InvalidOperationException("The statement finished without waiting on the held row; the test's premise is broken.");
      }
      if (Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0) {
        return;
      }
    }
    throw new TimeoutException($"The statement's session never waited on a lock within {_signalDeadline}.");
  }
}
