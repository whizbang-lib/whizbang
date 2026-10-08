// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What one claim poll costs when the oldest pending outbox rows belong to streams a live peer owns, and what the
/// liveness calls that were reported waiting beside it cost on their own.
/// </summary>
/// <remarks>
/// <para>
/// Under a bulk load the oldest pending outbox rows are the unleased tails of streams a live peer owns: the peer
/// leases a run at a time and the rest of each stream waits, unowned, in the outbox. The outbox acquisition chose
/// its heads by walking every pending row in arrival order and testing each for ownership, so every other instance's
/// poll walked all of them, found nothing, and did it again on the next poll. Two calls captured on a deployed
/// database visited 79,590,235 and 79,583,173 shared buffers to return 13 and 29 rows.
/// </para>
/// <para>
/// Measured at two backlog depths behind the same streams, so depth is the only thing that varies. The ceiling is
/// on outbox blocks per poll and is a property of the design: the acquisition examines a window of eight times the
/// heads it chooses, so its cost is fixed by the batch it was asked for, whatever the peer holds.
/// </para>
/// <para>
/// The liveness calls (heartbeat, role vote, due-schedule claim) are measured against the same loaded database and
/// recorded without a ceiling. They were reported at 8 to 16 seconds on average; what they do is a few blocks, which
/// is the evidence that their time was spent waiting, not working.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[Category("Benchmark")]
[Category("Performance")]
public class OutboxAcquisitionCostScenarioTests : EFCoreTestBase {
  /// <summary>Streams the peer owns. Held constant across the depths.</summary>
  private const int STREAMS = 100;
  /// <summary>The claim's stream window (heads) and row bound.</summary>
  private const int BATCH = 25;
  private const int MAX_ROWS = 100;
  /// <summary>Polls measured at each depth.</summary>
  private const int POLLS = 5;
  /// <summary>Pending rows behind each of the peer's streams.</summary>
  private static readonly int[] DEPTHS = [100, 400];

  private static readonly Guid _poller = new("aaaaaaaa-0000-0000-0000-0000000000a1");
  private static readonly Guid _peer = new("bbbbbbbb-0000-0000-0000-0000000000b2");

  private static readonly string[] MEASURED_TABLES = ["wh_outbox", "wh_active_streams"];

  [Test]
  [Timeout(1800000)]
  public async Task ClaimPoll_BacklogOwnedByALivePeer_CostDoesNotFollowTheBacklog_ReportAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var recorder = new WorkCostRecorder(ConnectionString);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Claim poll cost, backlog owned by a live peer");

    var blocksPerPoll = new List<double>();
    foreach (var depth in DEPTHS) {
      await _fillAsync(conn, depth);
      // The first poll on a session compiles the functions; a poller pays that once, so it is not a poll's cost.
      _ = await _claimAsync(conn, cancellationToken);

      var window = await recorder.MeasureAsync(conn, MEASURED_TABLES, async () => {
        for (var i = 0; i < POLLS; i++) {
          _ = await _claimAsync(conn, cancellationToken);
        }
      });
      var outboxBlocks = (double)window.Tables["wh_outbox"].Blocks / POLLS;
      blocksPerPoll.Add(outboxBlocks);
      report.Measure($"claim.peer_backlog.wh_outbox.blocks_per_poll.depth_{depth}", outboxBlocks, "blocks/poll");
      report.Measure($"claim.peer_backlog.wh_outbox.seq_scans_per_poll.depth_{depth}",
        (double)window.Tables["wh_outbox"].SequentialScans / POLLS, "scans/poll");
    }

