using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What it costs the ephemeral reap to discover that there is nothing to reap.
/// </summary>
/// <remarks>
/// <para>
/// Every maintenance cycle asks two questions: which ephemeral pairs need a snapshot, and which
/// ephemeral bodies this cycle will destroy. Both drive the same join from <c>wh_event_body</c> into
/// <c>wh_event_store</c> and filter on the ephemeral flag plus a grace window. The questions are
/// cheap; finding the answers was not. <c>wh_event_store</c> carries seven indexes and not one keys
/// or filters on <c>flags</c>, so nothing served the predicate and the planner read both tables end
/// to end.
/// </para>
/// <para>
/// Measured on a deployed fleet, on an idle environment: 205 calls each, <strong>zero rows
/// returned</strong>, 1,405 s and 714 s of statement time, 9.6 million block reads. The answer the
/// reap gets is almost always "nothing", and that is precisely the case that scans to exhaustion.
/// This is the same defect as the outbox and perspective emptiness probes beside it, one subsystem
/// over, and much the most expensive instance of it.
/// </para>
/// <para>
/// So the seed is the production shape and nothing more convenient: events with bodies and
/// <strong>no</strong> ephemeral flag, so both queries return nothing and have to prove it. No
/// matching rows are needed to measure this, because the defect is the cost of the empty answer.
/// Autovacuum is off and nothing is vacuumed, for the reason recorded in
/// <see cref="WorkCostRecorder"/>: a vacuumed table is all-visible and measures a plan shape a live
/// store never has.
/// </para>
/// <para>
/// The measure is taken TWICE against the same seed, without the index and with it, so the report
/// carries its own before and after rather than relying on a number recorded on another machine. It
/// calls the coordinator's real methods; a test that issued its own copy of the two statements would
/// measure a query nothing ships.
/// </para>
/// </remarks>
[Category("Benchmark")]
[Category("Performance")]
public class EphemeralReapCostScenarioTests : EFCoreTestBase {
  /// <summary>Events seeded, each with a body. None ephemeral: the empty-answer case.</summary>
  private const int EVENTS = 20_000;
  /// <summary>Reap cycles measured, so the per-cycle figure is not one sample.</summary>
  private const int CYCLES = 4;
  /// <summary>The index migration 163 adds, dropped and recreated to measure both sides.</summary>
  private const string INDEX = "idx_event_store_ephemeral_reap";

  private static readonly string[] MEASURED_TABLES = ["wh_event_store", "wh_event_body"];

