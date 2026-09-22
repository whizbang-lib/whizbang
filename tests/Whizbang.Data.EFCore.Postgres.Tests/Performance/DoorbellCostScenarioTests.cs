using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What it costs to announce work on streams that already carry history.
/// </summary>
/// <remarks>
/// <para>
/// The scenario that would have caught the defect this suite exists because of. A producer stores a
/// handful of messages on streams that have been running for a while, and rings a doorbell for
/// them. The question the measure answers is: does announcing a row cost what the row costs, or
/// what the stream has accumulated?
/// </para>
/// <para>
/// Not in a CI slice. <c>Benchmark</c> is the category the shard guard accepts in place of a shard,
/// and the CI matrix selects only <c>Shard1</c> through <c>Shard4</c>, so a class carrying
/// <c>Benchmark</c> and no shard runs in no slice by construction. <c>Performance</c> is beside it
/// so a person can select this suite by the name they think of it under.
/// </para>
/// </remarks>
[Category("Benchmark")]
[Category("Performance")]
public class DoorbellCostScenarioTests : EFCoreTestBase {
  /// <summary>Streams the doorbell names, as a store's batch spans several.</summary>
  private const int STREAMS = 8;
  /// <summary>Rows each of them has already settled: the history a ring must not read.</summary>
  private const int SETTLED_PER_STREAM = 2_500;
  /// <summary>Rings measured, so the per-ring figure is not one sample.</summary>
  private const int RINGS = 5;

  private static readonly string[] QUEUE_TABLES = ["wh_outbox", "wh_inbox", "wh_perspective_events"];

  [Test]
  [Timeout(1800000)]
  public async Task DoorbellCost_AgainstSettledHistory_ReportAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var recorder = new WorkCostRecorder(ConnectionString);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Doorbell against settled history");

    await _registerInstanceAsync(conn);
    var streams = await _seedSettledHistoryAsync(conn);

    foreach (var kind in new[] { "outbox", "inbox", "perspective" }) {
      var window = await recorder.MeasureAsync(conn, QUEUE_TABLES, async () => {
        for (var i = 0; i < RINGS; i++) {
          await using var ring = conn.CreateCommand();
          ring.CommandText = "SELECT notify_instance_owners(@kind, @streams::uuid[])";
          ring.Parameters.AddWithValue("kind", kind);
          ring.Parameters.AddWithValue("streams", streams);
          await ring.ExecuteNonQueryAsync(cancellationToken);
        }
      });
      foreach (var table in QUEUE_TABLES) {
        var cost = window.Tables[table];
        report.Measure($"doorbell.{kind}.{table}.tuples_read_per_ring",
          (double)cost.SequentialTuplesRead / RINGS, "tuples/ring");
        report.Measure($"doorbell.{kind}.{table}.seq_scans_per_ring",
          (double)cost.SequentialScans / RINGS, "scans/ring");
      }
    }

    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());
    await _writeReportAsync("doorbell", rendered, report.RenderBaselineLines());

    await Assert.That(report.Breaches).IsEmpty()
      .Because($"a declared ceiling was passed. {rendered}\nEvery measure here is per ring: a doorbell "
        + "names streams and must cost what it names, never what those streams have accumulated. A ceiling "
        + "is only declared in the baseline file where the number is a property of the design rather than of "
        + "the machine, so a breach is a plan shape that changed and not a slow runner");
  }

  /// <summary>
  /// Streams carrying settled rows in all three queue tables, none of them in the stream ledger, so
  /// a ring takes the deterministic-target branch. Autovacuum is off for the duration and nothing is
  /// vacuumed: a vacuumed fill leaves the visibility map all-visible and measures a plan shape a
  /// queue table under load never has.
  /// </summary>
  private static async Task<Guid[]> _seedSettledHistoryAsync(NpgsqlConnection conn) {
    var streams = Enumerable.Range(0, STREAMS).Select(_ => Guid.NewGuid()).ToArray();
    await using var seed = conn.CreateCommand();
    seed.CommandText = @"
      ALTER TABLE wh_outbox SET (autovacuum_enabled = false);
      ALTER TABLE wh_inbox SET (autovacuum_enabled = false);
      ALTER TABLE wh_perspective_events SET (autovacuum_enabled = false);

      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
         stream_id, partition_number, processed_at)
      SELECT gen_random_uuid(), 'topic', 'TestEvent', '{}', '{}', 0, 0, NOW(),
             s, compute_partition(s), NOW()
      FROM unnest(@streams::uuid[]) AS s CROSS JOIN generate_series(1, @per_stream);

      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at,
           stream_id, is_event)
        SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}', NOW(),
               s, TRUE
        FROM unnest(@streams::uuid[]) AS s CROSS JOIN generate_series(1, @per_stream)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts,
         partition_number, processed_at)
      SELECT message_id, stream_id, received_at, priority, is_event, 0, 0,
             compute_partition(stream_id), NOW()
      FROM m;

      INSERT INTO wh_perspective_events
        (stream_id, perspective_name, event_id, status, attempts, created_at, partition_number, processed_at)
      SELECT s, 'TestPerspective', gen_random_uuid(), 0, 0, NOW(), compute_partition(s), NOW()
      FROM unnest(@streams::uuid[]) AS s CROSS JOIN generate_series(1, @per_stream);

      DELETE FROM wh_active_streams WHERE stream_id = ANY(@streams::uuid[]);
      ANALYZE wh_outbox; ANALYZE wh_inbox; ANALYZE wh_perspective_events;";
    seed.Parameters.AddWithValue("streams", streams);
    seed.Parameters.AddWithValue("per_stream", SETTLED_PER_STREAM);
    seed.CommandTimeout = 900;
    await seed.ExecuteNonQueryAsync();
    return streams;
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (gen_random_uuid(), 'perf', 'perf-host', 1, NOW(), NOW(), '{}'::jsonb)";
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Leaves the report where a person and a follow-up run can both find it. The directory is the
  /// repository's own artifact directory when the runner sets one, and the build output otherwise.
  /// </summary>
  private static async Task _writeReportAsync(string scenario, string rendered, string baselineLines) {
    var dir = Environment.GetEnvironmentVariable("WHIZBANG_PERF_REPORT_DIR")
      ?? Path.Combine(AppContext.BaseDirectory, "perf-reports");
    Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(
      Path.Combine(dir, $"{scenario}.txt"),
      rendered + "\nBaseline lines for this run:\n" + baselineLines);
  }
}
