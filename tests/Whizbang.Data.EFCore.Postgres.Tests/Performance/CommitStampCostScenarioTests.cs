// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What one commit-order stamp costs as the backlog of unstamped events grows (#1062).
/// </summary>
/// <remarks>
/// <para>
/// The stamp chose its batch by sorting every unstamped row by transaction id, which no index can serve, so every
/// call read the whole backlog and a drain cost the backlog squared over the batch. Since migration 197 it walks an
/// insertion-order index and stops at the batch.
/// </para>
/// <para>
/// Blocks per stamped row are recorded and not gated: the update writes every index of the event store for each row
/// it stamps, which is the work the batch buys and swamps the selection at a batch of a thousand. The gate is on
/// tuples read per stamped row, which counts what the call had to look at to choose its rows and is the same number
/// at every depth when the choice follows the batch.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
[Category("Benchmark")]
[Category("Performance")]
public class CommitStampCostScenarioTests : EFCoreTestBase {
  private const int BATCH = 1000;
  private const int STAMPS = 3;
  private const int VERSIONS = 10;
  /// <summary>Unstamped rows behind the stamps, as streams of <see cref="VERSIONS"/>.</summary>
  private static readonly int[] BACKLOG_STREAMS = [1_000, 4_000, 16_000];

  private static readonly string[] MEASURED_TABLES = ["wh_event_store"];

  [Test]
  [Timeout(1800000)]
  public async Task Stamp_ABacklogGrows_TheCostOfAStampDoesNot_ReportAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var recorder = new WorkCostRecorder(ConnectionString);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Commit-order stamp cost by backlog depth");
    await _execAsync(conn, "ALTER TABLE wh_event_store SET (autovacuum_enabled = false)");

    var filled = 0;
    var stampedAny = false;
    foreach (var streams in BACKLOG_STREAMS) {
      await _fillAsync(conn, streams - filled);
      filled = streams;
      var stamped = 0;
      var window = await recorder.MeasureAsync(conn, MEASURED_TABLES, async () => {
        for (var i = 0; i < STAMPS; i++) {
          stamped += await _stampAsync(conn, cancellationToken);
        }
      });
      stampedAny |= stamped > 0;
      var cost = window.Tables["wh_event_store"];
      var rows = Math.Max(stamped, 1);
      var depth = streams * VERSIONS;
      report.Measure($"stamp.backlog_{depth}.wh_event_store.tuples_read_per_stamped_row",
        (double)(cost.SequentialTuplesRead + cost.IndexTuplesRead) / rows, "tuples/row");
      report.Measure($"stamp.backlog_{depth}.wh_event_store.blocks_per_stamped_row", (double)cost.Blocks / rows, "blocks/row");
      report.Measure($"stamp.backlog_{depth}.wh_event_store.seq_scans_per_stamp", (double)cost.SequentialScans / STAMPS, "scans/stamp");
    }

    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());
    await _writeReportAsync("commit-stamp", rendered, report.RenderBaselineLines());

    await Assert.That(stampedAny).IsTrue()
      .Because("the stamps have to have stamped something, or a scenario that never reached the walk would report a "
        + "perfect score");
    await Assert.That(report.Breaches).IsEmpty()
      .Because($"a declared ceiling was passed. {rendered}\nA stamp is asked for a batch and what it reads to choose "
        + "that batch has to follow the batch, not the backlog behind it");
  }

  private static async Task<int> _stampAsync(NpgsqlConnection conn, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT stamp_pending_commit_sequences({BATCH})";
    cmd.CommandTimeout = 600;
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
  }

  private static Task _fillAsync(NpgsqlConnection conn, int streams) => _execAsync(conn, $"""
    INSERT INTO wh_event_store
      (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
    SELECT gen_random_uuid(), s.stream_id, s.stream_id, 'Perf', v, 'Perf.Events.Happened', NULL, NOW()
    FROM (SELECT gen_random_uuid() AS stream_id FROM generate_series(1, {streams})) s
    CROSS JOIN generate_series(1, {VERSIONS}) v;
    ANALYZE wh_event_store;
    """);

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 600;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _writeReportAsync(string scenario, string rendered, string baselineLines) {
    var dir = Environment.GetEnvironmentVariable("WHIZBANG_PERF_REPORT_DIR")
      ?? Path.Combine(AppContext.BaseDirectory, "perf-reports");
    Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(
      Path.Combine(dir, $"{scenario}.txt"),
      rendered + "\nBaseline lines for this run:\n" + baselineLines);
  }
}
