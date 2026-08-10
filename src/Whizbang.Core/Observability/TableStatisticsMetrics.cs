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
  private readonly ConcurrentDictionary<string, double> _tableBloat = new();

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

    // How many times larger the heap is than the live rows need. Storage that a table occupies
    // but does not use costs on every read: index heap-fetches pull emptier pages and the
    // buffer cache holds fewer useful rows, so a table can be slow purely because of its shape.
    // Dead tuples awaiting vacuum are the familiar cause; a dropped column is the invisible one,
    // because Postgres leaves that column's bytes in every pre-existing row forever and only a
    // table rewrite removes them. Around 1.0 is healthy; a sustained large multiple means the
    // table needs a rewrite, not more vacuuming.
    meter.CreateObservableGauge(
      "whizbang.table.bloat_ratio",
      observeValues: () => _tableBloat.Select(kv =>
        new Measurement<double>(kv.Value, new KeyValuePair<string, object?>("table_name", kv.Key))),
      unit: "ratio",
      description: "Heap bytes per live row divided by the expected row width — 1.0 is lean");
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

  /// <summary>
  /// Updates the cached per-table bloat ratios. Called by <see cref="TableStatisticsCollector"/>.
  /// </summary>
  public void UpdateTableBloat(IReadOnlyDictionary<string, double> ratios) {
    foreach (var (table, ratio) in ratios) {
      _tableBloat[table] = ratio;
    }
  }

  /// <summary>
  /// Test seam: reads back a published ratio. Observable gauges are only sampled by an active
  /// meter listener, so asserting on the cache is the direct way to prove the value was fed.
  /// </summary>
  internal double? GetBloatRatioForTest(string table) =>
    _tableBloat.TryGetValue(table, out var v) ? v : null;
}
