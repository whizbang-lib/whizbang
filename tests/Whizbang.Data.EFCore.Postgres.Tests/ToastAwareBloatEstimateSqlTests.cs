// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2319, against real Postgres: the bloat estimate measures a table's TOAST relation as well as its heap, and weighs
/// each column's width by how often it is present, so the online rewrite is offered the tables where the reclaimable
/// bytes actually are. Before it, a table with a lean heap and a mostly dead TOAST relation was never a candidate, and a
/// mostly-null wide column made the heap look denser than it was.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/430_BloatIsMeasuredWithItsToast.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/TableMaintenanceSql.cs</code-under-test>
/// <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
[Category("Shard5")]
public class ToastAwareBloatEstimateSqlTests : EFCoreTestBase {
  private const string TOASTED = "wh_toast_probe";
  private const double THRESHOLD = 3.0;

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private async Task _executeAsync(string sql, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task<T?> _scalarAsync<T>(string sql, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var value = await cmd.ExecuteScalarAsync(cancellationToken);
    return value is T typed ? typed : default;
  }

  /// <summary>Removes the rows a statement just made dead, once no snapshot can still see them, and refreshes the statistics.</summary>
  private async Task _vacuumOnceRemovableAsync(string table, CancellationToken cancellationToken) {
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync(cancellationToken);
      await TableBloat.WaitUntilTheDeletedRowsAreRemovableAsync(conn, cancellationToken);
    }
    await _executeAsync($"VACUUM ANALYZE {table}", cancellationToken);
  }

  /// <summary>
  /// 2,000 rows, each holding a 4,000-byte value stored out of line and uncompressed (three TOAST chunks), with
  /// autovacuum off on the table and its TOAST relation, so the test owns both.
  /// </summary>
  private Task _createToastedAsync(int rows, CancellationToken cancellationToken) =>
    _executeAsync($"""
      CREATE TABLE {TOASTED} (id INT PRIMARY KEY, body TEXT);
      ALTER TABLE {TOASTED} ALTER COLUMN body SET STORAGE EXTERNAL;
      ALTER TABLE {TOASTED} SET (autovacuum_enabled = false, toast.autovacuum_enabled = false);
      INSERT INTO {TOASTED} SELECT g, repeat('x', 4000) FROM generate_series(1, {rows}) g;
      """, cancellationToken);

  /// <summary>The heap-only estimate the scan used before #2319: heap bytes over rows times the summed pg_stats width.</summary>
  private Task<decimal> _heapOnlyRatioAsync(string table, CancellationToken cancellationToken) =>
    _scalarAsync<decimal>($"""
      SELECT round((pg_relation_size(c.oid)::NUMERIC / c.reltuples::NUMERIC)
                   / GREATEST((SELECT COALESCE(sum(s.avg_width), 0) + 28 FROM pg_stats s
                               WHERE s.schemaname = 'public' AND s.tablename = c.relname), 1), 2)
      FROM pg_class c WHERE c.oid = '{table}'::regclass
      """, cancellationToken);

  [Test]
  [Timeout(120000)]
  public async Task AMostlyDeadToast_UnderALeanHeap_IsACandidateAsync(CancellationToken cancellationToken) {
    await _createToastedAsync(2000, cancellationToken);
    // Nine values in ten are replaced by a short inline one: their chunks are dead, and VACUUM leaves the TOAST
    // relation its size (the live chunks are spread through it) while counting the chunks that remain.
    await _executeAsync($"UPDATE {TOASTED} SET body = 'short' WHERE id % 10 <> 0", cancellationToken);
    await _vacuumOnceRemovableAsync(TOASTED, cancellationToken);
    await using var ctx = CreateDbContext();

    var candidates = await _coordinator(ctx).GetTablesNeedingRewriteAsync(cancellationToken);

    await Assert.That(await _heapOnlyRatioAsync(TOASTED, cancellationToken)).IsLessThan((decimal)THRESHOLD)
      .Because("the heap alone does not read bloated, which is why the heap-only estimate never offered this table");
    var candidate = candidates.Where(c => c.TableName == TOASTED).Cast<Whizbang.Core.Messaging.TableRewriteCandidate?>().SingleOrDefault();
    await Assert.That(candidate.HasValue).IsTrue()
      .Because("nine in ten of the TOAST relation's chunks are dead, and that is space only a rewrite returns");
    await Assert.That(candidate!.Value.BloatRatio).IsGreaterThanOrEqualTo(THRESHOLD);
    await Assert.That(candidate!.Value.SizeBytes).IsEqualTo(await _scalarAsync<long>($"SELECT pg_table_size('{TOASTED}')", cancellationToken))
      .Because("an online rewrite copies the TOAST relation too, so the byte budget counts it");
  }

