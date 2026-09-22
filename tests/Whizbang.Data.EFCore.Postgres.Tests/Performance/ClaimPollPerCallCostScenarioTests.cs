using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What one claim poll costs, and whether that cost follows the batch it was asked for or the depth
/// of the streams it happens to find.
/// </summary>
/// <remarks>
/// <para>
/// This measures cost per CALL, which is a different quantity from the cost per row returned that
/// <c>ClaimWorkPlanShapeTests</c> gates. A poll that returns twelve rows and one that returns a
/// hundred cost nearly the same, so a good ratio can sit on top of a poll that is far too expensive
/// to run several times a second. Per-call cost is what a single-pod service feels, because the poll
/// and the request handlers share one process: a poll that touches tens of thousands of blocks
/// blocks everything else while it runs.
/// </para>
/// <para>
/// Cost is measured in blocks and rows, never in elapsed time. The latency this stands in for is
/// real, but wall clock moves with the machine and the cache while pages touched is a property of
/// the plan and the data, so it is the same number here and on a server. The connection between
/// them is monotone and that is all the scenario needs: a poll that touches far fewer pages cannot
/// take longer.
/// </para>
/// <para>
/// The measure is taken at three stream depths against the same batch and the same stream count, so
/// depth is the only thing that varies. One number alone cannot distinguish a poll that is expensive
/// because it was asked for a lot from a poll that is expensive because of what it found, and that
/// distinction is the whole finding: the same code was reported at 4,575, 12,022 and 29,018 blocks a
/// call on three deployed services, which turns out to be one code path at three data shapes rather
/// than three problems. The ceiling is declared once and has to hold at every depth.
/// </para>
/// </remarks>
[Category("Benchmark")]
[Category("Performance")]
public class ClaimPollPerCallCostScenarioTests : EFCoreTestBase {
  /// <summary>Streams the work spreads over. Held constant across the depths.</summary>
  private const int STREAMS = 100;
  /// <summary>The claim's batch, in streams. Held constant across the depths.</summary>
  private const int BATCH = 10;
  /// <summary>Polls measured at each depth.</summary>
  private const int POLLS = 6;
  /// <summary>
  /// The depths measured, from a service with short-lived streams to the shape that made a poll cost
  /// tens of thousands of blocks. The ceiling has to hold at every one of them: the property worth
  /// having is that per-call cost is bounded by a number declared here rather than by a consumer's
  /// data shape, and a ceiling that holds only at the shallow end does not say that.
  /// </summary>
  private static readonly int[] DEPTHS = [12, 50, 200];

  private static readonly string[] MEASURED_TABLES =
    ["wh_outbox", "wh_inbox", "wh_perspective_events", "wh_event_store"];

  [Test]
  [Timeout(1800000)]
  public async Task ClaimPollPerCallCost_DoesNotFollowStreamDepth_ReportAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var recorder = new WorkCostRecorder(ConnectionString);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Claim poll cost per call, by stream depth");

