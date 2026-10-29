// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What the stream lifecycle ledger (#1732) adds to the outbox emit chain, the hottest write path: the same batches run
/// through the chain with the ledger upsert (migration 260) and through the chain as migration 216 defined it, on the
/// same database, alternating round by round so neither side runs on a warmer cache or a larger store.
/// </summary>
/// <remarks>
/// <para>
/// Two batch shapes, both 500 events: 50 streams that already have ledger rows taking 10 events each (the upsert
/// updates), and 500 new streams taking one event each (the upsert inserts). A perspective subscribes to the event, so
/// the chain does its whole job. Blocks are counted per table over the call (the ledger's are attributable to it alone);
/// the server's execution time from <c>EXPLAIN ANALYZE</c> is reported beside them and gates nothing.
/// </para>
/// <para>
/// The ceilings are design properties: the ledger writes one row per stream per batch, every update of a stream that
/// already has a row is heap-only even when the batch rewrites every row on the row's page, and the ledger's own blocks
/// are a few per stream, whatever the events carry. Measured (three alternating rounds, medians): existing streams
/// 31,921 blocks and 58.2 ms before, 32,186 blocks (250 of them the ledger's) and 61.0 ms after; new streams 33,298 blocks
/// and 56.8 ms before, 36,278 blocks (3,040 the ledger's) and 66.0 ms after.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/stream-end-of-life#what-it-costs</docs>
[Category("Benchmark")]
[Category("Performance")]
public class StreamLifecycleEmitCostScenarioTests : EFCoreTestBase {
  private const int EVENTS = 500;
  private const int WARM_STREAMS = 50;
  private const int ROUNDS = 3;
  private const string EVENT_TYPE = "Perf.Ledger.SomethingHappened, Perf.Ledger";
  private const string FUNCTION_START = "CREATE OR REPLACE FUNCTION public._emit_event_store_chain(";
  private const string FUNCTION_END = "$$ LANGUAGE plpgsql;";

  private static readonly string[] _tables = [
    "wh_outbox", "wh_event_store", "wh_event_body", "wh_stream_digests", "wh_perspective_events", "wh_stream_lifecycle",
  ];

  private static readonly DateTimeOffset _now = new(2026, 10, 29, 14, 5, 0, TimeSpan.Zero);

  [Test]
  [Timeout(1800000)]
  public async Task EmitChain_WithAndWithoutTheLedger_ReportAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var recorder = new WorkCostRecorder(ConnectionString);
    var report = new PerformanceBaseline.Report(PerformanceBaseline.Load(PerformanceBaseline.DefaultPath),
      "Outbox emit chain, with and without the stream lifecycle ledger");
    var withLedger = _chainAsMigrationDefinedIt("260_");
    var withoutLedger = _chainAsMigrationDefinedIt("216_");

    // A perspective subscribes to the event, so the chain does its whole job: store, body, digest and projection work.
    await _executeAsync(conn, $"""
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), '{EVENT_TYPE}', 'perspective', 'Perf.Ledger.Perspective', 'perf', '{EVENT_TYPE}', NOW(), NOW())
      """);
    // Warm streams for every round of both sides, stored with the ledger on, so each has history and a ledger row.
    var warm = new List<Guid[]>();
    for (var i = 0; i < 2 * ROUNDS; i++) {
      warm.Add(await _seedWarmStreamsAsync(conn));
    }
    await _executeAsync(conn, "ANALYZE");

    var after = new List<Shapes>();
    var before = new List<Shapes>();
    for (var round = 0; round < ROUNDS; round++) {
      await _executeAsync(conn, withLedger);
      after.Add(await _measureBothShapesAsync(conn, recorder, warm[2 * round]));
      await _executeAsync(conn, withoutLedger);
      before.Add(await _measureBothShapesAsync(conn, recorder, warm[(2 * round) + 1]));
    }
    await _executeAsync(conn, withLedger);

