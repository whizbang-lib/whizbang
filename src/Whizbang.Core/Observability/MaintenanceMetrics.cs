// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for the maintenance cycle's per-task outcomes. Meter name: <c>Whizbang.Maintenance</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>perform_maintenance</c> returns one result row per task (dedup sweep, ephemeral body reap,
/// expired perspective-row reap, …). These instruments make those outcomes queryable and
/// alertable across the fleet instead of only visible in per-pod logs — the primary consumer is
/// perspective row retention, where <c>rows_affected</c> tagged
/// <c>task=reap_expired_perspective_rows</c> is the "is retention working" signal and a
/// sustained-zero (with expired rows accumulating) means the maintenance cadence is losing to
/// churn:
/// </para>
/// <list type="bullet">
///   <item><description><b>RowsAffected</b> — rows deleted/processed by a task in one cycle.
///   Tagged by <c>task</c>.</description></item>
///   <item><description><b>TaskDuration</b> — a task's per-cycle wall time (ms). Tagged by
///   <c>task</c>; watches reap cost as tables scale.</description></item>
///   <item><description><b>whizbang.maintenance.pending_rows</b> and
///   <b>whizbang.ephemeral.history_walk_remaining</b> (#2289): what cleanup has left to do, so a dashboard can tell
///   "nothing left to clean" from "cleanup is falling behind". See
///   <see cref="UpdatePendingReclamation(PendingReclamation)"/>.</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/MaintenanceMetricsTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Observability/MaintenanceMetricsPendingTests.cs</tests>
public sealed class MaintenanceMetrics {
#pragma warning disable CA1707
  /// <summary>OpenTelemetry meter name.</summary>
  public const string METER_NAME = "Whizbang.Maintenance";
#pragma warning restore CA1707

  /// <summary>Rows affected by a maintenance task in one cycle. Tagged by <c>task</c>.</summary>
  public PassiveCounter<long> RowsAffected { get; }

  /// <summary>A maintenance task's per-cycle duration in milliseconds. Tagged by <c>task</c>.</summary>
  public Histogram<double> TaskDuration { get; }

  /// <summary>
  /// Rows past a declared retention window at the moment the maintenance cycle adopted it (opened
  /// the adoption gate). Tagged by <c>perspective</c>; the first day of a retroactive window.
  /// </summary>
  public PassiveCounter<long> RetentionAdopted { get; }

  /// <summary>
  /// Operational records the retention sweep removed (#2354). Tagged by <c>category</c> only. Exported to Prometheus as
  /// <c>whizbang_maintenance_operational_rows_expired_total</c>.
  /// </summary>
  /// <docs>operations/infrastructure/operational-retention#metrics</docs>
  public PassiveCounter<long> OperationalRowsExpired { get; }

  // The latest operational retention reading (#2354), replaced whole by each one.
  private IReadOnlyList<Whizbang.Core.Workers.OperationalRetentionSweepResult> _operational = [];

  // The latest pending-reclamation reading (#2289), replaced whole by each one.
  private PendingReclamation _pending = PendingReclamation.None;

