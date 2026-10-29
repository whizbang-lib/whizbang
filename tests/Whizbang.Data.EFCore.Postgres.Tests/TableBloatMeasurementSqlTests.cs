// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Issue #1807, against real Postgres with the real pgstattuple extension: the catalog estimate shortlists, and
/// <c>pgstattuple_approx</c> confirms. A queued table carries its measured ratio and how it was obtained (migration
/// 254's <c>wh_table_rewrite_measurements</c>), the queue is taken largest measured ratio first, and a request queued
/// without a measurement reads as the estimate.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/254_BloatMeasurementsRecordTheirMethod.sql</code-under-test>
/// <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
[Category("Shard4")]
public class TableBloatMeasurementSqlTests : EFCoreTestBase {
  private const string SETTINGS = "wh_settings";

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

  private Task _createPgstattupleAsync(CancellationToken cancellationToken) =>
    _executeAsync("CREATE EXTENSION IF NOT EXISTS pgstattuple", cancellationToken);

  /// <summary>
  /// A table the estimate calls bloated and the measurement calls lean: rows added after the last ANALYZE are not in
  /// the planner's row count, so the estimate divides the whole heap among the rows it last counted.
  /// </summary>
  private async Task _staleTheEstimateAsync(CancellationToken cancellationToken) {
    await _executeAsync($"ALTER TABLE {SETTINGS} SET (autovacuum_enabled = false)", cancellationToken);
    await _executeAsync($"""
      INSERT INTO {SETTINGS} (setting_key, setting_value, value_type)
      SELECT 'counted_' || g, repeat('x', 200), 'string' FROM generate_series(1, 1500) g
      """, cancellationToken);
    await _executeAsync($"ANALYZE {SETTINGS}", cancellationToken);
    await _executeAsync($"""
      INSERT INTO {SETTINGS} (setting_key, setting_value, value_type)
      SELECT 'uncounted_' || g, repeat('x', 200), 'string' FROM generate_series(1, 30000) g
      """, cancellationToken);
  }

  [Test]
  [Timeout(120000)]
  public async Task Approx_OnAGenuinelyBloatedTable_ConfirmsTheEstimateAsync(CancellationToken cancellationToken) {
    await _createPgstattupleAsync(cancellationToken);
    await TableBloat.ManufactureAsync(ConnectionString, cancellationToken);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var candidate = (await coordinator.GetTablesNeedingRewriteAsync(cancellationToken)).Single(c => c.TableName == SETTINGS);
    var approx = await coordinator.MeasureTableBloatAsync(SETTINGS, BloatMeasurementMethod.Approx, cancellationToken);

    await Assert.That(candidate.RewriteThreshold).IsEqualTo(3.0)
      .Because("the candidate carries the threshold its estimate crossed, which the measurement is held to");
    await Assert.That(approx.HasValue).IsTrue();
    await Assert.That(approx!.Value).IsGreaterThanOrEqualTo(candidate.RewriteThreshold)
      .Because("the heap holds the space of every deleted row, which reading the table confirms");
  }

  /// <summary>Locks the measured ratio on the estimate's scale, so dropped-column bytes read as bloat.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-measured-ratio-scale</docs>
  [Test]
  [Timeout(120000)]
  public async Task Approx_OnAStaleEstimate_MeasuresTheTableLeanAsync(CancellationToken cancellationToken) {
    await _createPgstattupleAsync(cancellationToken);
    await _staleTheEstimateAsync(cancellationToken);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var candidate = (await coordinator.GetTablesNeedingRewriteAsync(cancellationToken)).Single(c => c.TableName == SETTINGS);
    var approx = await coordinator.MeasureTableBloatAsync(SETTINGS, BloatMeasurementMethod.Approx, cancellationToken);

    await Assert.That(candidate.BloatRatio).IsGreaterThanOrEqualTo(candidate.RewriteThreshold)
      .Because("the estimate shortlists the table: it divides the heap among the rows the last ANALYZE counted");
    await Assert.That(candidate.Method).IsEqualTo(BloatMeasurementMethod.Estimate);
    await Assert.That(approx.HasValue).IsTrue();
    await Assert.That(approx!.Value).IsLessThan(candidate.RewriteThreshold)
      .Because("reading the table counts every row, so the measurement sees there is nothing to reclaim");
  }