    foreach (var (shape, pick) in new (string, Func<Shapes, Shape>)[] { ("existing", s => s.Existing), ("new", s => s.New) }) {
      var a = after.ConvertAll(s => pick(s));
      var b = before.ConvertAll(s => pick(s));
      report.Measure($"emit_chain.{shape}_streams.blocks_before", _median(b, x => x.Blocks), "blocks/batch");
      report.Measure($"emit_chain.{shape}_streams.blocks_after", _median(a, x => x.Blocks), "blocks/batch");
      report.Measure($"emit_chain.{shape}_streams.ledger_blocks", _median(a, x => x.LedgerBlocks), "blocks/batch");
      report.Measure($"emit_chain.{shape}_streams.ledger_rows_written", _median(a, x => x.LedgerRows), "rows/batch");
      report.Measure($"emit_chain.{shape}_streams.ms_before", _median(b, x => x.ExecutionMs), "ms/batch");
      report.Measure($"emit_chain.{shape}_streams.ms_after", _median(a, x => x.ExecutionMs), "ms/batch");
    }
    Console.WriteLine(report.Render());
    foreach (var (side, runs) in new[] { ("after", after), ("before", before) }) {
      foreach (var run in runs) {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
          $"{side}: existing {run.Existing.Blocks} blocks ({run.Existing.LedgerBlocks} ledger, {run.Existing.LedgerHotUpdates} heap-only) {run.Existing.ExecutionMs:F1} ms; " +
          $"new {run.New.Blocks} blocks ({run.New.LedgerBlocks} ledger) {run.New.ExecutionMs:F1} ms"));
      }
    }

    foreach (var run in after) {
      await Assert.That(run.Existing.LedgerRows).IsEqualTo((long)WARM_STREAMS)
        .Because("one row per stream per batch: ten events on a stream are one update, not ten");
      await Assert.That(run.Existing.LedgerHotUpdates).IsEqualTo((long)WARM_STREAMS)
        .Because("an append inside the hour changes no indexed column, and every page has room for its rows' new versions");
      await Assert.That(run.New.LedgerRows).IsEqualTo((long)EVENTS);
      // Measured at 5 blocks per stream (the arbiter's key probe, the row's page, its heap-only rewrite) for 10 events a
      // stream, and 6 per new stream (the insert and its key entry). Per stream, never per event.
      await Assert.That(run.Existing.LedgerBlocks).IsLessThanOrEqualTo(6L * WARM_STREAMS)
        .Because("the ledger costs its rows' pages and one key probe per stream, not anything per event");
      await Assert.That(run.New.LedgerBlocks).IsLessThanOrEqualTo(8L * EVENTS)
        .Because("a new stream costs one row insert and one key entry");
    }
    foreach (var run in before) {
      await Assert.That(run.Existing.LedgerRows).IsEqualTo(0L).Because("the control runs really are the chain without the ledger");
    }
  }

  private sealed record Shape(long Blocks, long LedgerBlocks, long LedgerRows, long LedgerHotUpdates, double ExecutionMs);

  private sealed record Shapes(Shape Existing, Shape New);

  private static double _median(List<Shape> runs, Func<Shape, double> value) {
    var sorted = runs.Select(value).Order().ToList();
    return sorted[sorted.Count / 2];
  }

  private async Task<Shapes> _measureBothShapesAsync(NpgsqlConnection conn, WorkCostRecorder recorder, Guid[] warm) {
    var existing = await _measureAsync(conn, recorder, [.. Enumerable.Range(0, EVENTS).Select(i => warm[i % warm.Length])]);
    var fresh = await _measureAsync(conn, recorder, [.. Enumerable.Range(0, EVENTS).Select(_ => (Guid)TrackedGuid.New())]);
    return new Shapes(existing, fresh);
  }

  private async Task<Shape> _measureAsync(NpgsqlConnection conn, WorkCostRecorder recorder, Guid[] streamPerEvent) {
    var (ids, instance) = await _queueAsync(conn, streamPerEvent);
    var hotBefore = await _hotUpdatesAsync(conn);
    var executionMs = 0.0;
    var window = await recorder.MeasureAsync(conn, _tables, async () => executionMs = await _emitAsync(conn, ids, instance));
    var ledger = window.Tables["wh_stream_lifecycle"];
    return new Shape(
      window.Tables.Values.Sum(t => t.Blocks),
      ledger.Blocks,
      ledger.RowsInserted + ledger.RowsUpdated,
      await _hotUpdatesAsync(conn) - hotBefore,
      executionMs);
  }

  /// <summary>Fifty streams with five events each, stored through the chain in one batch, so their rows share pages.</summary>
  private static async Task<Guid[]> _seedWarmStreamsAsync(NpgsqlConnection conn) {
    var streams = Enumerable.Range(0, WARM_STREAMS).Select(_ => (Guid)TrackedGuid.New()).ToArray();
    var (ids, instance) = await _queueAsync(conn, [.. Enumerable.Range(0, WARM_STREAMS * 5).Select(i => streams[i % WARM_STREAMS])]);
    _ = await _emitAsync(conn, ids, instance);
    return streams;
  }

  private static async Task<(Guid[] Ids, Guid Instance)> _queueAsync(NpgsqlConnection conn, Guid[] streamPerEvent) {
    var instance = (Guid)TrackedGuid.New();
    var ids = streamPerEvent.Select(_ => (Guid)TrackedGuid.New()).ToArray();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
         status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
      SELECT u.id, 'perf-topic', @type, 'env',
             jsonb_build_object('p', jsonb_build_object('orderId', u.id, 'note', repeat('n', 400))),
             '{}'::jsonb, '{"t":"tenant-perf"}'::jsonb, 1, 0, @inst, @lease, 0, u.sid, true, 0, @now
      FROM unnest(@ids, @sids) AS u(id, sid)
      """;
    cmd.Parameters.AddWithValue("type", EVENT_TYPE);
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue("lease", _now.AddMinutes(5));
    cmd.Parameters.AddWithValue("now", _now);
    cmd.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    cmd.Parameters.AddWithValue("sids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, streamPerEvent);
    _ = await cmd.ExecuteNonQueryAsync();
    return (ids, instance);
  }

  /// <summary>Runs the chain under EXPLAIN ANALYZE and returns the server's execution time for the call.</summary>
  private static async Task<double> _emitAsync(NpgsqlConnection conn, Guid[] ids, Guid instance) {
    await using var emit = conn.CreateCommand();
    emit.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    emit.Parameters.AddWithValue("inst", instance);
    emit.Parameters.AddWithValue("lease", _now.AddMinutes(5));
    emit.Parameters.AddWithValue("now", _now);
    var plan = (string)(await emit.ExecuteScalarAsync())!;
    using var json = JsonDocument.Parse(plan);
    return json.RootElement[0].GetProperty("Execution Time").GetDouble();
  }

  /// <summary>Heap-only updates of the ledger so far, read over a connection of its own after the worker flushes.</summary>
  private async Task<long> _hotUpdatesAsync(NpgsqlConnection conn) {
    await _executeAsync(conn, "SELECT pg_stat_force_next_flush()");
    // From the test's connection string: an open connection's own string has lost its password.
    await using var other = new NpgsqlConnection(ConnectionString);
    await other.OpenAsync();
    await using var cmd = other.CreateCommand();
    cmd.CommandText = "SELECT COALESCE((SELECT n_tup_hot_upd FROM pg_stat_user_tables WHERE relname = 'wh_stream_lifecycle'), 0)";
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  /// <summary>The outbox emit chain exactly as the named migration wrote it.</summary>
  private static string _chainAsMigrationDefinedIt(string migrationPrefix) {
    var sql = new PostgresMigrationProvider().GetMigrations().Single(m => m.Name.StartsWith(migrationPrefix, StringComparison.Ordinal)).Sql;
    var start = sql.IndexOf(FUNCTION_START, StringComparison.Ordinal);
    var end = sql.IndexOf(FUNCTION_END, start, StringComparison.Ordinal) + FUNCTION_END.Length;
    return sql[start..end];
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