  /// <summary>Initializes a new instance of <see cref="MaintenanceMetrics"/>.</summary>
  public MaintenanceMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);

    RowsAffected = meter.CreatePassiveCounter<long>(
      "whizbang.maintenance.rows_affected",
      description: "Rows affected by a maintenance task in one cycle; tagged by task");
    TaskDuration = meter.CreateHistogram<double>(
      "whizbang.maintenance.task_duration",
      unit: "ms",
      description: "A maintenance task's per-cycle duration in milliseconds; tagged by task");
    RetentionAdopted = meter.CreatePassiveCounter<long>(
      "whizbang.maintenance.retention_adopted",
      description: "Rows past the window when the maintenance cycle adopted a perspective's declared retention; tagged by perspective");
    OperationalRowsExpired = meter.CreatePassiveCounter<long>(
      "whizbang.maintenance.operational_rows_expired",
      description: "Operational records the retention sweep removed past their resting period; tagged by category");
    OperationalRowsExpired.Touch(CATEGORY_TAG, Enum.GetNames<Whizbang.Core.Workers.OperationalCategory>());
    meter.CreateObservableGauge(
      "whizbang.maintenance.operational_rows",
      _observeOperationalRows,
      description: "Operational records left after the last retention sweep (open rows, which are state, excluded); tagged by category");
    meter.CreateObservableGauge(
      "whizbang.maintenance.operational_oldest_age_seconds",
      _observeOperationalOldestAge,
      unit: "s",
      description: "Age of the oldest operational record left after the last retention sweep; tagged by category. Above twice the category's retention, the sweep is not keeping up");
    // #2289: what cleanup has left to do, read by PendingReclamationSampler on its own window.
    meter.CreateObservableGauge(
      "whizbang.maintenance.pending_rows",
      _observePendingRows,
      description: "Rows a maintenance task would remove now; tagged by task, and capped=true when the count stopped at the cap (at least that many)");
    meter.CreateObservableGauge(
      "whizbang.ephemeral.history_walk_remaining",
      _observeHistoryWalkRemaining,
      description: "Events an incomplete ephemeral history walk has yet to visit; tagged by event_type");
  }

  /// <summary>
  /// Records one adoption: the backlog the newly opened window will drain, tagged by the
  /// perspective's CLR type name. A zero backlog still records, so the series marks the adoption.
  /// </summary>
  /// <param name="clrTypeName">The perspective model's CLR type name.</param>
  /// <param name="backlog">Rows past the window at adoption time.</param>
  public void RecordRetentionAdopted(string clrTypeName, long backlog) =>
    RetentionAdopted.Add(Math.Max(0, backlog), new KeyValuePair<string, object?>("perspective", clrTypeName));

  /// <summary>Replaces the pending-reclamation reading the gauges report (#2289).</summary>
  /// <param name="reading">The reading; <see cref="PendingReclamation.None"/> reports nothing.</param>
  /// <remarks>
  /// Replaced whole, never merged: a task the latest reading did not measure stops reporting rather than repeating a
  /// stale count, and an instance that did not take the window reports nothing.
  /// </remarks>
  /// <docs>operations/observability/metrics#pending-reclamation</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/MaintenanceMetricsPendingTests.cs:AReading_ReplacesThePreviousOne_SoAnUnmeasuredTaskStopsReportingAsync</tests>
  public void UpdatePendingReclamation(PendingReclamation reading) {
    ArgumentNullException.ThrowIfNull(reading);
    Volatile.Write(ref _pending, reading);
  }

  private const string CATEGORY_TAG = "category";

  /// <summary>Records the rows one retention batch removed from a category's table (#2354).</summary>
  /// <param name="category">The table's category.</param>
  /// <param name="expired">Rows removed.</param>
  /// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/MaintenanceWorkerOperationalRetentionTests.cs:TheCycle_ReportsRowsRemovedAndLeft_AndTheOldestAge_PerCategoryAsync</tests>
  public void RecordOperationalRowsExpired(Whizbang.Core.Workers.OperationalCategory category, long expired) {
    if (expired > 0) {
      OperationalRowsExpired.Add(expired, new KeyValuePair<string, object?>(CATEGORY_TAG, category.ToString()));
    }
  }

  /// <summary>
  /// Replaces the operational retention reading the per-category gauges report (#2354): a category's rows are the sum
  /// over its tables, and its oldest age the oldest table's.
  /// </summary>
  /// <param name="reading">The last batch's results.</param>
  /// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/MaintenanceWorkerOperationalRetentionTests.cs:TheCycle_ReportsRowsRemovedAndLeft_AndTheOldestAge_PerCategoryAsync</tests>
  public void UpdateOperationalRetention(IReadOnlyList<Whizbang.Core.Workers.OperationalRetentionSweepResult> reading) {
    ArgumentNullException.ThrowIfNull(reading);
    Volatile.Write(ref _operational, reading);
  }

  private IEnumerable<Measurement<long>> _observeOperationalRows() =>
    Volatile.Read(ref _operational)
      .GroupBy(r => r.Category)
      .Select(g => new Measurement<long>(g.Sum(r => r.Remaining), new KeyValuePair<string, object?>(CATEGORY_TAG, g.Key.ToString())));

  private IEnumerable<Measurement<double>> _observeOperationalOldestAge() =>
    Volatile.Read(ref _operational)
      .Where(r => r.OldestAge.HasValue)
      .GroupBy(r => r.Category)
      .Select(g => new Measurement<double>(
        g.Max(r => r.OldestAge!.Value.TotalSeconds), new KeyValuePair<string, object?>(CATEGORY_TAG, g.Key.ToString())));

  private IEnumerable<Measurement<long>> _observePendingRows() {
    foreach (var (task, rows) in Volatile.Read(ref _pending).Tasks) {
      yield return new Measurement<long>(
        rows.Rows, new KeyValuePair<string, object?>("task", task), new KeyValuePair<string, object?>("capped", rows.Capped));
    }
  }

  private IEnumerable<Measurement<long>> _observeHistoryWalkRemaining() {
    foreach (var (eventType, remaining) in Volatile.Read(ref _pending).HistoryWalkRemaining) {
      yield return new Measurement<long>(remaining, new KeyValuePair<string, object?>("event_type", eventType));
    }
  }

  /// <summary>Records one task's cycle outcome. Duration always records; rows only when &gt; 0.</summary>
  /// <param name="taskName">The maintenance task name (e.g. <c>reap_expired_perspective_rows</c>).</param>
  /// <param name="rowsAffected">Rows the task affected this cycle.</param>
  /// <param name="durationMs">The task's wall time this cycle, in milliseconds.</param>
  public void Record(string taskName, long rowsAffected, double durationMs) {
    var tag = new KeyValuePair<string, object?>("task", taskName);
    TaskDuration.Record(durationMs, tag);
    if (rowsAffected > 0) {
      RowsAffected.Add(rowsAffected, tag);
    }
  }
}
