using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What the store's perspective emptiness probe costs, measured separately for each of the three
/// states a stream can be in, because only one of them was ever measured and it was the cheap one.
/// </summary>
/// <remarks>
/// <para>
/// Before its first insert for a stream, <c>store_outbox_messages</c> asks whether that stream has
/// any pending perspective event, so it knows whether to ring the perspective doorbell. Migration
/// 160 fixed the identical question on the outbox and recorded that this one was already served by
/// an index "at three buffers". That was true, and it was measured on a stream the table had never
/// held. A stream in that state is the one case where the question is cheap to answer.
/// </para>
/// <para>
/// A stream has three states and the probe behaves differently in each:
/// </para>
/// <list type="bullet">
/// <item><description><b>Fresh</b> -- the table has never held the stream. One index descent, no rows.</description></item>
/// <item><description><b>Has pending work</b> -- the first row read is a match, so any plan stops at once.</description></item>
/// <item><description><b>Drained</b> -- the stream's perspective work is all done, so there is no match at all.</description></item>
/// </list>
/// <para>
/// The drained state is not an edge case. It is what every long-lived stream becomes as soon as its
/// work is processed, and it stays that way between bursts, so it is the state the probe is in most
/// of the time. It was also the state that scanned the whole table: under a bare <c>LIMIT 1</c> the
/// planner prices a sequential scan as though the first row it reads will match, because
/// <c>processed_at IS NULL</c> is true of nearly every row in a table carrying a backlog. On a
/// drained stream there is no match, so "stop at the first one" becomes "read all of them". As with
/// the outbox probe, the answer the caller most often wants is the one that scans to exhaustion.
/// </para>
/// <para>
/// The seed is the shape that produces it and is written down here rather than left to a helper. The
/// backlog is PENDING, because the misestimate that chooses the sequential scan is driven by
/// <c>processed_at IS NULL</c> being common; a table of settled rows does not produce it. The probed
/// streams are three, one in each state, and the drained one carries real processed history so its
/// rows exist and are simply not claimable. Autovacuum is off and nothing is vacuumed, for the
/// reason recorded in <see cref="WorkCostRecorder"/>.
/// </para>
/// <para>
/// The gate is declared once and has to hold in ALL THREE states. That is the property worth having:
/// what this probe costs is a function of the question asked, not of how much history the stream
/// being asked about happens to carry. A ceiling that held only for a fresh stream is exactly the
/// measurement that missed this defect the first time.
/// </para>
/// </remarks>
[Category("Benchmark")]
[Category("Performance")]
public class PerspectiveProbeCostScenarioTests : EFCoreTestBase {
  /// <summary>Streams carrying a pending perspective backlog, which is what makes the misestimate.</summary>
  private const int BACKLOG_STREAMS = 200;
  /// <summary>Pending perspective events each of them holds.</summary>
  private const int PENDING_PER_STREAM = 100;
  /// <summary>Processed events the drained stream carries: real history, none of it claimable.</summary>
  private const int DRAINED_HISTORY = 400;
  /// <summary>Store calls measured per stream state.</summary>
  private const int CALLS = 5;

  private static readonly string[] MEASURED_TABLES = ["wh_perspective_events", "wh_outbox"];

  /// <summary>The stream whose perspective work is all done. Carries history, has nothing pending.</summary>
  private static readonly Guid DRAINED_STREAM = new("00000000-0000-7000-8000-000000000777");

  [Test]
  [Timeout(1800000)]
  public async Task PerspectiveProbeCost_HoldsInEveryStreamState_ReportAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var recorder = new WorkCostRecorder(ConnectionString);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(
      baseline, "Store perspective emptiness probe, by stream state");

    var instance = await _registerInstanceAsync(conn);
    var backlogStream = await _seedAsync(conn);

