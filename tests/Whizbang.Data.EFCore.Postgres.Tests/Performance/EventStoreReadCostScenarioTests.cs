// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// Two event-store reads that cost what the store holds rather than what they return: the digest closure's lane
/// discovery, and a collective replay's position read.
/// </summary>
/// <remarks>
/// <para>
/// <c>close_digest_epochs</c> found its lanes with <c>SELECT DISTINCT</c> over the whole event store on every call,
/// to return one value per origin service: on a deployed service 201 calls at 4.8 s average. A collective replay
/// read <c>event_type = ANY(...) AND scope ->> 't' = ...</c> with no index on either key: 361 calls at 1.6 s.
/// </para>
/// <para>
/// Each is measured at two store sizes, so size is the only thing that varies. The ceilings are on sequential scans
/// of the event store per call, which is a property of the design rather than of the machine: neither read has any
/// reason to visit a row it does not return. Blocks are recorded beside them as the same finding counted a second
/// way.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
[Category("Benchmark")]
[Category("Performance")]
public class EventStoreReadCostScenarioTests : EFCoreTestBase {
  /// <summary>Streams seeded at each store size; each stream holds 20 events.</summary>
  private static readonly int[] STREAM_COUNTS = [1000, 4000];
  private const int DEPTH = 20;
  private const int CALLS = 5;
  private const string TENANT = "tenant-07";

  private static readonly List<string> _collectiveTypes = [
    "Perf.Collectives.LedgerAdjusted0, Perf.Collectives",
    "Perf.Collectives.LedgerAdjusted1, Perf.Collectives",
    "Perf.Collectives.LedgerAdjusted2, Perf.Collectives",
  ];

  private static readonly string[] MEASURED_TABLES = ["wh_event_store"];

  [Test]
  [Timeout(1800000)]
  public async Task EventStoreReads_CostFollowsTheAnswerNotTheStore_ReportAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var recorder = new WorkCostRecorder(ConnectionString);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Event-store reads, by store size");
    var positions = CollectiveReplayApplier.CollectivePositions(
      new EFCoreFilterableEventStoreQuery(dbContext).Query, _collectiveTypes, TENANT);

    foreach (var streams in STREAM_COUNTS) {
      var events = streams * DEPTH;
      await _fillAsync(conn, streams);
      // Fold everything closable once, so the measured calls are the steady state: nothing left to close.
      await _execAsync(conn, "SELECT close_digest_epochs(NOW(), 3600, 100000)");

      var close = await recorder.MeasureAsync(conn, MEASURED_TABLES, async () => {
        for (var i = 0; i < CALLS; i++) {
          await _execAsync(conn, "SELECT close_digest_epochs(NOW(), 3600, 100000)");
        }
      });
      report.Measure($"digest_close.idle.wh_event_store.seq_scans_per_call.events_{events}",
        (double)close.Tables["wh_event_store"].SequentialScans / CALLS, "scans/call");
      report.Measure($"digest_close.idle.wh_event_store.blocks_per_call.events_{events}",
        (double)close.Tables["wh_event_store"].Blocks / CALLS, "blocks/call");

      var found = 0;
      var read = await recorder.MeasureAsync(conn, MEASURED_TABLES, async () => {
        for (var i = 0; i < CALLS; i++) {
          found = (await positions.ToListAsync(cancellationToken)).Count;
        }
      });
      report.Measure($"collective_positions.wh_event_store.seq_scans_per_read.events_{events}",
        (double)read.Tables["wh_event_store"].SequentialScans / CALLS, "scans/read");
      report.Measure($"collective_positions.wh_event_store.blocks_per_row.events_{events}",
        (double)read.Tables["wh_event_store"].Blocks / CALLS / Math.Max(found, 1), "blocks/row");
      await Assert.That(found).IsGreaterThan(0)
        .Because("the seeded tenant holds collectives, so a read that returned none measured nothing");
    }

    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());
    await _writeReportAsync("event-store-reads", rendered, report.RenderBaselineLines());

    await Assert.That(report.Breaches).IsEmpty()
      .Because($"a declared ceiling was passed. {rendered}\nNeither read has a reason to visit a row it does "
        + "not return; a sequential scan of the event store prices it by everything the service has ever stored");
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 600;
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// A settled, stamped store: half the streams local and the rest received from two origins, 50 tenants, about one
  /// event in a hundred a collective. Digest buckets folded as the emit chain folds them.
  /// </summary>
  private static async Task _fillAsync(NpgsqlConnection conn, int streams) {
    await _execAsync(conn, $"""
      ALTER TABLE wh_event_store SET (autovacuum_enabled = false);
      TRUNCATE wh_event_store, wh_stream_digests, wh_digest_epochs, wh_digest_epoch_frontiers CASCADE;
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('integrity_epoch_width', '1000', 'integer', 'perf epoch width')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value;

      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence)
      SELECT gen_random_uuid(), s.stream_id, s.stream_id, 'Perf.Collectives.LedgerAggregate',
             CASE WHEN g.seq % 97 = 0 THEN 'Perf.Collectives.LedgerAdjusted' || (g.seq % 3) || ', Perf.Collectives'
                  ELSE 'Perf.Collectives.SomethingHappened' || (g.seq % 37) || ', Perf.Collectives' END,
             jsonb_build_object('t', 'tenant-' || lpad((s.n % 50)::text, 2, '0'), 'u', 'user-' || (g.seq % 500)),
             v.version, g.seq, 0, NOW() - INTERVAL '2 hours',
             s.origin, CASE WHEN s.origin IS NULL THEN NULL ELSE g.seq END
      FROM (
        SELECT n, gen_random_uuid() AS stream_id,
               CASE n % 4 WHEN 0 THEN '0a000000-0000-0000-0000-00000000000a'::uuid
                          WHEN 1 THEN '0b000000-0000-0000-0000-00000000000b'::uuid END AS origin
        FROM generate_series(1, {streams}) n
      ) s
      CROSS JOIN LATERAL generate_series(1, {DEPTH}) v(version)
      CROSS JOIN LATERAL (SELECT (v.version - 1) * {streams} + s.n AS seq) g;

      INSERT INTO wh_stream_digests
        (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
      SELECT COALESCE(es.origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid),
             COALESCE(es.scope ->> 't', ''), es.event_type, es.stream_id,
             bit_xor(hashtextextended(es.event_id::text, 0)), bit_xor(hashtextextended(es.event_id::text, 1)),
             COUNT(*)::int, NOW()
      FROM wh_event_store es
      GROUP BY 1, 2, 3, 4;

      ANALYZE wh_event_store;
      ANALYZE wh_stream_digests;
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
