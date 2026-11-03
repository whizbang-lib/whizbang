// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What converting historical full metadata costs (#1727, migration 298): a history of events written before slim stored
/// metadata (the full metadata in the row, no route id) is converted by <c>wh_convert_event_metadata_history</c> until it
/// reports complete, once per batch size, each on a fresh copy of the same history.
/// </summary>
/// <remarks>
/// <para>
/// The history: <see cref="EVENTS"/> events, one per stream, ids in time order as the write paths make them, each with a
/// 400-character payload and two hops as a dispatched event carries (about 1.5 KB of metadata), stored over the last
/// sixty days so half are inside the default trace window. Autovacuum is off on the body table while it is measured, so
/// the dead tuples the rewrite leaves are counted rather than cleaned up mid-run, and a checkpoint runs first, so each
/// batch pays the full-page images a long-running conversion pays in production.
/// </para>
/// <para>
/// Reported per batch size: per batch, the server's execution time (<c>EXPLAIN ANALYZE</c>), WAL bytes and shared
/// blocks over the tables it touches (medians); for the whole run, the heap-only share of the updates, the dead tuples
/// left, the body table's size before, after and after a plain <c>VACUUM</c>, and the metadata bytes per row before and
/// after. Only the row size gates: the converted row is under half the full one. The numbers are recorded on the docs
/// page (event-store#what-history-conversion-costs). A last run converts with 1,000-row batches and vacuums the table
/// every <see cref="CYCLE_ROWS"/> rows, outside the measured window, as autovacuum runs between maintenance cycles.
/// </para>
/// <para>
/// Measured (30,000 events, a shared build server): 500, 1,000 and 2,000 rows a batch took 124, 237 and 487 ms and
/// 2.08, 4.16 and 8.33 MB of WAL a batch (4,169 bytes a row; 5,826 on a second server), 10,866, 21,863 and 42,923
/// blocks. No update was heap-only. Metadata went from 1,476 to 490 bytes a row. With autovacuum off the table grew from
/// 61.5 MB to 92.2 MB and stayed there after VACUUM, with 30,000 dead tuples; vacuumed every 5,000 rows it grew to
/// 66.6 MB.
/// </para>
/// </remarks>
/// <docs>data/event-store#what-history-conversion-costs</docs>
[Category("Benchmark")]
[Category("Performance")]
public class MetadataHistoryConversionCostScenarioTests : EFCoreTestBase {
  private const int EVENTS = 30000;
  private const string EVENT_TYPE = "Perf.Metadata.SomethingHappened, Perf.Metadata";

  private static readonly int[] _batchSizes = [500, 1000, 2000];

  /// <summary>Rows between the vacuums of the last run: a stand-in for autovacuum running between maintenance cycles.</summary>
  private const int CYCLE_ROWS = 5000;

  private static readonly string[] _tables = ["wh_event_body", "wh_event_store", "wh_routes", "wh_traces_default"];

