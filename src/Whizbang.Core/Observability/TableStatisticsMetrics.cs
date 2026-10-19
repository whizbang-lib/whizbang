// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  private readonly ConcurrentDictionary<string, TableStorageStatistics> _tableStorage = new();

  /// <summary>Initializes table statistics meters on the shared Whizbang meter.</summary>
  public TableStatisticsMetrics(WhizbangMetrics whizbangMetrics) {
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);

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

    // What the size is made of. A table whose bytes are in TOAST holds large documents, one whose
    // bytes are in indexes is over-indexed, and only the split tells the two apart from a table that
    // is simply long. The three components add up to estimated_bytes, which keeps meaning the total.
    meter.CreateObservableGauge(
      "whizbang.table.component_bytes",
      observeValues: () => _tableStorage.SelectMany(kv => _components(kv.Key, kv.Value)),
      unit: "bytes",
      description: "Disk size per table split into heap, TOAST and indexes; the three add up to estimated_bytes");

    // Rows, from the planner's statistics. With bytes it is bytes per row, and both over time are growth.
    meter.CreateObservableGauge(
      "whizbang.table.live_rows",
      observeValues: () => _tableStorage.Select(kv =>
        new Measurement<long>(kv.Value.LiveRows, new KeyValuePair<string, object?>("table_name", kv.Key))),
      unit: "rows",
      description: "Estimated live rows per table from the planner statistics");
  }

  private static IEnumerable<Measurement<long>> _components(string table, TableStorageStatistics storage) {
    yield return _component(storage.HeapBytes, table, "heap");
    yield return _component(storage.ToastBytes, table, "toast");
    yield return _component(storage.IndexBytes, table, "indexes");
  }

  private static Measurement<long> _component(long bytes, string table, string component) =>
    new(bytes,
      new KeyValuePair<string, object?>("table_name", table),
      new KeyValuePair<string, object?>("component", component));

  /// <summary>
  /// Makes <paramref name="cache"/> hold exactly <paramref name="latest"/>: a table that is no longer
  /// reported (dropped, or summed under the cap's <c>other</c>) stops reporting instead of keeping its
  /// last value, which every sum over the series would otherwise count twice.
  /// </summary>
  private static void _replace<T>(ConcurrentDictionary<string, T> cache, IReadOnlyDictionary<string, T> latest) {
    foreach (var (table, value) in latest) {
      cache[table] = value;
    }
    foreach (var table in cache.Keys.Where(table => !latest.ContainsKey(table))) {
      cache.TryRemove(table, out _);
    }
  }

  /// <summary>
  /// Updates the cached table sizes. Called by <see cref="TableStatisticsCollector"/>.
  /// </summary>
  /// <remarks>A table missing from <paramref name="sizes"/> stops reporting.</remarks>
  public void UpdateTableSizes(IReadOnlyDictionary<string, long> sizes) => _replace(_tableSizes, sizes);

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
  /// <remarks>A table missing from <paramref name="ratios"/> stops reporting.</remarks>
  public void UpdateTableBloat(IReadOnlyDictionary<string, double> ratios) => _replace(_tableBloat, ratios);

  /// <summary>
  /// Updates the cached per-table storage split and live row counts. Called by
  /// <see cref="TableStatisticsCollector"/>.
  /// </summary>
  /// <param name="storage">The latest reading, per table.</param>
  /// <remarks>A table missing from <paramref name="storage"/> stops reporting.</remarks>
  /// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsMetricsStorageTests.cs</tests>
  public void UpdateTableStorage(IReadOnlyDictionary<string, TableStorageStatistics> storage) =>
    _replace(_tableStorage, storage);

  /// <summary>
  /// Test seam: reads back a published ratio. Observable gauges are only sampled by an active
  /// meter listener, so asserting on the cache is the direct way to prove the value was fed.
  /// </summary>
  internal double? GetBloatRatioForTest(string table) =>
    _tableBloat.TryGetValue(table, out var v) ? v : null;
}
