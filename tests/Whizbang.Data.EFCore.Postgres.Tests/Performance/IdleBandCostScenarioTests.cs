using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Priority;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// A deep idle backlog does not make an ordinary poll more expensive.
/// </summary>
/// <remarks>
/// <para>
/// This is the risk the idle band introduces, and the reason to measure it rather than reason
/// about it. The band adds a fourth bucket and an admission probe to a function every instance
/// calls several times a second. If that probe scans the band, or if the fourth lane is walked
/// when it has been withheld, then a feature whose whole purpose is to keep withheld work out of
/// the way would instead charge every poll in the fleet for the size of the backlog it is
/// withholding -- worse than not having the band at all, and worse the better it is working.
/// </para>
/// <para>
/// So the scenario puts a large, fresh idle backlog behind a small amount of ordinary work and
/// polls as a BUSY service, which is the case where none of the idle rows may be touched. What it
/// costs is compared against the same poll with no idle backlog at all. The two should be close:
/// the band should cost a probe, not a proportion.
/// </para>
/// <para>
/// The sequential-scan measure carries the ceiling that matters. Blocks move with page layout and
/// fill; a sequential scan of wh_inbox_state on this path is a plan defect whatever the machine,
/// which is the kind of number this suite says to gate on.
/// </para>
/// <para>
/// Work done, never wall clock, in the shape the rest of this suite uses.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/167_IdleBandIsWithheldWhileBusy.sql</code-under-test>
/// <docs>fundamentals/messaging/message-priority#the-idle-band</docs>
[Category("Benchmark")]
[Category("Performance")]
public class IdleBandCostScenarioTests : EFCoreTestBase {
  /// <summary>The withheld backlog. Large enough that touching it would show plainly.</summary>
  private const int IDLE_ROWS = 20_000;
  /// <summary>Ordinary pending work the poll is actually there to claim.</summary>
  private const int ORDINARY_ROWS = 500;
  private const int BATCH = 100;

  private static async Task _execAsync(NpgsqlConnection c, string sql) {
    await using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 300;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _seedAsync(NpgsqlConnection c, int priority, int rows, string age) =>
    await _execAsync(c, $@"
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at,
           stream_id, is_event, priority)
        SELECT gen_random_uuid(), 'TestHandler', 'TestEvent',
               jsonb_build_object('pad', repeat('x', 1500)), '{{}}',
               NOW() - INTERVAL '{age}' + (g * INTERVAL '1 millisecond'),
               gen_random_uuid(), TRUE, {priority}
        FROM generate_series(1, {rows}) g
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts,
         partition_number, instance_id, lease_expiry, chain_emitted_at)
      -- Already chain-emitted. These rows exist to fill the LANES; letting the claim also try to
      -- build an event-store chain for them would measure that path instead, and would need a
      -- whole valid envelope per row to do it.
      SELECT message_id, stream_id, received_at, priority, is_event, 0, 0, 0, NULL, NULL, NOW()
      FROM m");

  /// <summary>One busy poll: the idle band is fresh, so none of it may be taken.</summary>
  private static async Task<WorkCostRecorder.Window> _pollAsync(
      WorkCostRecorder recorder, NpgsqlConnection conn, Guid instance, CancellationToken ct) =>
    await recorder.MeasureAsync(conn, ["wh_inbox_state"], async () => {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = $@"SELECT count(*) FROM claim_work(
        p_instance_id => '{instance}', p_service_name => 'test', p_host_name => 'test-host',
        p_process_id => 1, p_max_streams => {BATCH}, p_partition_count => 10000,
        p_lease_seconds => 300, p_max_rows => {BATCH},
        p_idle_settled => false)";
      cmd.CommandTimeout = 300;
      await cmd.ExecuteScalarAsync(ct);
    });

  [Test]
  [Timeout(1800000)]
  public async Task ABusyPoll_IsPricedTheSame_WhetherOrNotADeepIdleBacklogExistsAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var instance = Guid.NewGuid();

    await _execAsync(conn, "TRUNCATE wh_inbox_state, wh_inbox");
    await _execAsync(conn, $@"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES ('{instance}', 'test', 'test-host', 1, NOW(), NOW(), '{{}}')
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()");

    var recorder = new WorkCostRecorder(ConnectionString);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Idle band, busy poll behind a withheld backlog");

    // Control: ordinary work only, no idle band at all.
    await _seedAsync(conn, WorkPriority.BACKGROUND, ORDINARY_ROWS, "1 hour");
    await _execAsync(conn, "VACUUM (ANALYZE) wh_inbox, wh_inbox_state");
    var withoutIdle = await _pollAsync(recorder, conn, instance, cancellationToken);

    // Now the same poll with a large, FRESH idle backlog behind it: withheld, every row of it.
    await _seedAsync(conn, WorkPriority.IDLE, IDLE_ROWS, "1 minute");
    await _execAsync(conn, "VACUUM (ANALYZE) wh_inbox, wh_inbox_state");
    var withIdle = await _pollAsync(recorder, conn, instance, cancellationToken);

    var idleBlocks = withIdle.Tables["wh_inbox_state"].Blocks;
    var idleScans = withIdle.Tables["wh_inbox_state"].SequentialScans;
    var idleTuples = withIdle.Tables["wh_inbox_state"].SequentialTuplesRead;

    report.Measure("idle_band.busy_poll.blocks_per_call", idleBlocks, "blocks/call");
    report.Measure("idle_band.busy_poll.seq_scans_per_call", idleScans, "scans/call");
    report.Measure("idle_band.busy_poll.seq_tuples_per_call", idleTuples, "tuples/call");
    report.Measure("idle_band.control_poll.blocks_per_call",
      withoutIdle.Tables["wh_inbox_state"].Blocks, "blocks/call");
    // The measure the design actually promises. Neither absolute figure is a property of the
    // design -- both move with page fill and with what else the fixture left behind -- but the
    // DIFFERENCE between them is: what does a withheld band of 20,000 rows add to a poll that is
    // refusing to touch it. A probe adds a walk; a scan would add the band.
    report.Measure("idle_band.withheld_backlog.extra_blocks_per_call",
      idleBlocks - withoutIdle.Tables["wh_inbox_state"].Blocks, "blocks/call");

    var rendered = report.Render();
    Console.WriteLine(rendered);
    await _writeReportAsync("idle-band-cost", rendered, report.RenderBaselineLines());

    await Assert.That(idleTuples).IsLessThan(IDLE_ROWS)
      .Because($"a busy poll read {idleTuples} tuples of wh_inbox_state sequentially while "
        + $"{IDLE_ROWS} idle rows were withheld; reading the band it is refusing to work is the "
        + "one thing this design must not do, and it would get worse as the band filled");

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