  [Test]
  [Timeout(1800000)]
  public async Task EphemeralReapCost_NothingToReap_AnswersFromAnIndexAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonSerializerOptions.Default);
    var recorder = new WorkCostRecorder(ConnectionString);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Ephemeral reap with nothing to reap");

    await _seedNonEphemeralEventsAsync(conn);

    // The index exists because the migration created it. Drop it to measure the plan this migration
    // replaces, then put it back and measure again.
    await _setIndexAsync(conn, present: false);
    var without = await recorder.MeasureAsync(conn, MEASURED_TABLES, async () => {
      for (var i = 0; i < CYCLES; i++) {
        await _reapCycleAsync(coordinator, cancellationToken);
      }
    });

    await _setIndexAsync(conn, present: true);
    var with = await recorder.MeasureAsync(conn, MEASURED_TABLES, async () => {
      for (var i = 0; i < CYCLES; i++) {
        await _reapCycleAsync(coordinator, cancellationToken);
      }
    });

    foreach (var table in MEASURED_TABLES) {
      report.Measure($"ephemeral_reap.no_index.{table}.tuples_read_per_cycle",
        (double)without.Tables[table].SequentialTuplesRead / CYCLES, "tuples/cycle");
      report.Measure($"ephemeral_reap.indexed.{table}.tuples_read_per_cycle",
        (double)with.Tables[table].SequentialTuplesRead / CYCLES, "tuples/cycle");
    }
    var beforeTotal = MEASURED_TABLES.Sum(t => without.Tables[t].SequentialTuplesRead);
    var afterTotal = MEASURED_TABLES.Sum(t => with.Tables[t].SequentialTuplesRead);
    report.Measure("ephemeral_reap.tuples_read_per_cycle_total.no_index",
      (double)beforeTotal / CYCLES, "tuples/cycle");
    report.Measure("ephemeral_reap.tuples_read_per_cycle_total.indexed",
      (double)afterTotal / CYCLES, "tuples/cycle");

    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());
    await _writeReportAsync("ephemeral-reap", rendered, report.RenderBaselineLines());

    // The scenario has to have been the expensive one, or the comparison below is between two cheap
    // numbers and would report a win the deployed fleet never had.
    await Assert.That(beforeTotal).IsGreaterThan(EVENTS)
      .Because("without an index on the flag, each cycle has to read the seeded events to prove "
        + "none of them is reapable; a scenario that read less than one table has not reproduced "
        + "the plan this migration exists to replace");
    // A ratio, not an absolute: page and tuple counts are properties of the plan, wall clock is a
    // property of the runner.
    await Assert.That(afterTotal * 10).IsLessThan(beforeTotal)
      .Because($"the indexed plan read {afterTotal} tuples against {beforeTotal} without it. The "
        + "empty answer is the common one, and an index on the ephemeral flag answers it without "
        + "touching the event store or the body table");
    await Assert.That(report.Breaches).IsEmpty()
      .Because($"a declared ceiling was passed. {rendered}");
  }

  /// <summary>
  /// Both reap questions, as a maintenance cycle asks them. The real coordinator methods, so the
  /// measure is of the statements that ship.
  /// </summary>
  private static async Task _reapCycleAsync(
      EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, CancellationToken cancellationToken) {
    var pairs = await coordinator.GetEphemeralPairsNeedingSnapshotAsync(cancellationToken);
    var bodies = await coordinator.GetEphemeralBodiesAboutToReapAsync(cancellationToken);
    // Both must be empty: this scenario seeds nothing reapable, and a non-empty answer would mean
    // the seed drifted into measuring a different question.
    if (pairs.Count != 0 || bodies.Count != 0) {
      throw new InvalidOperationException(
        $"the seed is meant to hold nothing reapable, got {pairs.Count} pairs and {bodies.Count} bodies");
    }
  }

  /// <summary>
  /// Events with bodies and no ephemeral flag, aged well past any grace window: the shape that makes
  /// the reap prove emptiness rather than stop at a first match.
  /// </summary>
  private static async Task _seedNonEphemeralEventsAsync(NpgsqlConnection conn) {
    await using var seed = conn.CreateCommand();
    seed.CommandText = @"
      ALTER TABLE wh_event_store SET (autovacuum_enabled = false);
      ALTER TABLE wh_event_body SET (autovacuum_enabled = false);

      WITH ev AS (
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, version,
           commit_sequence, created_at, flags)
        SELECT gen_random_uuid(),
               ('00000000-0000-0000-0000-' || lpad((s % 200)::text, 12, '0'))::uuid,
               ('00000000-0000-0000-0000-' || lpad((s % 200)::text, 12, '0'))::uuid,
               'TestAggregate', 'TestEvent', s,
               s, NOW() - INTERVAL '30 days',
               0                      -- deliberately NOT ephemeral (EventFlags.Ephemeral = 8)
        FROM generate_series(1, @events) s
        RETURNING event_id
      )
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      SELECT event_id, jsonb_build_object('pad', repeat('x', 200)), '{}'::jsonb FROM ev;

      ANALYZE wh_event_store; ANALYZE wh_event_body;";
    seed.Parameters.AddWithValue("events", EVENTS);
    seed.CommandTimeout = 900;
    await seed.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Drops or recreates the migration's index, so one run reports both plans. The definition is
  /// repeated from migration 163 on purpose and must stay in step with it; the assertion that the
  /// indexed side is an order of magnitude cheaper is what would fail if it drifted.
  /// </summary>
  private static async Task _setIndexAsync(NpgsqlConnection conn, bool present) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = present
      ? $@"CREATE INDEX IF NOT EXISTS {INDEX}
             ON wh_event_store (created_at, event_id)
             INCLUDE (stream_id, event_type, commit_sequence)
             WHERE (flags & 8) = 8;
           ANALYZE wh_event_store;"
      : $"DROP INDEX IF EXISTS {INDEX}; ANALYZE wh_event_store;";
    cmd.CommandTimeout = 300;
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