  [Test]
  [Timeout(120000)]
  public async Task Approx_IsTheSameScaleAsTheEstimate_OnATableWhoseStatisticsAreCurrentAsync(CancellationToken cancellationToken) {
    await _createPgstattupleAsync(cancellationToken);
    await TableBloat.ManufactureAsync(ConnectionString, cancellationToken);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var estimate = await coordinator.MeasureTableBloatAsync(SETTINGS, BloatMeasurementMethod.Estimate, cancellationToken);
    var approx = await coordinator.MeasureTableBloatAsync(SETTINGS, BloatMeasurementMethod.Approx, cancellationToken);
    var candidate = (await coordinator.GetTablesNeedingRewriteAsync(cancellationToken)).Single(c => c.TableName == SETTINGS);

    await Assert.That(estimate).IsEqualTo(candidate.BloatRatio)
      .Because("the estimate measured on its own is the ratio the candidate scan reports");
    await Assert.That(Math.Abs(approx!.Value - estimate!.Value)).IsLessThanOrEqualTo(estimate.Value * 0.25)
      .Because("both divide the heap by live rows times the expected width; only how the rows are counted differs");
  }

  /// <summary>Locks the measured ratio on the estimate's scale, so dropped-column bytes read as bloat.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-measured-ratio-scale</docs>
  [Test]
  [Timeout(120000)]
  public async Task Approx_OnATableCarryingDroppedColumnBytes_ReadsBloated_AndItsRewriteReadsAsAnImprovementAsync(
      CancellationToken cancellationToken) {
    // Every row keeps the dropped column's 400 bytes until the table is rewritten. A ratio over the live tuples' own
    // bytes would count them as live and call the table lean; heap over live rows times the expected width does not.
    await _createPgstattupleAsync(cancellationToken);
    await _executeAsync("""
      CREATE TABLE wh_dropped_probe (id INT PRIMARY KEY, payload TEXT, filler TEXT);
      ALTER TABLE wh_dropped_probe SET (autovacuum_enabled = false);
      INSERT INTO wh_dropped_probe SELECT g, 'p', repeat('x', 400) FROM generate_series(1, 20000) g;
      ALTER TABLE wh_dropped_probe DROP COLUMN filler;
      ANALYZE wh_dropped_probe;
      """, cancellationToken);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var before = await coordinator.MeasureTableBloatAsync("wh_dropped_probe", BloatMeasurementMethod.Approx, cancellationToken);
    await _executeAsync("VACUUM FULL wh_dropped_probe", cancellationToken);
    await _executeAsync("ANALYZE wh_dropped_probe", cancellationToken);
    var after = await coordinator.MeasureTableBloatAsync("wh_dropped_probe", BloatMeasurementMethod.Approx, cancellationToken);

    await Assert.That(before.HasValue && after.HasValue).IsTrue();
    await Assert.That(before!.Value).IsGreaterThanOrEqualTo(3.0)
      .Because("the dropped column's bytes are space the table holds but cannot use, so the measurement must call it bloated");
    await Assert.That(after!.Value).IsLessThan(before.Value)
      .Because("the rewrite removes those bytes, and the re-measure has to see that or the request is never cleared");
  }

  /// <summary>Locks a failed measurement: a warning, and the estimate stands in.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-measurement-failure</docs>
  [Test]
  public async Task Approx_WithoutTheExtension_FailsRatherThanAnsweringAsync(CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();

    await Assert.That(async () =>
        await _coordinator(ctx).MeasureTableBloatAsync(SETTINGS, BloatMeasurementMethod.Approx, cancellationToken))
      .Throws<PostgresException>()
      .Because("the caller asks only while pgstattuple reads Ready, so its absence is a fault to report, not a lean table");
  }

  [Test]
  public async Task Approx_OnAFrameworkTableThatDoesNotExist_HasNoAnswerAsync(CancellationToken cancellationToken) {
    await _createPgstattupleAsync(cancellationToken);
    await using var ctx = CreateDbContext();

    var approx = await _coordinator(ctx).MeasureTableBloatAsync("wh_no_such_table", BloatMeasurementMethod.Approx, cancellationToken);

    await Assert.That(approx.HasValue).IsFalse()
      .Because("a table that is not in this schema has nothing to measure, which confirms nothing and rejects nothing");
  }

  /// <summary>Locks the exact pgstattuple scan never running.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-exact-never-runs</docs>
  [Test]
  public async Task Exact_IsNeverRunAsync(CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();

    await Assert.That(async () =>
        await _coordinator(ctx).MeasureTableBloatAsync(SETTINGS, BloatMeasurementMethod.Exact, cancellationToken))
      .Throws<NotSupportedException>()
      .Because("the exact pgstattuple scan reads the whole table and never runs automatically");
  }

