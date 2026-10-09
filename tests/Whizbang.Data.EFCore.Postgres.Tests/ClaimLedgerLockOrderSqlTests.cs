// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// One claim takes every stream-ledger row it writes in one <c>stream_id</c> order, whichever acquisition leased
/// the stream (#1256).
/// </summary>
/// <remarks>
/// <para>
/// Migration 200 (#1238) put the outbox acquisition's ledger writes into one ordered pass. Two other writers were
/// left with the shape it removed: the inbox and perspective acquisitions refreshed the streams they own with a bare
/// <c>UPDATE ... FROM</c>, in whatever order the join produced, and pinned the rest after it. And one
/// <c>claim_work</c> ran three acquisitions, each writing ledger rows for its own streams, so even three sorted
/// passes would be three ascending runs, not one order.
/// </para>
/// <para>
/// The deterministic tests pin each statement's order the way <see cref="OutboxStreamRunDeadlockSqlTests"/> does:
/// another session holds the first ledger row in order, the statement is started, and once the server reports it
/// waiting on a lock the test reads which of the later rows it already holds. Taken in order, it holds none. The
/// concurrent tests reproduce the deadlock itself with instances that are never registered, so every claim ranks
/// alone, sees no live peer, and takes rows on streams other claimers own.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/202_ClaimTakesItsLedgerInOnePass.sql</code-under-test>
[Category("Shard2")]
public class ClaimLedgerLockOrderSqlTests : EFCoreTestBase {

  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(30);

