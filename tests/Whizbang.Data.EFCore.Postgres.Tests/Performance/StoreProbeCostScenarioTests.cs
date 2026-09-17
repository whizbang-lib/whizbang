using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What it costs to store a message on a stream the outbox has never seen, while the outbox holds a
/// backlog for other streams.
/// </summary>
/// <remarks>
/// <para>
/// Before its first insert for a stream, <c>store_outbox_messages</c> asks whether that stream has
/// any pending outbox row, so it knows whether to ring a doorbell. The question is cheap; finding
/// the answer was not. The lookup is by stream id with no status predicate, and every stream_id
/// index on the outbox is partial on a condition it cannot satisfy, so nothing served it and it
/// scanned the table. The answer it wants most often is "none", which is the case that scans to
/// exhaustion.
/// </para>
/// <para>
/// This is the same defect as the doorbell scenario beside it, one function over, and it was
/// measured on a deployed fleet at about the same size: the probe was a third of what remained of
/// that database's block reads after the doorbell was fixed.
/// </para>
/// <para>
/// The seed is the shape that produces it and nothing more convenient: the backlog is PENDING rather
/// than settled, because the only index condition the old plan could use was
/// <c>published_at IS NULL</c>, so a settled backlog is invisible to it and measures nothing. The
/// probed streams are fresh, because a stream that already has a pending row short-circuits on the
/// first match. Autovacuum is off and nothing is vacuumed, for the reason recorded in
/// <see cref="WorkCostRecorder"/>: a vacuumed table is all-visible and measures a plan shape a queue
/// under load never has.
/// </para>
/// </remarks>
[Category("Benchmark")]
[Category("Performance")]
public class StoreProbeCostScenarioTests : EFCoreTestBase {
  /// <summary>Streams the outbox already holds pending work for.</summary>
  private const int BACKLOG_STREAMS = 200;
  /// <summary>Pending rows each of them holds, none of them published.</summary>
  private const int PENDING_PER_STREAM = 100;
  /// <summary>Store calls measured.</summary>
  private const int CALLS = 10;
  /// <summary>Messages per call, each on a stream of its own, as a producer's batch spans streams.</summary>
  private const int MESSAGES_PER_CALL = 4;

  private static readonly string[] MEASURED_TABLES = ["wh_outbox", "wh_perspective_events"];

  [Test]
  [Timeout(1800000)]
  public async Task StoreProbeCost_FreshStreamAgainstAPendingBacklog_ReportAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var recorder = new WorkCostRecorder(ConnectionString);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Store emptiness probe against a pending backlog");

    var instance = await _registerInstanceAsync(conn);
    await _seedPendingBacklogAsync(conn);

    var window = await recorder.MeasureAsync(conn, MEASURED_TABLES, async () => {
      for (var call = 0; call < CALLS; call++) {
        await _storeAsync(conn, instance, call, cancellationToken);
      }
    });

    foreach (var table in MEASURED_TABLES) {
      var cost = window.Tables[table];
      report.Measure($"store.{table}.tuples_read_per_call",
        (double)cost.SequentialTuplesRead / CALLS, "tuples/call");
      report.Measure($"store.{table}.seq_scans_per_call",
        (double)cost.SequentialScans / CALLS, "scans/call");
    }
    report.Measure("store.rows_written_per_call",
      (double)window.Tables["wh_outbox"].RowsInserted / CALLS, "rows/call");
    // The ratio the deployed fleet reported, and the one that makes a scan obvious whatever the
    // absolute numbers are: a store should read about what it writes, not thousands of times more.
    var written = Math.Max(window.Tables["wh_outbox"].RowsInserted, 1);
    report.Measure("store.wh_outbox.tuples_read_per_row_written",
      (double)window.Tables["wh_outbox"].SequentialTuplesRead / written, "tuples/row");

    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());
    await _writeReportAsync("store-probe", rendered, report.RenderBaselineLines());

    await Assert.That(window.Tables["wh_outbox"].RowsInserted).IsGreaterThan(0)
      .Because("the store has to have written something, or the ratios below have no denominator and "
        + "a scenario that stores nothing would report a perfect score");
    await Assert.That(report.Breaches).IsEmpty()
      .Because($"a declared ceiling was passed. {rendered}\nA store asks whether a stream has pending "
        + "work so it knows whether to ring; the answer is one index probe when an index carries the "
        + "predicate, and a scan of every unpublished row in the table when none does");
  }

  /// <summary>
  /// An outbox mid-import: pending, unpublished rows for many streams. Not settled rows -- the old
  /// plan's only index condition was published_at IS NULL, so settled rows are invisible to it.
  /// </summary>
  private static async Task _seedPendingBacklogAsync(NpgsqlConnection conn) {
    await using var seed = conn.CreateCommand();
    seed.CommandText = @"
      ALTER TABLE wh_outbox SET (autovacuum_enabled = false);
      ALTER TABLE wh_perspective_events SET (autovacuum_enabled = false);

      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
         stream_id, partition_number)
      SELECT gen_random_uuid(), 'topic', 'TestEvent', jsonb_build_object('pad', repeat('x', 200)), '{}',
             1, 0, NOW(),
             ('00000000-0000-0000-0000-' || lpad(s::text, 12, '0'))::uuid,
             compute_partition(('00000000-0000-0000-0000-' || lpad(s::text, 12, '0'))::uuid)
      FROM generate_series(1, @streams) s CROSS JOIN generate_series(1, @per_stream);

      ANALYZE wh_outbox; ANALYZE wh_perspective_events;";
    seed.Parameters.AddWithValue("streams", BACKLOG_STREAMS);
    seed.Parameters.AddWithValue("per_stream", PENDING_PER_STREAM);
    seed.CommandTimeout = 900;
    await seed.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// One producer batch, on streams nothing has stored before: the first-message case, which is when
  /// the probe has to prove emptiness rather than stop at the first match.
  /// </summary>
  private static async Task _storeAsync(
      NpgsqlConnection conn, Guid instance, int call, CancellationToken cancellationToken) {
    var messages = new List<Dictionary<string, object?>>(MESSAGES_PER_CALL);
    for (var i = 0; i < MESSAGES_PER_CALL; i++) {
      // Well above the seeded range, so every probed stream is one the outbox has never held.
      var stream = new Guid($"00000000-0000-0000-0000-{900000 + (call * MESSAGES_PER_CALL) + i:D12}");
      messages.Add(new Dictionary<string, object?> {
        ["MessageId"] = Guid.NewGuid().ToString(),
        ["StreamId"] = stream.ToString(),
        ["Destination"] = "topic",
        ["MessageType"] = "TestEvent",
        ["EnvelopeType"] = "TestEnvelope",
        ["EventData"] = new Dictionary<string, object?> { ["pad"] = new string('y', 200) },
        ["Metadata"] = new Dictionary<string, object?>(),
        ["Flags"] = 0,
      });
    }

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