  [Test]
  [Timeout(120000)]
  public async Task AHealthyToastedTable_IsNotACandidate_AndItsEstimateReadsLeanAsync(CancellationToken cancellationToken) {
    await _createToastedAsync(2000, cancellationToken);
    await _executeAsync($"VACUUM ANALYZE {TOASTED}", cancellationToken);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var candidates = await coordinator.GetTablesNeedingRewriteAsync(cancellationToken);
    var estimate = await coordinator.MeasureTableBloatAsync(TOASTED, BloatMeasurementMethod.Estimate, cancellationToken);
    var direct = await _scalarAsync<decimal>($"SELECT wh_estimate_table_bloat('{TOASTED}')", cancellationToken);

    await Assert.That(candidates.Any(c => c.TableName == TOASTED)).IsFalse();
    await Assert.That(estimate!.Value).IsLessThan(1.5)
      .Because("densely packed chunks occupy a quarter page each, so a live TOAST relation reads at or below one");
    await Assert.That((decimal)estimate.Value).IsEqualTo(direct)
      .Because("the re-measure after a rewrite is the scan's own estimate, so the two can be compared");
  }

  [Test]
  [Timeout(120000)]
  public async Task AnUntoastedTableWithoutNulls_ReadsAsTheHeapOnlyEstimateDidAsync(CancellationToken cancellationToken) {
    await TableBloat.ManufactureAsync(ConnectionString, cancellationToken);
    await _executeAsync("""
      CREATE TABLE wh_plain_probe (id INT PRIMARY KEY, payload TEXT NOT NULL);
      ALTER TABLE wh_plain_probe SET (autovacuum_enabled = false);
      INSERT INTO wh_plain_probe SELECT g, repeat('p', 200) FROM generate_series(1, 20000) g;
      DELETE FROM wh_plain_probe WHERE id % 5 <> 0;
      """, cancellationToken);
    await _vacuumOnceRemovableAsync("wh_plain_probe", cancellationToken);

    var estimate = await _scalarAsync<decimal>("SELECT wh_estimate_table_bloat('wh_plain_probe')", cancellationToken);

    await Assert.That(estimate).IsEqualTo(await _heapOnlyRatioAsync("wh_plain_probe", cancellationToken))
      .Because("with no TOAST bytes and no nulls, the estimate is the heap-only one it was before");
  }

  [Test]
  [Timeout(120000)]
  public async Task AMostlyNullWideColumn_DoesNotHideHeapBloatAsync(CancellationToken cancellationToken) {
    // pg_stats.avg_width averages a column over the rows where it is present, so a 1,500-byte column set on one row in
    // twenty (on the rows kept as on the rows deleted) counted 1,500 bytes against every row and made a bloated heap read dense.
    await _executeAsync("""
      CREATE TABLE wh_sparse_probe (id INT PRIMARY KEY, payload TEXT NOT NULL, extra TEXT);
      ALTER TABLE wh_sparse_probe ALTER COLUMN extra SET STORAGE EXTERNAL;
      ALTER TABLE wh_sparse_probe SET (autovacuum_enabled = false);
      INSERT INTO wh_sparse_probe
        SELECT g, repeat('p', 100), CASE WHEN (g / 4) % 20 = 0 THEN repeat('e', 1500) END FROM generate_series(1, 40000) g;
      DELETE FROM wh_sparse_probe WHERE id % 4 <> 0;
      """, cancellationToken);
    await _vacuumOnceRemovableAsync("wh_sparse_probe", cancellationToken);
    await using var ctx = CreateDbContext();

    var candidate = (await _coordinator(ctx).GetTablesNeedingRewriteAsync(cancellationToken))
      .Where(c => c.TableName == "wh_sparse_probe").Cast<Whizbang.Core.Messaging.TableRewriteCandidate?>().SingleOrDefault();

    await Assert.That(await _heapOnlyRatioAsync("wh_sparse_probe", cancellationToken)).IsLessThan((decimal)THRESHOLD)
      .Because("the unweighted width hides three dead rows in four");
    await Assert.That(candidate.HasValue).IsTrue()
      .Because("weighing each column by how often it is present reads the heap as the quarter-full file it is");
    await Assert.That(candidate!.Value.BloatRatio).IsGreaterThanOrEqualTo(THRESHOLD);
  }