    // One state per measured window, so a cheap state cannot average away an expensive one. That
    // averaging is how the drained case stayed invisible: it is one call in a handful, and the
    // handful looked fine.
    foreach (var (state, stream) in new[] {
        ("drained", (Guid?)DRAINED_STREAM),
        ("fresh", null),
        ("pending", backlogStream) }) {
      var window = await recorder.MeasureAsync(conn, MEASURED_TABLES, async () => {
        for (var call = 0; call < CALLS; call++) {
          await _storeAsync(conn, instance, state, call, stream, cancellationToken);
        }
      });
      var cost = window.Tables["wh_perspective_events"];
      report.Measure($"probe.wh_perspective_events.blocks_per_call.{state}",
        (double)cost.Blocks / CALLS, "blocks/call");
      report.Measure($"probe.wh_perspective_events.seq_scans_per_call.{state}",
        (double)cost.SequentialScans / CALLS, "scans/call");
      report.Measure($"probe.wh_perspective_events.tuples_read_per_call.{state}",
        (double)cost.SequentialTuplesRead / CALLS, "tuples/call");
    }

    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());
    await _writeReportAsync("perspective-probe", rendered, report.RenderBaselineLines());

    await Assert.That(report.Breaches).IsEmpty()
      .Because($"a declared ceiling was passed. {rendered}\nThe store asks whether a stream has "
        + "pending perspective work so it knows whether to ring. That question costs one index "
        + "descent when the plan can use an index, and a scan of every pending row in the table "
        + "when the planner bets the first row it reads will match and the stream is drained. A "
        + "drained stream is what every long-lived stream becomes, so this is the steady state "
        + "rather than an edge, and the ceiling has to hold there as well as on a fresh stream");
  }

  /// <summary>
  /// A pending perspective backlog over many streams, plus one stream whose work is all processed.
  /// The backlog is what makes <c>processed_at IS NULL</c> common enough for the planner to expect
  /// a sequential scan to stop at its first row; without it the defect does not reproduce.
  /// </summary>
  /// <returns>A stream that holds pending work, for the third state.</returns>
  private static async Task<Guid> _seedAsync(NpgsqlConnection conn) {
    await using var seed = conn.CreateCommand();
    seed.CommandText = @"
      ALTER TABLE wh_outbox SET (autovacuum_enabled = false);
      ALTER TABLE wh_perspective_events SET (autovacuum_enabled = false);

      INSERT INTO wh_perspective_events
        (stream_id, perspective_name, event_id, status, attempts, created_at, partition_number, priority)
      SELECT st.stream_id, 'TestPerspective',
             ('00000000-0000-7000-8000-' || lpad((st.ordinal * 1000 + r)::text, 12, '0'))::uuid,
             0, 0, NOW(), compute_partition(st.stream_id), 150
      FROM (
        SELECT ('00000000-0000-7000-8000-' || lpad((100000 + s)::text, 12, '0'))::uuid AS stream_id,
               s AS ordinal
        FROM generate_series(1, @streams) s
      ) st CROSS JOIN generate_series(1, @per_stream) r;

      -- The drained stream: real processed history, nothing claimable.
      INSERT INTO wh_perspective_events
        (stream_id, perspective_name, event_id, status, attempts, created_at, partition_number,
         priority, processed_at)
      SELECT @drained, 'TestPerspective',
             ('00000000-0000-7000-8000-' || lpad((700000 + r)::text, 12, '0'))::uuid,
             2, 0, NOW(), compute_partition(@drained::uuid), 150, NOW()
      FROM generate_series(1, @history) r;

      ANALYZE wh_perspective_events; ANALYZE wh_outbox;";
    seed.Parameters.AddWithValue("streams", BACKLOG_STREAMS);
    seed.Parameters.AddWithValue("per_stream", PENDING_PER_STREAM);
    seed.Parameters.AddWithValue("history", DRAINED_HISTORY);
    seed.Parameters.AddWithValue("drained", DRAINED_STREAM);
    seed.CommandTimeout = 900;
    await seed.ExecuteNonQueryAsync();
    return new Guid("00000000-0000-7000-8000-000000100001");
  }

  /// <summary>
  /// One store call carrying a single message, so exactly one stream is probed per call. A null
  /// stream means a fresh one: a stream id this call invents and nothing has ever stored.
  /// </summary>
  private static async Task _storeAsync(
      NpgsqlConnection conn, Guid instance, string state, int call, Guid? stream,
      CancellationToken cancellationToken) {
    var target = stream ?? new Guid($"00000000-0000-7000-8000-{900000 + call:D12}");
    var messages = new[] {
      new Dictionary<string, object?> {
        ["MessageId"] = Guid.NewGuid().ToString(),
        ["StreamId"] = target.ToString(),
        ["Destination"] = "topic",
        ["MessageType"] = "TestEvent",
        ["EnvelopeType"] = "TestEnvelope",
        ["EventData"] = new Dictionary<string, object?> { ["state"] = state },
        ["Metadata"] = new Dictionary<string, object?>(),
        ["Flags"] = 0,
      },
    };

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT count(*) FROM store_outbox_messages(
        @messages::jsonb, @instance, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    cmd.Parameters.AddWithValue("messages", JsonSerializer.Serialize(messages));
    cmd.Parameters.AddWithValue(nameof(instance), instance);
    cmd.CommandTimeout = 300;
    _ = await cmd.ExecuteScalarAsync(cancellationToken);
  }

  private static async Task<Guid> _registerInstanceAsync(NpgsqlConnection conn) {
    var instance = Guid.NewGuid();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'perf', 'perf-host', 1, NOW(), NOW(), '{}'::jsonb)";
    cmd.Parameters.AddWithValue("id", instance);
    await cmd.ExecuteNonQueryAsync();
    return instance;
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
