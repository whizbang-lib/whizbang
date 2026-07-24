using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// OTel meters for Whizbang infrastructure table statistics.
/// Uses ObservableGauge with cached values refreshed by <see cref="TableStatisticsCollector"/>.
/// Meter name: Whizbang.TableStatistics
/// </summary>
/// <docs>operations/observability/metrics#table-statistics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsMetricsTests.cs</tests>
public sealed class TableStatisticsMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.TableStatistics";
#pragma warning restore CA1707

  private readonly ConcurrentDictionary<string, long> _tableSizes = new();
  private readonly ConcurrentDictionary<string, long> _queueDepths = new();

  /// <summary>Initializes table statistics meters on the shared Whizbang meter.</summary>
  public TableStatisticsMetrics(WhizbangMetrics whizbangMetrics) {
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    meter.CreateObservableGauge(
      "whizbang.table.estimated_bytes",
      observeValues: () => _tableSizes.Select(kv =>
        new Measurement<long>(kv.Value, new KeyValuePair<string, object?>("table_name", kv.Key))),
      unit: "bytes",
      description: "Estimated disk size per table from database catalog");

    meter.CreateObservableGauge(
      "whizbang.queue.estimated_depth",
      observeValues: () => _queueDepths.Select(kv =>
        new Measurement<long>(kv.Value, new KeyValuePair<string, object?>("queue_name", kv.Key))),
      unit: "messages",
      description: "Unprocessed message count for inbox/outbox queues");
  }

  /// <summary>
  /// Updates the cached table sizes. Called by <see cref="TableStatisticsCollector"/>.
  /// </summary>
  public void UpdateTableSizes(IReadOnlyDictionary<string, long> sizes) {
    foreach (var (table, size) in sizes) {
      _tableSizes[table] = size;
    }
  }

  /// <summary>
  /// Updates the cached queue depths. Called by <see cref="TableStatisticsCollector"/>.
  /// </summary>
  public void UpdateQueueDepths(IReadOnlyDictionary<string, long> depths) {
    foreach (var (queue, depth) in depths) {
      _queueDepths[queue] = depth;
    }
  }
}