  [Test]
  [Timeout(120000)]
  public async Task AToastNeverVacuumed_IsNotMeasured_ForItsLiveChunksAreUnknownAsync(CancellationToken cancellationToken) {
    await _createToastedAsync(2000, cancellationToken);
    await _executeAsync($"UPDATE {TOASTED} SET body = 'short' WHERE id % 10 <> 0", cancellationToken);
    // ANALYZE counts the table's rows but never visits its TOAST relation, whose live chunk count stays unknown (-1).
    await _executeAsync($"ANALYZE {TOASTED}", cancellationToken);

    var toastTuples = await _scalarAsync<float>(
      $"SELECT t.reltuples FROM pg_class c JOIN pg_class t ON t.oid = c.reltoastrelid WHERE c.oid = '{TOASTED}'::regclass",
      cancellationToken);
    var estimate = await _scalarAsync<decimal>($"SELECT wh_estimate_table_bloat('{TOASTED}')", cancellationToken);

    await Assert.That(toastTuples).IsEqualTo(-1f);
    await Assert.That(estimate).IsEqualTo(await _heapOnlyRatioAsync(TOASTED, cancellationToken))
      .Because("an unknown chunk count is no measure, so the heap alone decides; it is never read as zero live chunks");
  }

  [Test]
  [Timeout(120000)]
  public async Task ASmallDeadToast_IsMeasuredAgainstTheMinimumRows_SoItIsNotOfferedAsync(CancellationToken cancellationToken) {
    // Sixty large values on 2,000 rows, the first fifty replaced: about 150 dead chunks before 30 live ones (the live
    // ones last, so VACUUM cannot truncate the dead pages away). Against 30 chunks that reads several times bloated,
    // for a rewrite that returns a few hundred kilobytes.
    await _executeAsync($"""
      CREATE TABLE {TOASTED} (id INT PRIMARY KEY, body TEXT);
      ALTER TABLE {TOASTED} ALTER COLUMN body SET STORAGE EXTERNAL;
      ALTER TABLE {TOASTED} SET (autovacuum_enabled = false, toast.autovacuum_enabled = false);
      INSERT INTO {TOASTED} SELECT g, CASE WHEN g <= 50 OR g > 1990 THEN repeat('x', 4000) ELSE 's' END FROM generate_series(1, 2000) g;
      UPDATE {TOASTED} SET body = 's' WHERE id <= 50;
      """, cancellationToken);
    await _vacuumOnceRemovableAsync(TOASTED, cancellationToken);

    var toastBytes = await _scalarAsync<long>(
      $"SELECT pg_relation_size(c.reltoastrelid) FROM pg_class c WHERE c.oid = '{TOASTED}'::regclass", cancellationToken);
    var estimate = await _scalarAsync<decimal>($"SELECT wh_estimate_table_bloat('{TOASTED}')", cancellationToken);

    await Assert.That(toastBytes).IsGreaterThan(8192L * 3).Because("the dead chunks still hold their pages");
    await Assert.That(estimate).IsLessThan((decimal)THRESHOLD)
      .Because("a TOAST relation is measured against at least table_rewrite_min_rows chunks, as a heap below that many rows is not measured");
  }

  [Test]
  [Timeout(120000)]
  public async Task TheReMeasure_ReadsTheSameEstimate_WithOrWithoutANamedSchemaAsync(CancellationToken cancellationToken) {
    await _createToastedAsync(2000, cancellationToken);
    await _executeAsync($"UPDATE {TOASTED} SET body = 'short' WHERE id % 10 <> 0", cancellationToken);
    await _vacuumOnceRemovableAsync(TOASTED, cancellationToken);
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    var named = await TableMaintenanceSql.MeasureRatioAsync(conn, TOASTED, "public", cancellationToken);
    var searchPath = await TableMaintenanceSql.MeasureRatioAsync(conn, TOASTED, schema: null, cancellationToken);

    await Assert.That(named!.Value).IsGreaterThanOrEqualTo(THRESHOLD)
      .Because("the re-measure in a named schema calls that schema's estimate, which reads the mostly dead TOAST relation");
    await Assert.That(searchPath).IsEqualTo(named)
      .Because("with no schema the re-measure resolves the same function through search_path, so both forms agree");
  }

