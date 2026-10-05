// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// An outbox claim leases a RUN of a stream's consecutive rows, so a backlog on a few long streams
/// drains in runs per claim cycle rather than one row per stream per cycle (migration 171).
/// </summary>
/// <remarks>
/// <para>
/// Measured on a deployed service, a backlog of a few dozen streams of several hundred rows each drained at about
/// one row per stream per two-second claim cycle while the database and the broker were idle: the
/// claim handed the outbox acquisition its STREAM window (floor 25) as a ROW cap, so the oldest rows
/// it leased were one row on each of the oldest streams. The drain time of such a backlog is its
/// longest stream's row count times the cycle.
/// </para>
/// <para>
/// Each test drives the SQL the claim loop and the drain call, in the order they call it, and counts
/// claim cycles rather than time: a cycle is the unit the fix changes, and counting it keeps the
/// tests deterministic.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/171_OutboxStreamRuns.sql</code-under-test>
[Category("Shard2")]
public class OutboxStreamRunSqlTests : EFCoreTestBase {

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES (@inst, 'test', 'test-host', 1, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Seeds rows on the given streams, interleaved round-robin in arrival order (row 1 of every stream,
  /// then row 2 of every stream, ...), which is the shape a producer fanning work onto a few streams
  /// writes. Message ids ascend with arrival, as UUIDv7 ids minted in order do, so the stream order
  /// the claim walks (arrival) and the order the drain publishes (message id) agree.
  /// </summary>
  /// <returns>Each stream's message ids in stream order.</returns>
  private static async Task<Dictionary<Guid, List<Guid>>> _seedInterleavedAsync(
      NpgsqlConnection conn, IReadOnlyList<Guid> streams, int rowsPerStream) {
    var total = streams.Count * rowsPerStream;
    var ids = Enumerable.Range(0, total).Select(_ => Guid.CreateVersion7()).Order().ToList();
    var byStream = streams.ToDictionary(s => s, _ => new List<Guid>());
    var messageIds = new Guid[total];
    var streamIds = new Guid[total];
    for (var i = 0; i < total; i++) {
      var stream = streams[i % streams.Count];
      messageIds[i] = ids[i];
      streamIds[i] = stream;
      byStream[stream].Add(ids[i]);
    }
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
      SELECT m.id, 'test-topic', 'TestEvent', '{}', '{}', 0, 0,
             TIMESTAMPTZ '2026-01-01 00:00:00+00' + (m.ord * INTERVAL '1 millisecond'), m.stream, 0
      FROM unnest(@ids, @streams) WITH ORDINALITY AS m(id, stream, ord)";
    ins.Parameters.AddWithValue("ids", messageIds);
    ins.Parameters.AddWithValue(nameof(streams), streamIds);
    await ins.ExecuteNonQueryAsync();
    return byStream;
  }

  /// <summary>One claim_work call. NULL outbox bound and run reproduce the previous claim exactly.</summary>
  private static async Task<(List<(Guid WorkId, Guid StreamId)> Outbox, List<string> Notices)> _claimAsync(
      NpgsqlConnection conn, Guid instanceId, int maxStreams, int? maxOutboxRows, int? runLength) {
    var notices = new List<string>();
    void OnNotice(object? sender, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
    conn.Notice += OnNotice;
    var outbox = new List<(Guid, Guid)>();
    try {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = @"
        SELECT source, work_id, work_stream_id FROM claim_work(
          p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
          p_max_streams => @max, p_partition_count => 10000, p_lease_seconds => 300,
          p_max_outbox_rows => @rows, p_outbox_run_length => @run)";
      cmd.Parameters.AddWithValue("inst", instanceId);
      cmd.Parameters.AddWithValue("max", maxStreams);
      cmd.Parameters.Add(new NpgsqlParameter("rows", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)maxOutboxRows ?? DBNull.Value });
      cmd.Parameters.Add(new NpgsqlParameter("run", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)runLength ?? DBNull.Value });
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        if (reader.GetString(0) == "outbox") {
          outbox.Add((reader.GetGuid(1), reader.GetGuid(2)));
        }
      }
    } finally {
      conn.Notice -= OnNotice;
    }
    return (outbox, notices);
  }

