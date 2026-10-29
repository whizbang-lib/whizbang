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

  private const string TABLE_NAME_TAG = "table_name";
  private const string METHOD_TAG = "method";
  private const string DATABASE_TAG = "database";
  private const string EVENT_TYPE_TAG = "event_type";
  private const string BYTES_UNIT = "bytes";

  private readonly ConcurrentDictionary<string, long> _tableSizes = new();
  private readonly ConcurrentDictionary<string, long> _queueDepths = new();
  private readonly ConcurrentDictionary<string, double> _tableBloat = new();
  private readonly ConcurrentDictionary<string, BloatMeasurement> _measuredBloat = new();
  private readonly ConcurrentDictionary<string, TableStorageStatistics> _tableStorage = new();
  private readonly ConcurrentDictionary<string, EventTypeBytes> _eventTypeBytes = new();
  private readonly ConcurrentDictionary<string, UnusedIndex> _unusedIndexes = new();

  /// <summary>The database the readings come from, read when a gauge is observed; null until it is known.</summary>
  private volatile string? _database;

  /// <summary>Initializes table statistics meters on the shared Whizbang meter.</summary>
  public TableStatisticsMetrics(WhizbangMetrics whizbangMetrics) {
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);

    meter.CreateObservableGauge(
      "whizbang.table.estimated_bytes",
      observeValues: () => _tableSizes.Select(kv => _measure(kv.Value, _tag(TABLE_NAME_TAG, kv.Key))),
      unit: BYTES_UNIT,
      description: "Estimated disk size per table from database catalog");

    meter.CreateObservableGauge(
      "whizbang.queue.estimated_depth",
      observeValues: () => _queueDepths.Select(kv => _measure(kv.Value, _tag("queue_name", kv.Key))),
      unit: "messages",
      description: "Unprocessed message count for inbox/outbox queues");

    // How many times larger the heap is than the live rows need. Storage that a table occupies
    // but does not use costs on every read: index heap-fetches pull emptier pages and the
    // buffer cache holds fewer useful rows, so a table can be slow purely because of its shape.
    // Dead tuples awaiting vacuum are the familiar cause; a dropped column is the invisible one,
    // because Postgres leaves that column's bytes in every pre-existing row forever and only a
    // table rewrite removes them. Around 1.0 is healthy; a sustained large multiple means the
    // table needs a rewrite, not more vacuuming. The method tag says how each value was obtained: every table's
    // catalog estimate is "estimate", and a table the maintenance cycle measured with pgstattuple_approx is a second
    // series, "approx", so a guess and a measurement are never one line.
    meter.CreateObservableGauge(
      "whizbang.table.bloat_ratio",
      observeValues: () => _tableBloat
        .Select(kv => _measure(kv.Value, _tag(TABLE_NAME_TAG, kv.Key), _tag(METHOD_TAG, BloatMeasurementMethods.ESTIMATE)))
        .Concat(_measuredBloat.Select(kv =>
          _measure(kv.Value.Ratio, _tag(TABLE_NAME_TAG, kv.Key), _tag(METHOD_TAG, kv.Value.Method.ToValue())))),
      unit: "ratio",
      description: "Heap bytes per live row divided by the expected row width, 1.0 is lean; the method tag says whether it was estimated or measured");

    // What the size is made of. A table whose bytes are in TOAST holds large documents, one whose
    // bytes are in indexes is over-indexed, and only the split tells the two apart from a table that
    // is simply long. The three components add up to estimated_bytes, which keeps meaning the total.
    meter.CreateObservableGauge(
      "whizbang.table.component_bytes",
      observeValues: () => _tableStorage.SelectMany(kv => _components(kv.Key, kv.Value)),
      unit: BYTES_UNIT,
      description: "Disk size per table split into heap, TOAST and indexes; the three add up to estimated_bytes");

    // Rows, from the planner's statistics. With bytes it is bytes per row, and both over time are growth.
    meter.CreateObservableGauge(
      "whizbang.table.live_rows",
      observeValues: () => _tableStorage.Select(kv => _measure(kv.Value.LiveRows, _tag(TABLE_NAME_TAG, kv.Key))),
      unit: "rows",
      description: "Estimated live rows per table from the planner statistics");

    // Indexes nothing has scanned since the statistics were last reset, with what each costs on disk. A report only:
    // an unused index may serve a rare path, so nothing is dropped on the strength of it.
    meter.CreateObservableGauge(
      "whizbang.table.unused_index_bytes",
      observeValues: () => _unusedIndexes.Select(kv =>
        _measure(kv.Value.Bytes, _tag(TABLE_NAME_TAG, kv.Value.TableName), _tag("index_name", kv.Key))),
      unit: BYTES_UNIT,
      description: "Size of each index on a framework table that nothing has scanned since the statistics were last reset");

    // Which event types the event store's bytes belong to, and how much of each event is payload and how much
    // metadata: the reading that decides what to exclude from audit, mark ephemeral or slim. Sampled on one
    // instance per window, so at most one instance reports it at a time.
    meter.CreateObservableGauge(
      "whizbang.event_store.sampled_bytes_per_event",
      observeValues: () => _eventTypeBytes.SelectMany(kv => _parts(kv.Key, kv.Value)),
      unit: BYTES_UNIT,
      description: "Mean stored bytes per event of each event type, split into payload and metadata, from a sample");

    meter.CreateObservableGauge(
      "whizbang.event_store.estimated_type_bytes",
      observeValues: () => _eventTypeBytes.Select(kv => _measure(kv.Value.EstimatedTotalBytes, _tag(EVENT_TYPE_TAG, kv.Key))),
      unit: BYTES_UNIT,
      description: "Stored bytes of every event of each event type, extrapolated from a sample");
  }

  /// <summary>
  /// A measurement with <paramref name="tags"/> and, once the database is known, the <c>database</c> tag after them.
  /// </summary>
  /// <remarks>
  /// The database is read here, when the gauge is observed, rather than when a value is written, so a value another
  /// worker wrote before the collector learned the database is still reported under it.
  /// </remarks>
  private Measurement<T> _measure<T>(T value, params ReadOnlySpan<KeyValuePair<string, object?>> tags) where T : struct {
    var database = _database;
    if (database is null) {
      return new Measurement<T>(value, tags);
    }
    var tagged = new KeyValuePair<string, object?>[tags.Length + 1];
    tags.CopyTo(tagged);
    tagged[^1] = new(DATABASE_TAG, database);
    return new Measurement<T>(value, tagged);
  }

  private static KeyValuePair<string, object?> _tag(string key, object? value) => new(key, value);

  private IEnumerable<Measurement<double>> _parts(string eventType, EventTypeBytes bytes) {
    yield return _part(bytes.MeanDataBytes, eventType, "data");
    yield return _part(bytes.MeanMetadataBytes, eventType, "metadata");
  }

  private Measurement<double> _part(double bytes, string eventType, string part) =>
    _measure(bytes, _tag(EVENT_TYPE_TAG, eventType), _tag("part", part));

  private IEnumerable<Measurement<long>> _components(string table, TableStorageStatistics storage) {
    yield return _component(storage.HeapBytes, table, "heap");
    yield return _component(storage.ToastBytes, table, "toast");
    yield return _component(storage.IndexBytes, table, "indexes");
  }

  private Measurement<long> _component(long bytes, string table, string component) =>
    _measure(bytes, _tag(TABLE_NAME_TAG, table), _tag("component", component));

  /// <summary>
  /// Sets the database every gauge on this meter is tagged with. Called by <see cref="TableStatisticsCollector"/>.
  /// </summary>
  /// <param name="database">The database the readings come from; null reports the gauges without the tag.</param>
  /// <remarks>
  /// Services on different databases report the same tables, so the tag is what keeps one database's series from
  /// overwriting another's. It applies to every gauge from its next observation, values written earlier included.
  /// </remarks>
  /// <docs>operations/observability/metrics#table-statistics-database</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsMetricsDatabaseTests.cs</tests>
  public void UpdateDatabase(string? database) => _database = database;

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
  /// Replaces the measured bloat ratios, each reported on <c>whizbang.table.bloat_ratio</c> beside the table's estimate
  /// and tagged with its method. Called by the maintenance cycle, which measures the tables the estimate shortlists.
  /// </summary>
  /// <param name="measurements">The latest measurement per table.</param>
  /// <remarks>A table missing from <paramref name="measurements"/> stops reporting a measured value.</remarks>
  /// <docs>operations/observability/metrics#table-statistics</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsMetricsBloatMethodTests.cs</tests>
  public void UpdateMeasuredTableBloat(IReadOnlyDictionary<string, BloatMeasurement> measurements) =>
    _replace(_measuredBloat, measurements);

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
  /// Updates the cached unused indexes. Called by <see cref="TableStatisticsCollector"/>.
  /// </summary>
  /// <param name="indexes">The latest reading, by index name.</param>
  /// <remarks>An index missing from <paramref name="indexes"/> (scanned since, or dropped) stops reporting.</remarks>
  /// <docs>operations/observability/metrics#unused-indexes</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsMetricsUnusedIndexTests.cs</tests>
  public void UpdateUnusedIndexes(IReadOnlyDictionary<string, UnusedIndex> indexes) => _replace(_unusedIndexes, indexes);

  /// <summary>
  /// Test seam: reads back a published ratio. Observable gauges are only sampled by an active
  /// meter listener, so asserting on the cache is the direct way to prove the value was fed.
  /// </summary>
  /// <summary>Replaces the event-type sample; an empty one clears it.</summary>
  /// <param name="sample">The latest sample per event type.</param>
  public void UpdateEventTypeBytes(IReadOnlyDictionary<string, EventTypeBytes> sample) => _replace(_eventTypeBytes, sample);

  internal double? GetBloatRatioForTest(string table) =>
    _tableBloat.TryGetValue(table, out var v) ? v : null;

  internal BloatMeasurement? GetMeasuredBloatForTest(string table) =>
    _measuredBloat.TryGetValue(table, out var v) ? v : null;
}