  [Test]
  [Timeout(120000)]
  public async Task Approx_MeasuresTheToastRelationToo_SoItConfirmsTheEstimateAsync(CancellationToken cancellationToken) {
    await _executeAsync("CREATE EXTENSION IF NOT EXISTS pgstattuple", cancellationToken);
    await _createToastedAsync(2000, cancellationToken);
    await _executeAsync($"UPDATE {TOASTED} SET body = 'short' WHERE id % 10 <> 0", cancellationToken);
    await _vacuumOnceRemovableAsync(TOASTED, cancellationToken);
    await using var ctx = CreateDbContext();

    var approx = await _coordinator(ctx).MeasureTableBloatAsync(TOASTED, BloatMeasurementMethod.Approx, cancellationToken);

    await Assert.That(approx!.Value).IsGreaterThanOrEqualTo(THRESHOLD)
      .Because("while pgstattuple is Ready its measurement decides whether the table is queued, so it must read the TOAST relation too");
  }

  [Test]
  [Timeout(120000)]
  public async Task TheFillFactorQueue_OrdersByTheSameEstimateAsync(CancellationToken cancellationToken) {
    await _createToastedAsync(2000, cancellationToken);
    await _executeAsync($"UPDATE {TOASTED} SET body = 'short' WHERE id % 10 <> 0", cancellationToken);
    await _vacuumOnceRemovableAsync(TOASTED, cancellationToken);
    await _executeAsync($"SELECT wh_request_fillfactor_rewrite('{TOASTED}', 100, 90)", cancellationToken);

    var owed = await _scalarAsync<decimal>(
      $"SELECT bloat_ratio FROM wh_tables_owed_fillfactor_rewrite() WHERE table_name = '{TOASTED}'", cancellationToken);

    await Assert.That(owed).IsEqualTo(await _scalarAsync<decimal>($"SELECT wh_estimate_table_bloat('{TOASTED}')", cancellationToken))
      .Because("every queue reads one estimate, so a table is ordered by the same ratio whichever rule offered it");
  }

  /// <summary>430's reverse restores the heap-only estimate in every function and drops the shared one.</summary>
  [Test]
  [Timeout(120000)]
  public async Task TheReverse_RestoresTheHeapOnlyEstimate_AndDropsTheSharedOneAsync(CancellationToken cancellationToken) {
    await _createToastedAsync(2000, cancellationToken);
    await _executeAsync($"UPDATE {TOASTED} SET body = 'short' WHERE id % 10 <> 0", cancellationToken);
    await _vacuumOnceRemovableAsync(TOASTED, cancellationToken);
    var down = typeof(Whizbang.Data.Postgres.PostgresMigrationProvider).Assembly.GetManifestResourceStream(
      "Whizbang.Data.Postgres.MigrationReversals.430_BloatIsMeasuredWithItsToast.down.sql")
      ?? throw new InvalidOperationException("430 ships its reverse");
    using var reader = new StreamReader(down);
    await _executeAsync(Whizbang.Data.Postgres.MigrationConstants.Apply(
      (await reader.ReadToEndAsync(cancellationToken)).Replace("__SCHEMA__", "public", StringComparison.Ordinal)), cancellationToken);

    var offered = await _scalarAsync<long>(
      $"SELECT count(*) FROM wh_tables_needing_rewrite() WHERE table_name = '{TOASTED}'", cancellationToken);
    var shared = await _scalarAsync<long>(
      "SELECT count(*) FROM pg_proc WHERE proname IN ('wh_estimate_table_bloat', 'wh_expected_row_bytes', 'wh_toast_bloat_ratio')",
      cancellationToken);
    var bodies = await _scalarAsync<long>(
      """
      SELECT count(*) FROM pg_proc
      WHERE proname IN ('wh_tables_needing_rewrite', 'wh_tables_owed_fillfactor_rewrite', 'wh_measure_table_bloat_approx')
        AND (prosrc LIKE '%wh_estimate_table_bloat%' OR prosrc LIKE '%reltoastrelid%' OR prosrc LIKE '%null_frac%')
      """, cancellationToken);

    await Assert.That(offered).IsEqualTo(0L).Because("the restored scan measures the heap alone, which is lean here");
    await Assert.That(shared).IsEqualTo(0L).Because("the functions 430 added are dropped");
    await Assert.That(bodies).IsEqualTo(0L).Because("the three redefined functions are back on their earlier bodies");
  }
}