  /// <summary>The rows this instance holds under a live lease, in the order the drain publishes them.</summary>
  private static async Task<List<(Guid MessageId, Guid StreamId)>> _heldAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT message_id, stream_id FROM wh_outbox
      WHERE instance_id = @inst AND processed_at IS NULL AND lease_expiry > NOW()
      ORDER BY stream_id, message_id";
    cmd.Parameters.AddWithValue("inst", instanceId);
    var held = new List<(Guid, Guid)>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      held.Add((reader.GetGuid(0), reader.GetGuid(1)));
    }
    return held;
  }

  private static async Task _completeAsync(NpgsqlConnection conn, IEnumerable<Guid> messageIds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT complete_outbox_published(@ids)";
    cmd.Parameters.AddWithValue("ids", messageIds.ToArray());
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int> _pendingAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_outbox WHERE processed_at IS NULL";
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Drives claim cycles the way the claim loop and the drain do: claim, publish every row held in
  /// stream order, complete them. Returns the number of cycles and, per stream, the order its rows
  /// were published in.
  /// </summary>
  private static async Task<(int Cycles, Dictionary<Guid, List<Guid>> Published)> _drainByClaimCyclesAsync(
      NpgsqlConnection conn, Guid instanceId, int maxStreams, int? maxOutboxRows, int? runLength, int cycleCap) {
    var published = new Dictionary<Guid, List<Guid>>();
    var cycles = 0;
    while (await _pendingAsync(conn) > 0 && cycles < cycleCap) {
      cycles++;
      _ = await _claimAsync(conn, instanceId, maxStreams, maxOutboxRows, runLength);
      var held = await _heldAsync(conn, instanceId);
      foreach (var (messageId, streamId) in held) {
        if (!published.TryGetValue(streamId, out var list)) {
          published[streamId] = list = [];
        }
        list.Add(messageId);
      }
      await _completeAsync(conn, held.Select(h => h.MessageId));
    }
    return (cycles, published);
  }

  [Test]
  public async Task ClaimWork_OneLongStream_DrainsInRunsNotRowsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var stream = Guid.CreateVersion7();
    const int rows = 60;
    const int run = 20;
    var seeded = await _seedInterleavedAsync(conn, [stream], rows);

    // The stream window at one stream is the shape the adaptive window gives a single long stream.
    var (cycles, published) = await _drainByClaimCyclesAsync(
      conn, instance, maxStreams: 1, maxOutboxRows: 1000, runLength: run, cycleCap: rows + 5);

    await Assert.That(cycles).IsEqualTo(rows / run)
      .Because($"{rows} rows on one stream with a run of {run} must drain in {rows / run} claim cycles; "
             + $"one row per stream per cycle would take {rows} cycles, which is the measured defect");
    await Assert.That(published[stream]).IsEquivalentTo(seeded[stream], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a run is published in the stream's own order, across every run");
  }

  [Test]
  public async Task ClaimWork_OneLongStream_WithoutARun_TakesOneRowPerCycleAsync() {
    // The before, pinned: the previous claim (no outbox bound, no run) hands the stream window to the
    // acquisition as a row cap, so a one-stream window leases one row per claim cycle.
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var stream = Guid.CreateVersion7();
    const int rows = 12;
    _ = await _seedInterleavedAsync(conn, [stream], rows);

    var (cycles, _) = await _drainByClaimCyclesAsync(
      conn, instance, maxStreams: 1, maxOutboxRows: null, runLength: null, cycleCap: rows + 5);

    await Assert.That(cycles).IsEqualTo(rows)
      .Because("with no run the claim is exactly the previous one, and a caller that passes neither "
             + "parameter must keep that behavior");
  }

  [Test]
  public async Task ClaimWork_InterleavedStreams_EachChosenStreamLeasesItsShareOfTheBoundAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var streams = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).ToList();
    var seeded = await _seedInterleavedAsync(conn, streams, rowsPerStream: 30);

    // Row bound 40 over 4 streams: each stream's share is 10, under its run of 25.
    var (outbox, _) = await _claimAsync(conn, instance, maxStreams: 4, maxOutboxRows: 40, runLength: 25);
    var held = await _heldAsync(conn, instance);

    await Assert.That(held.Count).IsEqualTo(40)
      .Because("the row bound is the outbox's own, not the stream window of 4");
    foreach (var stream in streams) {
      var mine = held.Where(h => h.StreamId == stream).Select(h => h.MessageId).ToList();
      await Assert.That(mine).IsEquivalentTo(seeded[stream].Take(10), TUnit.Assertions.Enums.CollectionOrdering.Matching)
        .Because("each chosen stream leases a prefix of its rows, an even share of the bound");
    }
    await Assert.That(outbox.Select(o => o.StreamId).Distinct().Count()).IsEqualTo(4)
      .Because("every stream holding work is offered to the drain");
  }

  [Test]
  public async Task ClaimOrphanedOutbox_RunStopsAtTheFirstRowItMayNotTakeAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    var sibling = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    await _registerInstanceAsync(conn, sibling);
    var deferred = Guid.CreateVersion7();
    var siblingHeld = Guid.CreateVersion7();
    var seeded = await _seedInterleavedAsync(conn, [deferred, siblingHeld], rowsPerStream: 10);

    // Stream one: its 5th row is a retry deferred into the future. Stream two: its 3rd row is under a
    // sibling's live lease (a lease that outlived the stream's ownership record, the case a run must
    // not jump).
    await using (var setup = conn.CreateCommand()) {
      setup.CommandText = @"
        UPDATE wh_outbox SET scheduled_for = NOW() + INTERVAL '1 hour' WHERE message_id = @deferredRow;
        UPDATE wh_outbox SET instance_id = @sibling, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @siblingRow;";
      setup.Parameters.AddWithValue("deferredRow", seeded[deferred][4]);
      setup.Parameters.AddWithValue("sibling", sibling);
      setup.Parameters.AddWithValue("siblingRow", seeded[siblingHeld][2]);
      await setup.ExecuteNonQueryAsync();
    }

    // Two heads (one per stream), a run of 10: everything past the first untakeable row stays put.
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"
        SELECT count(*) FROM claim_orphaned_outbox(
          @inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 1, NOW() - INTERVAL '10 minutes', 100, 10, 2)";
      cmd.Parameters.AddWithValue("inst", instance);
      _ = await cmd.ExecuteScalarAsync();
    }
    var held = await _heldAsync(conn, instance);

    await Assert.That(held.Where(h => h.StreamId == deferred).Select(h => h.MessageId))
      .IsEquivalentTo(seeded[deferred].Take(4), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a run ends at a deferred retry; a row beyond it would publish before the retry");
    await Assert.That(held.Where(h => h.StreamId == siblingHeld).Select(h => h.MessageId))
      .IsEquivalentTo(seeded[siblingHeld].Take(2), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a run ends at a row another instance holds; a row beyond it would publish past one that has not been");
  }

  [Test]
  public async Task ClaimWork_ReOffersOneRowPerHeldStreamAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var longStream = Guid.CreateVersion7();
    var seededLong = await _seedInterleavedAsync(conn, [longStream], rowsPerStream: 10);
    var others = Enumerable.Range(0, 2).Select(_ => Guid.CreateVersion7()).ToList();
    _ = await _seedInterleavedAsync(conn, others, rowsPerStream: 1);

    // Everything is leased on the first claim (a window wide enough for every head); the second, at a
    // window of three streams, only re-offers what is held.
    _ = await _claimAsync(conn, instance, maxStreams: 12, maxOutboxRows: 100, runLength: 10);
    await Assert.That((await _heldAsync(conn, instance)).Count).IsEqualTo(12);
    var (reoffer, _) = await _claimAsync(conn, instance, maxStreams: 3, maxOutboxRows: 100, runLength: 10);

    await Assert.That(reoffer.Select(r => r.StreamId)).IsEquivalentTo([longStream, .. others])
      .Because("the re-offer is a batch of held STREAMS; a batch of held rows let the long stream's "
             + "run fill it and hid the other two streams from the drain");
    await Assert.That(reoffer.Single(r => r.StreamId == longStream).WorkId).IsEqualTo(seededLong[longStream][0])
      .Because("a stream stands as its most urgent held row, its head");
  }

  [Test]
  public async Task ClaimWork_OutboxAcquisitionFillsItsBound_RaisesTheFullNoticeAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var streams = Enumerable.Range(0, 2).Select(_ => Guid.CreateVersion7()).ToList();
    _ = await _seedInterleavedAsync(conn, streams, rowsPerStream: 20);

    var (_, full) = await _claimAsync(conn, instance, maxStreams: 2, maxOutboxRows: 10, runLength: 10);
    var (_, reoffer) = await _claimAsync(conn, instance, maxStreams: 2, maxOutboxRows: 0, runLength: 10);

    await Assert.That(full).Contains("whizbang.outbox_acquisition_full=true")
      .Because("an acquisition that leased its whole bound left at least that much behind it, so the "
             + "claim loop may claim again at once");
    await Assert.That(reoffer).DoesNotContain("whizbang.outbox_acquisition_full=true")
      .Because("a claim that acquired nothing (here, a zero bound: the caller is holding all it may) "
             + "is a re-offer, and re-claiming on a re-offer is the spin the loop's spacing prevents");
  }

  [Test]
  public async Task ClaimWork_OutboxAcquisitionBelowItsBound_DoesNotRaiseTheFullNoticeAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    _ = await _seedInterleavedAsync(conn, [Guid.CreateVersion7()], rowsPerStream: 5);

    var (_, notices) = await _claimAsync(conn, instance, maxStreams: 2, maxOutboxRows: 10, runLength: 10);

    await Assert.That(notices).DoesNotContain("whizbang.outbox_acquisition_full=true")
      .Because("five rows under a bound of ten is the whole backlog; claiming again at once would find nothing");
  }

  [Test]
  public async Task ProcessOutboxFailures_MidRun_ReleasesTheRestOfTheRunToBeRetriedInOrderAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var stream = Guid.CreateVersion7();
    var seeded = (await _seedInterleavedAsync(conn, [stream], rowsPerStream: 10))[stream];

    _ = await _claimAsync(conn, instance, maxStreams: 1, maxOutboxRows: 100, runLength: 10);
    await Assert.That((await _heldAsync(conn, instance)).Count).IsEqualTo(10);

    // The drain publishes rows 1-3, and row 4 fails.
    await _completeAsync(conn, seeded.Take(3));
    await using (var fail = conn.CreateCommand()) {
      fail.CommandText = "SELECT report_failures('outbox', @f::jsonb)";
      fail.Parameters.AddWithValue("f",
        $"[{{\"MessageId\":\"{seeded[3]}\",\"CompletedStatus\":0,\"Error\":\"broker refused\",\"Reason\":0}}]");
      await fail.ExecuteNonQueryAsync();
    }

    await using (var check = conn.CreateCommand()) {
      check.CommandText = @"
        SELECT count(*) FILTER (WHERE instance_id IS NULL AND attempts = 0)
        FROM wh_outbox WHERE message_id = ANY(@rest)";
      check.Parameters.AddWithValue("rest", seeded.Skip(4).ToArray());
      var released = Convert.ToInt32(await check.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
      await Assert.That(released).IsEqualTo(6)
        .Because("the rest of the run is released with the attempt its claim charged refunded; left "
               + "leased, the next drain published it ahead of the failed row's retry");
    }

    // While the retry is deferred nothing on the stream may be claimed.
    _ = await _claimAsync(conn, instance, maxStreams: 1, maxOutboxRows: 100, runLength: 10);
    await Assert.That(await _heldAsync(conn, instance)).IsEmpty()
      .Because("the failed row's deferred retry holds every later row of its stream behind it");

    // The backoff elapses: the stream is claimed again from the failed row, in order.
    await using (var elapse = conn.CreateCommand()) {
      elapse.CommandText = "UPDATE wh_outbox SET scheduled_for = NOW() - INTERVAL '1 second' WHERE message_id = @id";
      elapse.Parameters.AddWithValue("id", seeded[3]);
      await elapse.ExecuteNonQueryAsync();
    }
    _ = await _claimAsync(conn, instance, maxStreams: 1, maxOutboxRows: 100, runLength: 10);
    var retried = (await _heldAsync(conn, instance)).ConvertAll(h => h.MessageId);

    await Assert.That(retried).IsEquivalentTo(seeded.Skip(3), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the retry leads the rest of its run, so the stream publishes in order");
  }

  [Test]
  public async Task ContinueOutboxStreams_LeasesTheNextRunAndReturnsOnlyRowsAfterTheCursorAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var stream = Guid.CreateVersion7();
    var seeded = (await _seedInterleavedAsync(conn, [stream], rowsPerStream: 30))[stream];

    _ = await _claimAsync(conn, instance, maxStreams: 1, maxOutboxRows: 100, runLength: 10);
    // Rows 1-10 are published and their completion has not landed; rows 11-12 were leased here by a
    // later claim and not yet published. Row 12 must come back as unpublished work of the run.
    await using (var setup = conn.CreateCommand()) {
      setup.CommandText = "UPDATE wh_outbox SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes', attempts = 1 WHERE message_id = ANY(@ids)";
      setup.Parameters.AddWithValue("inst", instance);
      setup.Parameters.AddWithValue("ids", seeded.Skip(10).Take(2).ToArray());
      await setup.ExecuteNonQueryAsync();
    }

    var continued = await _continueAsync(conn, instance, stream, after: seeded[9], run: 10);

    await Assert.That(continued).IsEquivalentTo(seeded.Skip(10).Take(10), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the continuation returns the rows after the last one published, in order: the two "
             + "already held and the eight it leased, and never a published row awaiting its completion");
    var held = (await _heldAsync(conn, instance)).ConvertAll(h => h.MessageId);
    await Assert.That(held).IsEquivalentTo(seeded.Take(20), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the next run is leased from the lease the stream already holds");

    // Completions land and the drain continues again from its new cursor.
    await _completeAsync(conn, seeded.Take(20));
    var next = await _continueAsync(conn, instance, stream, after: seeded[19], run: 10);
    await Assert.That(next).IsEquivalentTo(seeded.Skip(20), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await _completeAsync(conn, seeded.Skip(20));
    var none = await _continueAsync(conn, instance, stream, after: seeded[29], run: 10);
    await Assert.That(none).IsEmpty()
      .Because("a drained stream has nothing to continue");
  }

  [Test]
  public async Task ContinueOutboxStreams_AStreamThisInstanceDoesNotOwn_IsNotContinuedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    var sibling = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    await _registerInstanceAsync(conn, sibling);
    var stream = Guid.CreateVersion7();
    var seeded = (await _seedInterleavedAsync(conn, [stream], rowsPerStream: 5))[stream];
    _ = await _claimAsync(conn, sibling, maxStreams: 1, maxOutboxRows: 100, runLength: 2);

    var continued = await _continueAsync(conn, instance, stream, after: seeded[0], run: 10);

    await Assert.That(continued).IsEmpty()
      .Because("the stream's lease is the sibling's; continuing it here would interleave one stream across two instances");
    await Assert.That(await _heldAsync(conn, instance)).IsEmpty();
  }

  [Test]
  public async Task ContinueOutboxStreams_NoStreams_ReturnsNothingAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_continue_outbox_streams(@inst, ARRAY[]::uuid[], ARRAY[]::uuid[], 10)";
    cmd.Parameters.AddWithValue("inst", Guid.CreateVersion7());
    var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    await Assert.That(count).IsEqualTo(0);
  }

  [Test]
  public async Task TheRunIndex_ServesTheRunWalkAndTheStoreEmptinessProbeAsync() {
    // idx_outbox_stream_run replaced idx_outbox_stream_unpublished (160). The probe that index was
    // added for must still be answered from an index, and the run walk must be a range scan on the
    // run index in its own order (no Sort), or the run is priced by the stream's backlog.
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _seedInterleavedAsync(conn, [stream], rowsPerStream: 50);

    var probe = await _explainAsync(conn, @"
      SELECT 1 FROM wh_outbox o
      WHERE o.stream_id = @s AND o.processed_at IS NULL AND o.published_at IS NULL
        AND (o.scheduled_for IS NULL OR o.scheduled_for <= NOW())
      LIMIT 1", stream);
    var walk = await _explainAsync(conn, @"
      SELECT o.message_id FROM wh_outbox o
      WHERE o.stream_id = @s AND o.processed_at IS NULL AND o.published_at IS NULL AND o.coalesce_group IS NULL
      ORDER BY o.created_at, o.message_id
      LIMIT 10", stream);

    await Assert.That(probe).Contains("idx_outbox_stream_run");
    await Assert.That(walk).Contains("idx_outbox_stream_run");
    await Assert.That(walk).DoesNotContain("Sort")
      .Because("the index is keyed in the walk's order, so the LIMIT stops the scan inside the index");
  }

  private static async Task<string> _explainAsync(NpgsqlConnection conn, string sql, Guid stream) {
    await using (var off = conn.CreateCommand()) {
      // Ten-row tables make a sequential scan the cheapest plan; the question here is whether an
      // index CAN serve the shape, so take the scan off the table.
      off.CommandText = "SET enable_seqscan = off";
      await off.ExecuteNonQueryAsync();
    }
    try {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "EXPLAIN " + sql;
      cmd.Parameters.AddWithValue("s", stream);
      var lines = new List<string>();
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        lines.Add(reader.GetString(0));
      }
      return string.Join('\n', lines);
    } finally {
      await using var on = conn.CreateCommand();
      on.CommandText = "RESET enable_seqscan";
      await on.ExecuteNonQueryAsync();
    }
  }

  private static async Task<List<Guid>> _continueAsync(NpgsqlConnection conn, Guid instanceId, Guid stream, Guid after, int run) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT message_id FROM wh_continue_outbox_streams(@inst, @streams, @after, @run)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.AddWithValue("streams", new[] { stream });
    cmd.Parameters.AddWithValue(nameof(after), new[] { after });
    cmd.Parameters.AddWithValue(nameof(run), run);
    var rows = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetGuid(0));
    }
    return rows;
  }
}