  [Test]
  public async Task Measure_RefusesANameOutsideTheFrameworksTablesAsync(CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();

    await Assert.That(async () =>
        await _coordinator(ctx).MeasureTableBloatAsync("users", BloatMeasurementMethod.Approx, cancellationToken))
      .Throws<ArgumentException>();
  }

  /// <summary>Locks the queue measurement row: a queued table without one reads as the estimate.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-queue-measurements</docs>
  [Test]
  [Timeout(120000)]
  public async Task AMeasuredRequest_IsQueuedWithItsRatioAndMethod_AndTheQueueOrdersByTheMeasuredRatioAsync(
      CancellationToken cancellationToken) {
    // Every analyzed wh_ table becomes a candidate, so the queue's order can be read across real tables.
    await _executeAsync("UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'table_rewrite_bloat_threshold'", cancellationToken);
    await _executeAsync("UPDATE wh_settings SET setting_value = '1' WHERE setting_key = 'table_rewrite_min_rows'", cancellationToken);
    await _executeAsync("ANALYZE", cancellationToken);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var before = await coordinator.GetTablesNeedingRewriteAsync(cancellationToken);
    await Assert.That(before.Count).IsGreaterThanOrEqualTo(2);
    var estimated = before[0];
    var measured = before[^1];

    await coordinator.RequestTableRewriteAsync(estimated.TableName, cancellationToken);
    await coordinator.RequestMeasuredTableRewriteAsync(
      measured.TableName, new BloatMeasurement(999.5, BloatMeasurementMethod.Approx), measured.BloatRatio, cancellationToken);
    var queue = await coordinator.GetTablesNeedingRewriteAsync(cancellationToken);

    await Assert.That(queue[0].TableName).IsEqualTo(measured.TableName)
      .Because("the queue is taken largest measured ratio first, and the measurement put this table at the top");
    await Assert.That(queue[0].BloatRatio).IsEqualTo(999.5);
    await Assert.That(queue[0].Method).IsEqualTo(BloatMeasurementMethod.Approx);
    await Assert.That(queue[0].Requested).IsTrue();
    var fromEstimate = queue.Single(c => c.TableName == estimated.TableName);
    await Assert.That(fromEstimate.Method).IsEqualTo(BloatMeasurementMethod.Estimate)
      .Because("a request queued without a measurement, as every request before this version was, reads as the estimate");
    await Assert.That(fromEstimate.BloatRatio).IsEqualTo(estimated.BloatRatio);
    await Assert.That(queue.Select(c => c.BloatRatio).SequenceEqual(queue.Select(c => c.BloatRatio).OrderByDescending(r => r))).IsTrue()
      .Because("the boot step takes the queue in the order the coordinator returns it");
    await Assert.That(queue[0].RewriteThreshold).IsEqualTo(0.0);

    var row = await _scalarAsync<string>(
      $"SELECT method || ':' || bloat_ratio || ':' || estimated_ratio FROM wh_table_rewrite_measurements WHERE table_name = '{measured.TableName}'",
      cancellationToken);
    await Assert.That(row).IsEqualTo(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"approx:999.5:{(decimal)measured.BloatRatio}"))
      .Because("the queue row records the measurement, its method and the estimate it confirmed");

    await coordinator.ClearTableRewriteRequestAsync(measured.TableName, cancellationToken);
    var remaining = await _scalarAsync<long>(
      $"SELECT count(*) FROM wh_table_rewrite_measurements WHERE table_name = '{measured.TableName}'", cancellationToken);
    await Assert.That(remaining).IsEqualTo(0L)
      .Because("a cleared request takes its measurement with it, so a later request is never ordered by a stale one");
  }

  /// <summary>Locks the queue measurement row: a queued table without one reads as the estimate.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-queue-measurements</docs>
  [Test]
  public async Task AQueueRowWithoutAMethod_IsTheEstimate_AndAnUnknownMethodIsRefusedAsync(CancellationToken cancellationToken) {
    await _executeAsync("INSERT INTO wh_table_rewrite_measurements (table_name, bloat_ratio) VALUES ('wh_outbox', 4.2)", cancellationToken);

    var method = await _scalarAsync<string>(
      "SELECT method FROM wh_table_rewrite_measurements WHERE table_name = 'wh_outbox'", cancellationToken);

    await Assert.That(method).IsEqualTo(BloatMeasurementMethods.ESTIMATE);
    await Assert.That(async () => await _executeAsync(
        "INSERT INTO wh_table_rewrite_measurements (table_name, bloat_ratio, method) VALUES ('wh_inbox', 4.2, 'guess')",
        cancellationToken))
      .Throws<PostgresException>()
      .Because("a reader maps the stored name back to its method, so only the stable names may be stored");
  }
}