    var measures = new List<DepthMeasure>();
    foreach (var depth in DEPTHS) {
      var measured = await _measureAtDepthAsync(conn, recorder, depth, cancellationToken);
      measures.Add(measured);
      report.Measure($"claim.blocks_per_call.depth_{depth}", measured.BlocksPerCall, "blocks/call");
      report.Measure($"claim.rows_leased_per_call.depth_{depth}", measured.RowsLeasedPerCall, "rows/call");
    }
    // Recorded, not gated. It says how much more a poll costs purely because the streams it found
    // are deeper, and it is the number that fell from near-linear to sublinear when the row bound
    // landed. It is deliberately not a ceiling: the deepest fixture holds sixteen times the rows of
    // the shallowest, so some of this ratio is a larger index being deeper to descend rather than a
    // poll doing more work, and gating a ratio between two different table sizes would conflate the
    // two. The absolute ceiling at every depth is the property that matters.
    report.Measure("claim.depth_cost_multiple",
      measures[^1].BlocksPerCall / Math.Max(measures[0].BlocksPerCall, 1), "x");

    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());
    await _writeReportAsync("claim-per-call", rendered, report.RenderBaselineLines());

    await Assert.That(measures[0].BlocksPerCall).IsGreaterThan(0)
      .Because("the poll has to have done something, or the multiple below divides by nothing and a "
        + "scenario that never polls would report a perfect score");
    await Assert.That(report.Breaches).IsEmpty()
      .Because($"a declared ceiling was passed. {rendered}\nA poll is asked for a batch of streams and "
        + "its cost has to follow that batch. When it instead leases every pending event of each stream "
        + "it takes, a service whose streams are deep pays for its own data shape on every poll, several "
        + "times a second, in the same process as its request handlers");
  }

  private readonly record struct DepthMeasure(double BlocksPerCall, double RowsLeasedPerCall);

  /// <summary>
  /// Fills to one stream depth and measures the polls. Each depth gets its own fill, so the
  /// measurements differ in depth and in nothing else.
  /// </summary>
  private static async Task<DepthMeasure> _measureAtDepthAsync(
      NpgsqlConnection conn, WorkCostRecorder recorder, int depth, CancellationToken cancellationToken) {
    var poller = await _fillAtDepthAsync(conn, depth);
    // The first poll after a fill does the one-time bookkeeping a steady state does not repeat.
    _ = await _claimAsync(conn, poller, cancellationToken);

    var leasedBefore = await _leasedRowsAsync(conn);
    var window = await recorder.MeasureAsync(conn, MEASURED_TABLES, async () => {
      for (var i = 0; i < POLLS; i++) {
        _ = await _claimAsync(conn, poller, cancellationToken);
      }
    });
    var leasedAfter = await _leasedRowsAsync(conn);

    var blocks = MEASURED_TABLES.Sum(t => window.Tables[t].Blocks);
    return new DepthMeasure(
      (double)blocks / POLLS,
      (double)(leasedAfter - leasedBefore) / POLLS);
  }

  /// <summary>Perspective events this instance holds a live lease on: what a poll leased.</summary>
  private static async Task<long> _leasedRowsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT count(*) FROM wh_perspective_events
      WHERE instance_id IS NOT NULL AND lease_expiry > NOW() AND processed_at IS NULL";
    return (long?)await cmd.ExecuteScalarAsync() ?? 0L;
  }

  private static async Task<int> _claimAsync(
      NpgsqlConnection conn, Guid poller, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT count(*) FROM claim_work(
        p_instance_id => @id, p_service_name => 'perf', p_host_name => 'perf-host', p_process_id => 1,
        p_max_streams => @batch, p_partition_count => 10000, p_lease_seconds => 300,
        p_max_rows => @batch)";
    cmd.Parameters.AddWithValue("id", poller);
    cmd.Parameters.AddWithValue("batch", BATCH);
    cmd.CommandTimeout = 600;
    return Convert.ToInt32(
      await cmd.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// A fresh workload at one stream depth: unowned perspective events over many streams, at mixed
  /// priorities, with autovacuum off and nothing vacuumed.
  /// </summary>
  private static async Task<Guid> _fillAtDepthAsync(NpgsqlConnection conn, int depth) {
    var poller = Guid.NewGuid();
    await using var fill = conn.CreateCommand();
    fill.CommandText = @"
      ALTER TABLE wh_outbox SET (autovacuum_enabled = false);
      ALTER TABLE wh_inbox SET (autovacuum_enabled = false);
      ALTER TABLE wh_perspective_events SET (autovacuum_enabled = false);
      ALTER TABLE wh_event_store SET (autovacuum_enabled = false);

      TRUNCATE wh_perspective_events, wh_active_streams, wh_notify_state;
      DELETE FROM wh_service_instances;

      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@poller, 'perf', 'perf-host', 1, NOW(), NOW(), '{}'::jsonb);

      INSERT INTO wh_perspective_events
        (stream_id, perspective_name, event_id, status, attempts, created_at, partition_number, priority)
      SELECT st.stream_id, 'TestPerspective', gen_random_uuid(), 0, 0,
             NOW() - (st.ordinal * INTERVAL '1 millisecond') + (r * INTERVAL '1 microsecond'),
             compute_partition(st.stream_id),
             CASE st.ordinal % 3 WHEN 0 THEN 50 WHEN 1 THEN 150 ELSE 250 END
      FROM (
        SELECT gen_random_uuid() AS stream_id, s AS ordinal FROM generate_series(1, @streams) s
      ) st CROSS JOIN generate_series(1, @depth) r;

      ANALYZE wh_perspective_events;";
    fill.Parameters.AddWithValue("poller", poller);
    fill.Parameters.AddWithValue("streams", STREAMS);
    fill.Parameters.AddWithValue(nameof(depth), depth);
    fill.CommandTimeout = 900;
    await fill.ExecuteNonQueryAsync();
    return poller;
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