    var liveness = await recorder.MeasureAsync(conn,
      ["wh_service_instances", "wh_role_assignments", "wh_role_candidates", "wh_schedules"], async () => {
        for (var i = 0; i < POLLS; i++) {
          await _execAsync(conn, $"SELECT record_heartbeat('{_poller}'::uuid, 'perf', 'perf-host', 1)");
          await _execAsync(conn,
            $"SELECT * FROM wh_vote_role('perf-role', '{_poller}'::uuid, INTERVAL '30 seconds', INTERVAL '5 seconds', "
            + "4242, ARRAY[1, 0], NULL)");
          await _execAsync(conn,
            $"SELECT * FROM wh_claim_due_schedules('{_poller}'::uuid, NOW() + INTERVAL '30 seconds', 10000, 100)");
        }
      });
    report.Measure("liveness.heartbeat_vote_schedule.blocks_per_round",
      (double)liveness.Tables.Values.Sum(t => t.Blocks) / POLLS, "blocks/round");

    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());
    await _writeReportAsync("outbox-acquisition", rendered, report.RenderBaselineLines());

    await Assert.That(blocksPerPoll[0]).IsGreaterThan(0)
      .Because("the poll has to have read the outbox, or a scenario that never reached the acquisition would "
        + "report a perfect score");
    await Assert.That(report.Breaches).IsEmpty()
      .Because($"a declared ceiling was passed. {rendered}\nA poll is asked for a batch and its cost has to "
        + "follow that batch. When it walks every pending row of streams a live peer owns, every other instance "
        + "pays for the peer's backlog on every poll");
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 600;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int> _claimAsync(NpgsqlConnection conn, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT count(*) FROM claim_work(
        p_instance_id => @id, p_service_name => 'perf', p_host_name => 'perf-host', p_process_id => 1,
        p_max_streams => @batch, p_partition_count => 10000, p_lease_seconds => 300,
        p_max_rows => @rows)";
    cmd.Parameters.AddWithValue("id", _poller);
    cmd.Parameters.AddWithValue("batch", BATCH);
    cmd.Parameters.AddWithValue("rows", MAX_ROWS);
    cmd.CommandTimeout = 600;
    return Convert.ToInt32(
      await cmd.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// The poller and a live peer. The peer owns every stream under a live stream lease, and each stream's pending
  /// rows are unowned, as they are behind a run the peer is draining. Autovacuum off, nothing vacuumed.
  /// </summary>
  private static async Task _fillAsync(NpgsqlConnection conn, int depth) {
    await _execAsync(conn, $"""
      ALTER TABLE wh_outbox SET (autovacuum_enabled = false);
      ALTER TABLE wh_active_streams SET (autovacuum_enabled = false);
      TRUNCATE wh_outbox, wh_active_streams;
      DELETE FROM wh_service_instances;

      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES ('{_poller}', 'perf', 'perf-host', 1, NOW() + INTERVAL '1 hour', NOW(), jsonb_build_object()),
             ('{_peer}', 'perf', 'perf-host', 2, NOW() + INTERVAL '1 hour', NOW(), jsonb_build_object());

      CREATE TEMP TABLE IF NOT EXISTS perf_peer_streams (n int, stream_id uuid);
      TRUNCATE perf_peer_streams;
      INSERT INTO perf_peer_streams SELECT n, gen_random_uuid() FROM generate_series(1, {STREAMS}) n;

      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry, last_activity_at)
      SELECT ps.stream_id, compute_partition(ps.stream_id, 10000), '{_peer}'::uuid, NOW() + INTERVAL '1 hour', NOW()
      FROM perf_peer_streams ps;

      INSERT INTO wh_outbox (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
                             stream_id, partition_number, is_event, status, attempts, created_at)
      SELECT gen_random_uuid(), 'topic-perf', 'Perf.Events.SomethingHappened, Perf', 'MessageEnvelope',
             jsonb_build_object('payload', repeat('x', 200)), jsonb_build_object('hops', jsonb_build_array()),
             jsonb_build_object('t', 'tenant-perf'), ps.stream_id, compute_partition(ps.stream_id, 10000), true, 1, 0,
             NOW() - INTERVAL '1 hour' + ((r * {STREAMS} + ps.n) * INTERVAL '1 millisecond')
      FROM perf_peer_streams ps CROSS JOIN generate_series(1, {depth}) r;

      ANALYZE wh_outbox;
      ANALYZE wh_active_streams;
      """);
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
