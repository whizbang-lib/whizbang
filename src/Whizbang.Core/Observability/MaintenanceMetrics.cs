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
/// </list>
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/MaintenanceMetricsTests.cs</tests>
public sealed class MaintenanceMetrics {
#pragma warning disable CA1707
  /// <summary>OpenTelemetry meter name.</summary>
  public const string METER_NAME = "Whizbang.Maintenance";
#pragma warning restore CA1707

  /// <summary>Rows affected by a maintenance task in one cycle. Tagged by <c>task</c>.</summary>
  public Counter<long> RowsAffected { get; }

  /// <summary>A maintenance task's per-cycle duration in milliseconds. Tagged by <c>task</c>.</summary>
  public Histogram<double> TaskDuration { get; }

  /// <summary>Initializes a new instance of <see cref="MaintenanceMetrics"/>.</summary>
  public MaintenanceMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    RowsAffected = meter.CreateCounter<long>(
      "whizbang.maintenance.rows_affected",
      description: "Rows affected by a maintenance task in one cycle; tagged by task");
    TaskDuration = meter.CreateHistogram<double>(
      "whizbang.maintenance.task_duration",
      unit: "ms",
      description: "A maintenance task's per-cycle duration in milliseconds; tagged by task");
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
