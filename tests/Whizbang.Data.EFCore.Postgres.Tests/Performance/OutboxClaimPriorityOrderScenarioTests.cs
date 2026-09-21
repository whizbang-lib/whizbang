using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// An urgent outbox row overtakes a deep backlog of bulk rows queued before it, and the claim that
/// does so is still priced by the batch rather than by what the instance holds.
/// </summary>
/// <remarks>
/// <para>
/// The corpus rule in <c>OutboxClaimHonoursPriorityTests</c> asserts the ORDER BY exists. That is a
/// property of the text; it does not show that an urgent row actually comes out first, and it would
/// still pass if an index or a plan change quietly defeated the ordering. This measures the
/// outcome: with 5,000 bulk rows queued ahead of it, where in the claim's output does the
/// interactive row land.
/// </para>
/// <para>
/// The second half matters as much as the first. Ordering by priority is easy to get at the cost of
/// reading everything the instance holds and sorting it, which is the defect migration 157 exists
/// to prevent -- and is exactly what happened on the first attempt at this fix, caught at 1,272
/// blocks against a 1,200 ceiling. So the scenario measures the position AND the blocks, because a
/// fix that gets the order right by scanning the backlog has traded one problem for another.
/// </para>
/// <para>
/// Work done, never wall clock, in the shape the rest of this suite uses.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/message-priority</docs>
[Category("Benchmark")]
[Category("Performance")]
public class OutboxClaimPriorityOrderScenarioTests : EFCoreTestBase {
  /// <summary>
  /// Bulk rows queued BEFORE the urgent one, so arrival order would put it last. Payloads are
  /// padded to the same width the claim-shape scenario uses: with empty payloads the whole table
  /// fits in a handful of pages and the planner scans it whatever the indexes say, which makes any
  /// block ceiling here unfailable and therefore worthless as a gate.
  /// </summary>
  private const int BULK_ROWS = 5_000;
  /// <summary>Streams the bulk spreads over; the claim returns stream ids.</summary>
  private const int BULK_STREAMS = 500;
  private const int BATCH = 100;

  private static async Task _execAsync(NpgsqlConnection c, string sql) {
    await using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 300;
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  [Timeout(1800000)]
  public async Task UrgentOutboxRow_OvertakesABulkBacklog_WithoutScanningItAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var instance = Guid.NewGuid();

    await _execAsync(conn, "TRUNCATE wh_outbox");
    await _execAsync(conn, $@"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES ('{instance}', 'test', 'test-host', 1, NOW(), NOW(), '{{}}')
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()");

    // Bulk first, so arrival order alone would return every one of these before the urgent row.
    await _execAsync(conn, $@"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
         stream_id, partition_number, instance_id, lease_expiry, priority)
      SELECT gen_random_uuid(), 'topic', 'BulkEvent', jsonb_build_object('pad', repeat('x', 1500)), '{{}}', 1, 0,
             NOW() - INTERVAL '1 hour' + (g * INTERVAL '1 millisecond'),
             ('00000000-0000-0000-0000-' || lpad(((g % {BULK_STREAMS}) + 1)::text, 12, '0'))::uuid,
             0, '{instance}', NOW() + INTERVAL '5 minutes', 250
      FROM generate_series(1, {BULK_ROWS}) g");

    // One interactive row, queued LAST. Arrival order puts it 5,001st.
    var urgentStream = Guid.NewGuid();
    await _execAsync(conn, $@"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
         stream_id, partition_number, instance_id, lease_expiry, priority)
      VALUES (gen_random_uuid(), 'topic', 'ChatReply', jsonb_build_object('pad', repeat('x', 1500)), '{{}}', 1, 0, NOW(),
              '{urgentStream}', 0, '{instance}', NOW() + INTERVAL '5 minutes', 50)");
    await _execAsync(conn, "ANALYZE wh_outbox");

    var recorder = new WorkCostRecorder(ConnectionString);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Outbox claim, urgent row behind a bulk backlog");

    // Where does the urgent stream land in the claim's output?
    int position;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = $@"
        WITH claimed AS (
          SELECT work_stream_id, ROW_NUMBER() OVER () AS pos
          FROM claim_work(p_instance_id => '{instance}', p_service_name => 'test',
                          p_host_name => 'test-host', p_process_id => 1,
                          p_max_streams => {BATCH}, p_partition_count => 10000,
                          p_lease_seconds => 300, p_max_rows => {BATCH}))
        SELECT COALESCE(MIN(pos), -1) FROM claimed WHERE work_stream_id = '{urgentStream}'";
      cmd.CommandTimeout = 300;
      position = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken),
        System.Globalization.CultureInfo.InvariantCulture);
    }

    var cost = await recorder.MeasureAsync(conn, ["wh_outbox"], async () => {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = $@"SELECT count(*) FROM claim_work(
        p_instance_id => '{instance}', p_service_name => 'test', p_host_name => 'test-host',
        p_process_id => 1, p_max_streams => {BATCH}, p_partition_count => 10000,
        p_lease_seconds => 300, p_max_rows => {BATCH})";
      cmd.CommandTimeout = 300;
      await cmd.ExecuteScalarAsync(cancellationToken);
    });

    report.Measure("outbox_priority.urgent_row_position", position, "position");
    report.Measure("outbox_priority.claim.blocks_per_call", cost.Tables["wh_outbox"].Blocks, "blocks/call");
    report.Measure("outbox_priority.claim.seq_scans_per_call", cost.Tables["wh_outbox"].SequentialScans, "scans/call");

    var rendered = report.Render();
    Console.WriteLine(rendered);
    await _writeReportAsync("outbox-claim-priority", rendered, report.RenderBaselineLines());

    await Assert.That(position).IsGreaterThan(0)
      .Because($"the urgent row must appear in a batch of {BATCH}; arrival order alone would place "
        + $"it {BULK_ROWS + 1}th and it would never be claimed while the backlog lasts.");

    await Assert.That(position).IsLessThanOrEqualTo(1)
      .Because("an interactive row is the most urgent thing the instance holds, so it comes first. "
        + "Anything else means the ordering is present in the text but not in the plan.");

    await Assert.That(report.Breaches).IsEmpty()
      .Because("a recorded measure passed its ceiling:\n" + string.Join("\n", report.Breaches));
  }

  /// <summary>Writes the run's report where a human or CI can read it; TUnit swallows stdout.</summary>
  private static async Task _writeReportAsync(string scenario, string rendered, string baselineLines) {
    var dir = Environment.GetEnvironmentVariable("WHIZBANG_PERF_REPORT_DIR")
      ?? Path.Combine(AppContext.BaseDirectory, "perf-reports");
    Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(
      Path.Combine(dir, $"{scenario}.txt"),
      rendered + "\nBaseline lines for this run:\n" + baselineLines);
  }
}
