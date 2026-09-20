using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What the maintenance cycle's row-expiry reap costs when nothing is expired, priced by how it
/// finds its tables.
/// </summary>
/// <remarks>
/// <para>
/// Every perspective table carries an <c>expires_at</c> column, because the column comes from the
/// shared table template rather than from the perspective declaring a row lifetime. Finding tables
/// by asking the catalog for that column therefore selects every perspective table, and each takes
/// an unindexed delete. The predicate matches nothing -- a perspective that declares no lifetime
/// never stamps the column -- but not matching still reads every row of every table.
/// </para>
/// <para>
/// Both discovery shapes are measured against one fixture so the pair reads as a before and after,
/// the same way the ephemeral reap scenario drops and recreates its index. The BEFORE runs the
/// catalog sweep as the migration used to issue it; the AFTER runs the shipped
/// <c>perform_maintenance</c>. Only <c>wh_per_*</c> tables are measured, so the cycle's other twelve
/// tasks do not enter the number.
/// </para>
/// <para>
/// Measured on a deployed service before the change: 120 tables scanned for the 3 that declare a
/// lifetime, 14,758 ms to delete zero rows -- 98 percent of the whole maintenance cycle, which was
/// itself the heaviest statement on that database by total time. Two other services scanned 81
/// tables for one declaring perspective and 32 for nine.
/// </para>
/// <para>
/// The gate is the number of perspective tables SEQUENTIALLY SCANNED per cycle, because that is a
/// property of the design rather than of the machine: the reap has no reason to read a table whose
/// perspective never declared a lifetime, whatever the planner or the hardware does. Tuples read is
/// recorded alongside as the same finding counted a second way.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/maintenance</docs>
[Category("Benchmark")]
[Category("Performance")]
public class MaintenanceTtlReapCostScenarioTests : EFCoreTestBase {
  /// <summary>Perspective tables seeded. A deployed service carried 120.</summary>
  private const int TABLES = 24;
  /// <summary>Rows per table, none expired: the empty-answer case the reap actually meets.</summary>
  private const int ROWS_PER_TABLE = 500;
  /// <summary>Of those tables, how many declare a row lifetime. A deployed service had 3 of 120.</summary>
  private const int DECLARING = 2;
  /// <summary>Cycles measured, so the per-cycle figure is not one sample.</summary>
  private const int CYCLES = 3;

  private static string _table(int i) => $"wh_per_ttlcost_{i:D2}";

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 120;
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  [Timeout(1800000)]
  public async Task TtlReapCost_NothingExpired_TouchesOnlyDeclaringPerspectivesAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }

    var measured = Enumerable.Range(0, TABLES).Select(_table).ToArray();
    for (var i = 0; i < TABLES; i++) {
      var t = _table(i);
      await _execAsync(conn, $@"CREATE TABLE IF NOT EXISTS {t} (
        id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        expires_at TIMESTAMPTZ, version INTEGER NOT NULL)");
      await _execAsync(conn, $"TRUNCATE {t}");
      // Nothing expired: expires_at is NULL on a perspective that declares no lifetime, which is
      // exactly the case the sweep spends all its time proving empty.
      await _execAsync(conn, $@"
        INSERT INTO {t} (id, data, metadata, scope, created_at, updated_at, expires_at, version)
        SELECT gen_random_uuid(), '{{}}', '{{}}', '{{}}', NOW(), NOW(), NULL, 1
        FROM generate_series(1, {ROWS_PER_TABLE})");
      // Only the first DECLARING tables declare a lifetime, as the schema pass would record it.
      if (i < DECLARING) {
        await _execAsync(conn, $@"
          INSERT INTO wh_perspective_registry
            (id, clr_type_name, table_name, schema_json, schema_hash, service_name,
             created_at, updated_at, row_retention_enrolled, row_ttl_seconds)
          VALUES (gen_random_uuid(), 'Test.{t}', '{t}', '{{}}', 'test', 'tests',
                  NOW(), NOW(), TRUE, 300)
          ON CONFLICT DO NOTHING");
      }
    }
    await _execAsync(conn, "ANALYZE");

    var recorder = new WorkCostRecorder(ConnectionString);
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Maintenance TTL reap, nothing expired");

    // BEFORE: the catalog sweep, issued as the migration used to issue it.
    var sweep = await recorder.MeasureAsync(conn, measured, async () => {
      for (var c = 0; c < CYCLES; c++) {
        await _execAsync(conn, @"
          DO $$
          DECLARE t TEXT;
          BEGIN
            FOR t IN SELECT table_name FROM information_schema.columns
                     WHERE table_schema = current_schema()
                       AND column_name = 'expires_at'
                       AND table_name LIKE 'wh\_per\_ttlcost\_%'
            LOOP
              EXECUTE format('DELETE FROM %I.%I WHERE expires_at IS NOT NULL AND expires_at < NOW()',
                             current_schema(), t);
            END LOOP;
          END $$;");
      }
    });

    // AFTER: the shipped maintenance cycle, which reads the registry.
    var registry = await recorder.MeasureAsync(conn, measured, async () => {
      for (var c = 0; c < CYCLES; c++) {
        await _execAsync(conn, "SELECT * FROM perform_maintenance()");
      }
    });

    double ScannedTables(WorkCostRecorder.Window w) =>
      (double)measured.Count(t => w.Tables[t].SequentialScans > 0);
    double Tuples(WorkCostRecorder.Window w) =>
      (double)measured.Sum(t => w.Tables[t].SequentialTuplesRead) / CYCLES;

    report.Measure("maintenance.ttl_reap.catalog_sweep.tables_scanned_per_cycle",
      ScannedTables(sweep), "tables/cycle");
    report.Measure("maintenance.ttl_reap.registry.tables_scanned_per_cycle",
      ScannedTables(registry), "tables/cycle");
    report.Measure("maintenance.ttl_reap.catalog_sweep.tuples_read_per_cycle",
      Tuples(sweep), "tuples/cycle");
    report.Measure("maintenance.ttl_reap.registry.tuples_read_per_cycle",
      Tuples(registry), "tuples/cycle");

    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());
    await _writeReportAsync("maintenance-ttl-reap", rendered, report.RenderBaselineLines());

    // The sweep must actually be expensive here, or the comparison below is measuring nothing.
    await Assert.That(ScannedTables(sweep)).IsGreaterThan((double)DECLARING)
      .Because("the catalog sweep selects every table carrying an expires_at column, so the fixture "
        + "has to show it touching more than the two that declare a lifetime. If it does not, the "
        + "fixture is not reproducing the shape this scenario prices.");

    await Assert.That(ScannedTables(registry)).IsLessThanOrEqualTo((double)DECLARING)
      .Because("the reap reads the registry, so it may touch only the perspectives that declare a "
        + "row lifetime. Reading a table whose perspective never declared one is the defect, "
        + "whatever it costs on this machine.");

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