  private const string CLAIM_INBOX = @"SELECT count(*) FROM claim_orphaned_inbox(
    @inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '1 minute', 100)";

  private const string CLAIM_PERSPECTIVE = @"SELECT count(*) FROM claim_orphaned_perspective_events(
    @inst, NOW() + INTERVAL '5 minutes', NOW(), 100, 0, 1, 100)";

  // --- each acquisition takes its ledger rows in one order -----------------------------------------

  [Test]
  public async Task ClaimOrphanedInbox_RefreshesTheStreamLedgerInStreamOrderAsync() {
    // Three streams this instance owns, each with a claimable inbox row. Their arrival order is the reverse of
    // their stream order, and so is the ledger's physical order, so a refresh that follows either one reaches the
    // highest stream first. The first stream in stream order is held elsewhere.
    var instance = Guid.CreateVersion7();
    var streams = _ascending(3);
    await using var setup = await _openAsync();
    await _registerAsync(setup, instance);
    foreach (var stream in streams.Reverse()) {
      await _ledgerAsync(setup, stream, instance);
      await _inboxRowAsync(setup, stream);
    }

    var free = await _whileBlockedOnTheFirstAsync(streams, claim => _runAsync(claim, CLAIM_INBOX, instance));

    await Assert.That(free).IsEquivalentTo(streams.Skip(1))
      .Because("waiting on the first ledger row in stream order, the inbox claim must hold none of the later ones");
  }

  [Test]
  public async Task ClaimOrphanedInbox_RefreshedAndPinnedStreams_TakesTheirLedgerRowsInOneStreamOrderAsync() {
    // #1256, the inbox counterpart of #1238. The middle stream is this instance's own (refreshed); the first and the
    // last are an unregistered peer's (taken over). Each kind in order is not one order: the refresh ran first.
    var instance = Guid.CreateVersion7();
    var peer = Guid.CreateVersion7();
    var streams = _ascending(3);
    await using var setup = await _openAsync();
    Guid[] owners = [peer, instance, peer];
    foreach (var (stream, owner) in streams.Zip(owners)) {
      await _ledgerAsync(setup, stream, owner);
      await _inboxRowAsync(setup, stream);
    }

    var free = await _whileBlockedOnTheFirstAsync(streams, claim => _runAsync(claim, CLAIM_INBOX, instance));

    await Assert.That(free).IsEquivalentTo(streams.Skip(1))
      .Because("waiting on the first ledger row in stream order, the inbox claim must hold none of the later ones, refreshed or taken over");
    await Assert.That(await _ownedByAsync(setup, streams, instance)).IsEqualTo(3)
      .Because("released, the claim refreshes its own stream and takes over the unregistered peer's two, as before");
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_RefreshedAndPinnedStreams_TakesTheirLedgerRowsInOneStreamOrderAsync() {
    // The perspective acquisition writes the ledger along the same two paths, refresh first.
    var instance = Guid.CreateVersion7();
    var peer = Guid.CreateVersion7();
    var streams = _ascending(3);
    await using var setup = await _openAsync();
    Guid[] owners = [peer, instance, peer];
    foreach (var (stream, owner) in streams.Zip(owners)) {
      await _ledgerAsync(setup, stream, owner);
      await _perspectiveEventAsync(setup, stream);
    }

    var free = await _whileBlockedOnTheFirstAsync(streams, claim => _runAsync(claim, CLAIM_PERSPECTIVE, instance));

    await Assert.That(free).IsEquivalentTo(streams.Skip(1))
      .Because("waiting on the first ledger row in stream order, the perspective claim must hold none of the later ones");
    await Assert.That(await _ownedByAsync(setup, streams, instance)).IsEqualTo(3)
      .Because("released, the claim refreshes its own stream and takes over the unregistered peer's two, as before");
  }

  [Test]
  public async Task ClaimOrphanedInbox_AStreamWithNoLedgerRow_IsPinnedToTheClaimerAsync() {
    // A stream nobody has recorded is inserted after the lock pass, leased to the claimer.
    var instance = Guid.CreateVersion7();
    var stream = Guid.NewGuid();
    await using var setup = await _openAsync();
    await _inboxRowAsync(setup, stream);

    await using var claim = await _openAsync();
    await _runAsync(claim, CLAIM_INBOX, instance);

    await Assert.That(await _ownedByAsync(setup, [stream], instance)).IsEqualTo(1)
      .Because("an unrecorded stream is pinned to the instance that leased its row");
  }

  // --- one claim takes the ledger in one pass across its acquisitions ------------------------------

  [Test]
  [Arguments("inbox")]
  [Arguments("perspective")]
  public async Task ClaimWork_LedgerRowsOfEveryAcquisition_AreTakenInOneStreamOrderAsync(string firstStreamWork) {
    // One claim_work leases outbox rows on the second and third streams and inbox (or perspective) work on the
    // first. The outbox acquisition runs first, so a claim whose acquisitions each write their own ledger rows holds
    // the second and third streams' rows when the later acquisition reaches the first: two ascending runs. In one
    // pass, waiting on the first row it holds none of the later ones.
    var instance = Guid.CreateVersion7();
    var streams = _ascending(3);
    await using var setup = await _openAsync();
    await _registerAsync(setup, instance);
    foreach (var stream in streams) {
      await _ledgerAsync(setup, stream, instance);
    }
    if (firstStreamWork == "inbox") {
      await _inboxRowAsync(setup, streams[0]);
    } else {
      await _perspectiveEventAsync(setup, streams[0]);
    }
    await _outboxRowAsync(setup, streams[1]);
    await _outboxRowAsync(setup, streams[2]);

    var free = await _whileBlockedOnTheFirstAsync(streams, claim => _claimWorkAsync(claim, instance));

    await Assert.That(free).IsEquivalentTo(streams.Skip(1))
      .Because($"waiting on the first ledger row ({firstStreamWork} work), the claim must not already hold the outbox streams' rows");
    await Assert.That(await _leaseRenewedAsync(setup, streams, instance)).IsEqualTo(3)
      .Because("released, the claim refreshes every stream it leased work on, whichever acquisition leased it");
  }

  [Test]
  public async Task ClaimWork_AStreamLeasedByTwoAcquisitions_IsWrittenOnceAsync() {
    // The same stream has outbox and inbox work. The one pass sees it twice in its input and writes it once.
    var instance = Guid.CreateVersion7();
    var stream = Guid.NewGuid();
    await using var setup = await _openAsync();
    await _registerAsync(setup, instance);
    await _inboxRowAsync(setup, stream);
    await _outboxRowAsync(setup, stream);

    await using var claim = await _openAsync();
    await _claimWorkAsync(claim, instance);

    await Assert.That(await _ownedByAsync(setup, [stream], instance)).IsEqualTo(1)
      .Because("a stream leased by both acquisitions is pinned to the claimer once");
  }

  // --- the deadlock itself ---------------------------------------------------------------------------

  [Test]
  public Task ConcurrentInboxClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync() =>
    // #1256. Four instances drain one inbox and never register: no row in wh_service_instances and no
    // whizbang-<id> application name for the whole run, so every claim ranks alone, sees no live peer, and leases
    // rows on streams another claimer owns. Its ledger writes then refresh some streams and take over others.
    _concurrentRoundsAsync(withOutbox: false, withInbox: true);

  [Test]
  public Task ConcurrentOutboxAndInboxClaims_WithNoInstanceEverRegistered_NeverDeadlockAsync() =>
    // #1256. The same rounds over streams that carry outbox AND inbox work, so one claim_work leases outbox rows on
    // some streams and inbox rows on others, and its two acquisitions both write the ledger.
    _concurrentRoundsAsync(withOutbox: true, withInbox: true);

  private async Task _concurrentRoundsAsync(bool withOutbox, bool withInbox) {
    // Each round releases every instance's claim and drains together on one signal, and the round ends when all of
    // them have returned. A deadlock is recorded rather than thrown so the rounds carry on, and the assertion is
    // that there was none.
    const int iterations = 8;
    const int roundCap = 600;
    var actors = Enumerable.Range(0, 4).Select(_ => new Actor(Guid.CreateVersion7())).ToArray();
    var deadlocks = new ConcurrentBag<string>();
    await using var control = await _openAsync();
    foreach (var actor in actors) {
      await actor.OpenAsync(ConnectionString);
    }
    try {
      var rounds = 0;
      for (var iteration = 0; iteration < iterations; iteration++) {
        await _seedIterationAsync(control, iteration, withOutbox, withInbox);
        while (await _pendingAsync(control) > 0 && rounds < roundCap) {
          rounds++;
          var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          var steps = actors.SelectMany(a => a.Round(go.Task, deadlocks)).ToList();
          go.SetResult();
          await Task.WhenAll(steps);
          foreach (var actor in actors) {
            actor.EndRound();
          }
        }
      }

      await Assert.That(deadlocks).IsEmpty()
        .Because($"no two claims may deadlock; seen {deadlocks.Count} in {rounds} rounds, first: {string.Join(" || ", deadlocks.Take(3))}");
      await Assert.That(await _pendingAsync(control)).IsEqualTo(0)
        .Because($"every row drains within {roundCap} rounds");

      // The premise, read while the actors' sessions are still open so the application-name check sees them.
      await using var cmd = control.CreateCommand();
      cmd.CommandText = "SELECT count(*) FROM wh_service_instances";
      await Assert.That(Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture)).IsEqualTo(0)
        .Because("the premise: no instance was registered at any point in the run");
      cmd.CommandText = @"SELECT count(*) FILTER (WHERE application_name LIKE 'whizbang-%'), count(*)
        FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()";
      await using var reader = await cmd.ExecuteReaderAsync();
      await reader.ReadAsync();
      await Assert.That(reader.GetInt64(0)).IsEqualTo(0)
        .Because("the premise: no connection advertises an instance as live");
      await Assert.That(reader.GetInt64(1)).IsGreaterThanOrEqualTo(12)
        .Because("the four actors' twelve sessions are open while this is read");
    } finally {
      foreach (var actor in actors) {
        await actor.DisposeAsync();
      }
    }
  }

  /// <summary>
  /// One instance's three sessions (claim, inbox drain, outbox drain), and the streams its claim offered, which its
  /// drains take and complete in the next round.
  /// </summary>
  private sealed class Actor(Guid instanceId) : IAsyncDisposable {
    private readonly NpgsqlConnection[] _sessions = new NpgsqlConnection[3];
    private List<Guid> _inboxOffered = [];
    private List<Guid> _outboxOffered = [];
    private List<Guid> _nextInboxOffered = [];
    private List<Guid> _nextOutboxOffered = [];

    public async Task OpenAsync(string connectionString) {
      for (var i = 0; i < _sessions.Length; i++) {
        _sessions[i] = new NpgsqlConnection(connectionString);
        await _sessions[i].OpenAsync();
      }
    }

    public IEnumerable<Task> Round(Task go, ConcurrentBag<string> deadlocks) {
      yield return _stepAsync(go, deadlocks, _claimAsync);
      yield return _stepAsync(go, deadlocks, _drainInboxAsync);
      yield return _stepAsync(go, deadlocks, _drainOutboxAsync);
    }

    public void EndRound() {
      _inboxOffered = _nextInboxOffered;
      _outboxOffered = _nextOutboxOffered;
      _nextInboxOffered = [];
      _nextOutboxOffered = [];
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
      // No registration after a stale claim: the instance stays unregistered for the whole run.
      await using var cmd = _sessions[0].CreateCommand();
      cmd.CommandText = @"
        SELECT source, work_stream_id FROM claim_work(
          p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
          p_max_streams => 25, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => 60,
          p_max_outbox_rows => 60, p_outbox_run_length => 3)
        WHERE source IN ('inbox', 'outbox') AND work_stream_id IS NOT NULL";
      cmd.Parameters.AddWithValue("inst", instanceId);
      var inbox = new HashSet<Guid>();
      var outbox = new HashSet<Guid>();
      await using (var reader = await cmd.ExecuteReaderAsync()) {
        while (await reader.ReadAsync()) {
          (reader.GetString(0) == "inbox" ? inbox : outbox).Add(reader.GetGuid(1));
        }
      }
      _nextInboxOffered = [.. inbox];
      _nextOutboxOffered = [.. outbox];
    }

    private async Task _drainInboxAsync() {
      if (_inboxOffered.Count == 0) {
        return;
      }
      var ids = new List<Guid>();
      await using (var fetch = _sessions[1].CreateCommand()) {
        fetch.CommandText = "SELECT message_id FROM fetch_inbox_batch(@streams, @inst, 100)";
        fetch.Parameters.AddWithValue("streams", _inboxOffered.ToArray());
        fetch.Parameters.AddWithValue("inst", instanceId);
        await using var reader = await fetch.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
          ids.Add(reader.GetGuid(0));
        }
      }
      if (ids.Count == 0) {
        return;
      }
      await using var complete = _sessions[1].CreateCommand();
      complete.CommandText = "SELECT count(*) FROM process_inbox_completions(@c::jsonb, NOW())";
      complete.Parameters.AddWithValue("c", JsonSerializer.Serialize(
        ids.ConvertAll(id => new Dictionary<string, object> { ["MessageId"] = id, ["Status"] = 2 })));
      await complete.ExecuteScalarAsync();
    }

    private async Task _drainOutboxAsync() {
      if (_outboxOffered.Count == 0) {
        return;
      }
      var ids = new List<Guid>();
      await using (var fetch = _sessions[2].CreateCommand()) {
        fetch.CommandText = "SELECT message_id FROM fetch_outbox_batch(@streams, @inst, 100)";
        fetch.Parameters.AddWithValue("streams", _outboxOffered.ToArray());
        fetch.Parameters.AddWithValue("inst", instanceId);
        await using var reader = await fetch.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
          ids.Add(reader.GetGuid(0));
        }
      }
      if (ids.Count == 0) {
        return;
      }
      await using var complete = _sessions[2].CreateCommand();
      complete.CommandText = "SELECT complete_outbox_published(@ids)";
      complete.Parameters.AddWithValue(nameof(ids), ids.ToArray());
      await complete.ExecuteNonQueryAsync();
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

  private static async Task _runAsync(NpgsqlConnection conn, string sql, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteScalarAsync();
  }

  private static async Task _claimWorkAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT count(*) FROM claim_work(
      p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
      p_max_streams => 25, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => 60)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteScalarAsync();
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

  /// <summary>One unowned pending inbox command on the stream, partition 0, arriving now.</summary>
  private static async Task _inboxRowAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id)
        VALUES (@id, 'TestHandler', 'TestCommand', '{}', '{}', clock_timestamp(), @stream)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, 0 FROM m";
    cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>One unowned pending outbox row on the stream, partition 0, arriving now.</summary>
  private static async Task _outboxRowAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
      VALUES (@id, 'test-topic', 'TestEvent', '{}', '{}', 0, 0, clock_timestamp(), @stream, 0)";
    cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>One unowned pending perspective event on the stream, partition 0.</summary>
  private static async Task _perspectiveEventAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry, partition_number, status, attempts, created_at)
      VALUES (gen_random_uuid(), @stream, 'TestPerspective', @event, NULL, NULL, 0, 0, 0, NOW() - INTERVAL '10 minutes')";
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    cmd.Parameters.AddWithValue("event", Guid.CreateVersion7());
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _ownedByAsync(NpgsqlConnection conn, Guid[] streams, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_active_streams WHERE stream_id = ANY(@streams) AND assigned_instance_id = @inst";
    cmd.Parameters.AddWithValue(nameof(streams), streams);
    cmd.Parameters.AddWithValue("inst", instanceId);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  /// <summary>Streams owned by the instance whose activity was written by a claim (the seed wrote it a minute back).</summary>
  private static async Task<long> _leaseRenewedAsync(NpgsqlConnection conn, Guid[] streams, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT count(*) FROM wh_active_streams
      WHERE stream_id = ANY(@streams) AND assigned_instance_id = @inst AND last_activity_at > NOW() - INTERVAL '30 seconds'";
    cmd.Parameters.AddWithValue(nameof(streams), streams);
    cmd.Parameters.AddWithValue("inst", instanceId);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  /// <summary>Stream ids in the order Postgres sorts uuids: bytewise, which is their text order.</summary>
  private static Guid[] _ascending(int count) =>
    [.. Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).OrderBy(g => g.ToString(), StringComparer.Ordinal)];

  private static async Task<long> _pendingAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT (SELECT count(*) FROM wh_outbox WHERE processed_at IS NULL)
                             + (SELECT count(*) FROM wh_inbox_state WHERE processed_at IS NULL)";
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// One iteration's backlog on a hundred streams: one row each, and eight on every third, interleaved, with stream
  /// ids unrelated to arrival order. Outbox and inbox rows, when both are asked for, sit on the same streams.
  /// </summary>
  private static async Task _seedIterationAsync(NpgsqlConnection conn, int iteration, bool withOutbox, bool withInbox) {
    // Derived, not drawn, so a failing iteration can be replayed: the same iteration has the same streams.
    var streams = Enumerable.Range(0, 100)
      .Select(s => new Guid(SHA256.HashData(BitConverter.GetBytes((iteration * 1000) + s))[..16]))
      .ToArray();
    var streamIds = new List<Guid>();
    for (var r = 0; r < 8; r++) {
      for (var s = 0; s < streams.Length; s++) {
        if (r == 0 || s % 3 == 0) {
          streamIds.Add(streams[s]);
        }
      }
    }
    await using var cmd = conn.CreateCommand();
    cmd.Parameters.AddWithValue("streams", streamIds.ToArray());
    cmd.Parameters.AddWithValue("outboxIds", streamIds.Select(_ => Guid.CreateVersion7()).ToArray());
    cmd.Parameters.AddWithValue("inboxIds", streamIds.Select(_ => Guid.CreateVersion7()).ToArray());
    var sql = new List<string>();
    if (withOutbox) {
      sql.Add(@"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        SELECT m.id, 'test-topic', 'TestEvent', '{}', '{}', 0, 0,
               clock_timestamp() + (m.ord * INTERVAL '1 microsecond'), m.stream, abs(hashtext(m.stream::text)) % 10000
        FROM unnest(@outboxIds, @streams) WITH ORDINALITY AS m(id, stream, ord)");
    }
    if (withInbox) {
      sql.Add(@"
        WITH m AS (
          INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id)
          SELECT u.id, 'TestHandler', 'TestCommand', '{}', '{}', clock_timestamp() + (u.ord * INTERVAL '1 microsecond'), u.stream
          FROM unnest(@inboxIds, @streams) WITH ORDINALITY AS u(id, stream, ord)
          RETURNING message_id, stream_id, received_at, priority, is_event
        )
        INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number)
        SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, abs(hashtext(stream_id::text)) % 10000 FROM m");
    }
    cmd.CommandText = string.Join(";\n", sql);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Holds the ledger row of the first of <paramref name="streams"/> from another session, starts the statement,
  /// waits for the server to report the statement's session waiting on a lock, and reads which of the other streams'
  /// ledger rows are still free. Then releases the hold and lets the statement finish.
  /// </summary>
  private async Task<List<Guid>> _whileBlockedOnTheFirstAsync(Guid[] streams, Func<NpgsqlConnection, Task> statement) {
    await using var holder = await _openAsync();
    await using var hold = await holder.BeginTransactionAsync();
    await using (var first = holder.CreateCommand()) {
      first.Transaction = hold;
      first.CommandText = "SELECT stream_id FROM wh_active_streams WHERE stream_id = @key FOR UPDATE";
      first.Parameters.AddWithValue("key", streams[0]);
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
      cmd.CommandText = "SELECT stream_id FROM wh_active_streams WHERE stream_id = ANY(@keys) FOR UPDATE SKIP LOCKED";
      cmd.Parameters.AddWithValue("keys", streams);
      var free = new List<Guid>();
      await using (var reader = await cmd.ExecuteReaderAsync()) {
        while (await reader.ReadAsync()) {
          free.Add(reader.GetGuid(0));
        }
      }
      await probing.RollbackAsync();
      return [.. streams.Where(free.Contains)];
    } finally {
      await hold.RollbackAsync();
      await running;
    }
  }

  /// <summary>
  /// The signal the statement cannot give itself: the server's own report that its session is waiting on a lock.
  /// Bounded; a statement that finishes without ever waiting breaks the test's premise.
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