  private static readonly string _hops = StoredMetadataCorpus.HopsJson([
    StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders),
    new MessageHop {
      Type = HopType.Causation, ServiceInstance = StoredMetadataCorpus.Billing, Timestamp = StoredMetadataCorpus.Sent.AddSeconds(-2),
      CausationType = "Perf.Metadata.DoSomething", CorrelationId = StoredMetadataCorpus.Correlation, Topic = "commands",
      CallerMemberName = "HandleAsync", CallerFilePath = "/src/Perf/DoSomethingReceptor.cs", CallerLineNumber = 77,
    },
  ]);

  [Test]
  [Timeout(1800000)]
  public async Task Conversion_OnACopiedHistory_ReportAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var recorder = new WorkCostRecorder(ConnectionString);
    var report = new PerformanceBaseline.Report(PerformanceBaseline.Load(PerformanceBaseline.DefaultPath),
      "History conversion: full metadata rows rewritten to the slim form plus route id, per batch size");
    await _executeAsync(conn, "ALTER TABLE wh_event_body SET (autovacuum_enabled = false)");

    var runs = new List<Run>();
    foreach (var batchSize in _batchSizes) {
      runs.Add(await _runAsync(conn, recorder, batchSize, vacuumEveryRows: null));
    }
    runs.Add(await _runAsync(conn, recorder, 1000, vacuumEveryRows: CYCLE_ROWS));
    await _executeAsync(conn, "ALTER TABLE wh_event_body RESET (autovacuum_enabled)");

    foreach (var run in runs) {
      var key = $"history_conversion.batch_{run.BatchSize}{(run.VacuumEveryRows is int cycle ? $"_vacuum_every_{cycle}" : string.Empty)}";
      report.Measure($"{key}.ms", run.MedianMs, "ms/batch");
      report.Measure($"{key}.wal_bytes", run.MedianWal, "bytes/batch");
      report.Measure($"{key}.blocks", run.MedianBlocks, "blocks/batch");
      report.Measure($"{key}.hot_share", run.HotShare, "share");
      report.Measure($"{key}.dead_tuples", run.DeadTuples, "tuples");
      report.Measure($"{key}.table_bytes_before", run.TableBefore, "bytes");
      report.Measure($"{key}.table_bytes_after", run.TableAfter, "bytes");
      report.Measure($"{key}.table_bytes_after_vacuum", run.TableAfterVacuum, "bytes");
      report.Measure($"{key}.metadata_bytes_before", run.MetadataBefore, "bytes/row");
      report.Measure($"{key}.metadata_bytes_after", run.MetadataAfter, "bytes/row");
    }
    Console.WriteLine(report.Render());
    foreach (var run in runs) {
      Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"batch {run.BatchSize}{(run.VacuumEveryRows is int cycle ? $" vacuum every {cycle}" : string.Empty)}: {run.Batches} batches, median {run.MedianMs:F1} ms {run.MedianWal:F0} WAL B {run.MedianBlocks:F0} blocks; " +
        $"total WAL {run.TotalWal} B ({run.TotalWal / (double)EVENTS:F0} B/row); HOT {run.HotShare:P1}; dead {run.DeadTuples}; " +
        $"table {run.TableBefore} -> {run.TableAfter} -> vacuum {run.TableAfterVacuum}; metadata {run.MetadataBefore:F0} -> {run.MetadataAfter:F0} B/row; " +
        $"checkpoint {(run.Checkpointed ? "yes" : "not permitted")}"));
    }

    foreach (var run in runs) {
      await Assert.That(run.Converted).IsEqualTo((long)EVENTS).Because("every historical row is converted");
      await Assert.That(run.MetadataAfter).IsLessThan(run.MetadataBefore / 2).Because("the slim row keeps a fraction of the hops");
      await Assert.That(run.MedianWal).IsGreaterThan(0d).Because("control: the rewrite is measured");
    }
  }

  private sealed record Run(
    int BatchSize, int Batches, long Converted, double MedianMs, double MedianWal, double MedianBlocks, long TotalWal,
    double HotShare, long DeadTuples, long TableBefore, long TableAfter, long TableAfterVacuum,
    double MetadataBefore, double MetadataAfter, bool Checkpointed, int? VacuumEveryRows);

  private async Task<Run> _runAsync(NpgsqlConnection conn, WorkCostRecorder recorder, int batchSize, int? vacuumEveryRows) {
    await _executeAsync(conn, "TRUNCATE wh_event_body, wh_routes, wh_traces, wh_event_metadata_staging");
    await _executeAsync(conn, "DELETE FROM wh_event_store");
    await _executeAsync(conn, "UPDATE wh_metadata_history_conversion SET last_event_id = NULL, rows_converted = 0, completed_at = NULL");
    await _seedHistoryAsync(conn);
    await _executeAsync(conn, "VACUUM (ANALYZE) wh_event_body");
    await _executeAsync(conn, "ANALYZE wh_event_store");

    var tableBefore = await _scalarAsync<long>(conn, "SELECT pg_table_size('wh_event_body')");
    var metadataBefore = await _scalarAsync<decimal>(conn, "SELECT avg(pg_column_size(metadata)) FROM wh_event_body");
    var statsBefore = await _bodyStatsAsync(conn);
    var checkpointed = await _tryCheckpointAsync(conn);

    var ms = new List<double>();
    var wal = new List<double>();
    var blocks = new List<double>();
    long totalWal = 0;
    long converted = 0;
    var batches = 0;
    // Until the conversion reports complete; each batch's rows are the growth of the cursor row's count.
    while (await _scalarAsync<bool>(conn, "SELECT completed_at IS NULL FROM wh_metadata_history_conversion")) {
      var walStart = await _scalarAsync<string>(conn, "SELECT pg_current_wal_insert_lsn()::text");
      var convertedBefore = await _scalarAsync<long>(conn, "SELECT rows_converted FROM wh_metadata_history_conversion");
      var executionMs = 0.0;
      var window = await recorder.MeasureAsync(conn, _tables, async () => executionMs = await _convertAsync(conn, batchSize));
      var walBytes = (long)await _scalarAsync<decimal>(conn, $"SELECT pg_wal_lsn_diff(pg_current_wal_insert_lsn(), '{walStart}')");
      var batchRows = await _scalarAsync<long>(conn, "SELECT rows_converted FROM wh_metadata_history_conversion") - convertedBefore;
      totalWal += walBytes;
      if (batchRows == 0) {
        continue; // the call that found the end: it read nothing, so it is no batch's cost
      }
      batches++;
      converted += batchRows;
      if (vacuumEveryRows is int cycle && converted % cycle == 0) {
        await _executeAsync(conn, "VACUUM wh_event_body"); // outside the measured window, as autovacuum runs between cycles
      }
      ms.Add(executionMs);
      wal.Add(walBytes);
      blocks.Add(window.Tables.Values.Sum(t => t.Blocks));
    }

    var statsAfter = await _bodyStatsAsync(conn);
    var tableAfter = await _scalarAsync<long>(conn, "SELECT pg_table_size('wh_event_body')");
    var metadataAfter = await _scalarAsync<decimal>(conn, "SELECT avg(pg_column_size(metadata)) FROM wh_event_body");
    await _executeAsync(conn, "VACUUM wh_event_body");
    var tableAfterVacuum = await _scalarAsync<long>(conn, "SELECT pg_table_size('wh_event_body')");
    var updates = statsAfter.Updates - statsBefore.Updates;
    return new Run(
      batchSize, batches, converted, _median(ms), _median(wal), _median(blocks), totalWal,
      updates == 0 ? 0 : (statsAfter.HotUpdates - statsBefore.HotUpdates) / (double)updates,
      statsAfter.DeadTuples, tableBefore, tableAfter, tableAfterVacuum,
      (double)metadataBefore, (double)metadataAfter, checkpointed, vacuumEveryRows);
  }

  private static double _median(List<double> values) {
    var sorted = values.Order().ToList();
    return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
  }

  /// <summary>Rows as the write paths stored them before slim metadata: the full form in the row, no route id.</summary>
  private static async Task _seedHistoryAsync(NpgsqlConnection conn) {
    var ids = Enumerable.Range(0, EVENTS).Select(_ => (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New()).ToArray();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      SELECT u.id, u.id, u.id, 'Perf.Metadata.SomethingHappened', @type, 1, now() - ((u.ord % 60) * interval '1 day') - interval '1 hour'
      FROM unnest(@ids) WITH ORDINALITY AS u(id, ord)
      ORDER BY u.ord;
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      SELECT u.id, jsonb_build_object('orderId', u.id, 'note', repeat('n', 400)),
             jsonb_build_object('MessageId', u.id::text, 'Hops', @hops::jsonb)
      FROM unnest(@ids) WITH ORDINALITY AS u(id, ord)
      ORDER BY u.ord;
      """;
    cmd.Parameters.AddWithValue("type", EVENT_TYPE);
    cmd.Parameters.AddWithValue("hops", NpgsqlDbType.Text, _hops);
    cmd.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    _ = await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<bool> _tryCheckpointAsync(NpgsqlConnection conn) {
    try {
      await _executeAsync(conn, "CHECKPOINT");
      return true;
    } catch (PostgresException) {
      return false;
    }
  }

  private sealed record BodyStats(long Updates, long HotUpdates, long DeadTuples);

  /// <summary>The body table's counters, read on a connection of its own after the working backend flushed its own.</summary>
  private async Task<BodyStats> _bodyStatsAsync(NpgsqlConnection worker) {
    await _executeAsync(worker, "SELECT pg_stat_force_next_flush()");
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT n_tup_upd, n_tup_hot_upd, n_dead_tup FROM pg_stat_user_tables WHERE relname = 'wh_event_body'";
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return new BodyStats(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
  }

  /// <summary>One batch under EXPLAIN ANALYZE, which runs it whole: the server's execution time.</summary>
  private static async Task<double> _convertAsync(NpgsqlConnection conn, int batchSize) {
    await using var explain = conn.CreateCommand();
    explain.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) SELECT * FROM wh_convert_event_metadata_history(@batch)";
    explain.Parameters.AddWithValue("batch", batchSize);
    var plan = (string)(await explain.ExecuteScalarAsync())!;
    using var json = JsonDocument.Parse(plan);
    return json.RootElement[0].GetProperty("Execution Time").GetDouble();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (T)Convert.ChangeType(await cmd.ExecuteScalarAsync(), typeof(T), CultureInfo.InvariantCulture)!;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
