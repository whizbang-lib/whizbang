// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Before and after, on the backlog shape that exposed #917: a few long streams, interleaved, drained by
/// one instance at the claim window's floor of 25 streams. Drives the SQL the claim loop and the drain
/// issue, in their order, and counts claim cycles: the measured defect was a drain rate of about one row
/// per stream per claim cycle, so cycles are what the fix has to cut, and the wall time of a drain is
/// cycles times the cycle (about two seconds when measured).
/// </summary>
/// <remarks>
/// <para>
/// BEFORE is the previous claim exactly: no outbox bound and no run, so the stream window is the row cap
/// and each stream moves when the claim cycle comes round. AFTER is the claim with the outbox's own bound
/// (1,000 rows) and a run of 100, followed by the drain's continuation rounds (up to 10 per cycle, each a
/// page of 100) from the leases the streams already hold.
/// </para>
/// <para>
/// The row count per stream defaults to 100 so the suite stays quick; set
/// <c>WHIZBANG_OUTBOX_RUN_MEASURE_ROWS</c> to reproduce a larger backlog (the report's 44 x 500).
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[Category("Shard2")]
public class OutboxStreamRunDrainMeasurementTests : EFCoreTestBase {

  private const int STREAMS = 44;
  private const int WINDOW = 25;

  private static int _rowsPerStream() =>
    int.TryParse(Environment.GetEnvironmentVariable("WHIZBANG_OUTBOX_RUN_MEASURE_ROWS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
      ? n
      : 100;

  [Test]
  public async Task ABacklogOnAFewLongStreams_DrainsInFarFewerClaimCyclesAsync() {
    var rowsPerStream = _rowsPerStream();
    var before = await _measureAsync(rowsPerStream, withRuns: false);
    var after = await _measureAsync(rowsPerStream, withRuns: true);

    Console.WriteLine(
      $"#917 drain of {STREAMS} streams x {rowsPerStream} rows at a window of {WINDOW}: "
      + $"BEFORE {before.Cycles} claim cycles, {before.Statements} statements, {before.Sql.TotalMilliseconds:F0} ms of SQL; "
      + $"AFTER {after.Cycles} claim cycles, {after.Statements} statements, {after.Sql.TotalMilliseconds:F0} ms of SQL. "
      + $"At a 2 s claim cycle: BEFORE ~{before.Cycles * 2} s, AFTER ~{(after.Cycles * 2) + after.Sql.TotalSeconds:F0} s.");

    await Assert.That(before.Published).IsEqualTo(STREAMS * rowsPerStream);
    await Assert.That(after.Published).IsEqualTo(STREAMS * rowsPerStream);
    await Assert.That(before.Cycles).IsGreaterThanOrEqualTo(STREAMS * rowsPerStream / WINDOW)
      .Because("the previous claim leased at most a window's worth of rows per cycle: one row on each of the oldest streams");
    await Assert.That(after.Cycles * 10).IsLessThan(before.Cycles)
      .Because("runs and continuation must cut the claim cycles a backlog of long streams needs by more than an order of magnitude");
    await Assert.That(after.InOrder).IsTrue();
    await Assert.That(before.InOrder).IsTrue();
  }

  private sealed record Measurement(int Cycles, int Statements, TimeSpan Sql, int Published, bool InOrder);

  private async Task<Measurement> _measureAsync(int rowsPerStream, bool withRuns) {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using (var clear = conn.CreateCommand()) {
      // A fresh start for each measurement, one instance: a second registered instance would split the
      // partitions and leave half the backlog to nobody.
      clear.CommandText = "DELETE FROM wh_outbox; DELETE FROM wh_active_streams; DELETE FROM wh_service_instances;";
      await clear.ExecuteNonQueryAsync();
    }
    var instance = Guid.CreateVersion7();
    await using (var hb = conn.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
        VALUES (@inst, 'test', 'test-host', 1, NOW(), NOW())";
      hb.Parameters.AddWithValue("inst", instance);
      await hb.ExecuteNonQueryAsync();
    }

    // Interleaved round-robin, message ids ascending with arrival: the shape a producer fanning work
    // onto a few streams writes.
    var streams = Enumerable.Range(0, STREAMS).Select(_ => Guid.CreateVersion7()).ToArray();
    var total = STREAMS * rowsPerStream;
    var ids = Enumerable.Range(0, total).Select(_ => Guid.CreateVersion7()).Order().ToArray();
    var rowStreams = Enumerable.Range(0, total).Select(i => streams[i % STREAMS]).ToArray();
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        SELECT m.id, 'test-topic', 'TestEvent', '{}', '{}', 0, 0,
               TIMESTAMPTZ '2026-01-01 00:00:00+00' + (m.ord * INTERVAL '1 millisecond'), m.stream, 0
        FROM unnest(@ids, @streams) WITH ORDINALITY AS m(id, stream, ord);
        ANALYZE wh_outbox;";
      ins.Parameters.AddWithValue("ids", ids);
      ins.Parameters.AddWithValue("streams", rowStreams);
      await ins.ExecuteNonQueryAsync();
    }

    var published = new Dictionary<Guid, List<Guid>>();
    var cycles = 0;
    var statements = 0;
    var sql = Stopwatch.StartNew();
    while (cycles < total + 10) {
      await using (var pending = conn.CreateCommand()) {
        pending.CommandText = "SELECT EXISTS (SELECT 1 FROM wh_outbox)";
        if (!(bool)(await pending.ExecuteScalarAsync())!) {
          break;
        }
      }
      cycles++;

      // The claim.
      await using (var claim = conn.CreateCommand()) {
        claim.CommandText = @"
          SELECT count(*) FROM claim_work(
            p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
            p_max_streams => @window, p_partition_count => 10000, p_lease_seconds => 300,
            p_max_outbox_rows => @rows, p_outbox_run_length => @run)";
        claim.Parameters.AddWithValue("inst", instance);
        claim.Parameters.AddWithValue("window", WINDOW);
        claim.Parameters.Add(new NpgsqlParameter("rows", NpgsqlTypes.NpgsqlDbType.Integer) { Value = withRuns ? 1000 : DBNull.Value });
        claim.Parameters.Add(new NpgsqlParameter("run", NpgsqlTypes.NpgsqlDbType.Integer) { Value = withRuns ? 100 : DBNull.Value });
        _ = await claim.ExecuteScalarAsync();
        statements++;
      }

      // The drain: publish what is held, then (after) continue each stream from its cursor.
      var batch = await _heldAsync(conn, instance);
      statements++;
      var round = 0;
      while (batch.Count > 0) {
        foreach (var (messageId, streamId) in batch) {
          if (!published.TryGetValue(streamId, out var list)) {
            published[streamId] = list = [];
          }
          list.Add(messageId);
        }
        await using (var complete = conn.CreateCommand()) {
          complete.CommandText = "SELECT complete_outbox_published(@ids)";
          complete.Parameters.AddWithValue("ids", batch.Select(b => b.MessageId).ToArray());
          await complete.ExecuteNonQueryAsync();
          statements++;
        }
        if (!withRuns || round++ >= 10) {
          break;
        }
        var cursors = batch.GroupBy(b => b.StreamId).Select(g => (Stream: g.Key, After: g.Max(x => x.MessageId))).ToArray();
        await using var cont = conn.CreateCommand();
        cont.CommandText = "SELECT message_id, stream_id FROM wh_continue_outbox_streams(@inst, @streams, @after, 100)";
        cont.Parameters.AddWithValue("inst", instance);
        cont.Parameters.AddWithValue("streams", cursors.Select(c => c.Stream).ToArray());
        cont.Parameters.AddWithValue("after", cursors.Select(c => c.After).ToArray());
        batch = [];
        await using (var reader = await cont.ExecuteReaderAsync()) {
          while (await reader.ReadAsync()) {
            batch.Add((reader.GetGuid(0), reader.GetGuid(1)));
          }
        }
        statements++;
      }
    }
    sql.Stop();

    var expected = new Dictionary<Guid, List<Guid>>();
    for (var i = 0; i < total; i++) {
      if (!expected.TryGetValue(rowStreams[i], out var list)) {
        expected[rowStreams[i]] = list = [];
      }
      list.Add(ids[i]);
    }
    var inOrder = expected.All(e => published.TryGetValue(e.Key, out var got) && got.SequenceEqual(e.Value));
    return new Measurement(cycles, statements, sql.Elapsed, published.Values.Sum(v => v.Count), inOrder);
  }

  private static async Task<List<(Guid MessageId, Guid StreamId)>> _heldAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT message_id, stream_id FROM wh_outbox
      WHERE instance_id = @inst AND processed_at IS NULL AND lease_expiry > NOW()
      ORDER BY stream_id, message_id";
    cmd.Parameters.AddWithValue("inst", instance);
    var held = new List<(Guid, Guid)>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      held.Add((reader.GetGuid(0), reader.GetGuid(1)));
    }
    return held;
  }
}
