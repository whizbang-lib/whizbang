// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
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
  private const string PART_TAG = "part";
  private const string BYTES_UNIT = "bytes";
  private const string TOOL_TAG = "tool";
  private const string HEAP_PART = "heap";
  private const string TOAST_PART = "toast";
  private const string TABLE_PART = "table";

  /// <summary>The <c>state</c> tag values of <c>whizbang.table.rewrite.queue</c>, in report order.</summary>
  internal static readonly string[] RewriteQueueStates = ["requested", "candidate", "held_by_floor", "over_budget"];

  private readonly ConcurrentDictionary<string, long> _tableSizes = new();
  private readonly ConcurrentDictionary<string, long> _queueDepths = new();
  private readonly ConcurrentDictionary<string, double> _tableBloat = new();
  private readonly ConcurrentDictionary<string, BloatMeasurement> _measuredBloat = new();
  private readonly ConcurrentDictionary<string, TableStorageStatistics> _tableStorage = new();
  private readonly ConcurrentDictionary<string, EventTypeBytes> _eventTypeBytes = new();
  private readonly ConcurrentDictionary<string, UnusedIndex> _unusedIndexes = new();
  private readonly ConcurrentDictionary<string, double> _rewriteFloors = new();
  private readonly ConcurrentDictionary<string, long> _rewriteQueue = new(StringComparer.Ordinal);
  private readonly PassiveCounter<long> _rewrites;
  private readonly PassiveCounter<long> _reclaimedBytes;
  private readonly Histogram<double> _rewriteDuration;

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
    // series, "approx", so a guess and a measurement are never one line. The part tag (#2319) says which relation the
    // estimate measured: the heap, or the TOAST relation large values live in, whose dead chunks the heap's ratio never
    // showed; a measurement is of the whole table (the worse of the two), "table".
    meter.CreateObservableGauge(
      "whizbang.table.bloat_ratio",
      observeValues: () => _tableBloat
        .Select(kv => _measure(kv.Value, _tag(TABLE_NAME_TAG, kv.Key), _tag(METHOD_TAG, BloatMeasurementMethods.ESTIMATE),
          _tag(PART_TAG, HEAP_PART)))
        .Concat(_tableStorage
          .Where(kv => kv.Value.ToastBloatRatio.HasValue)
          .Select(kv => _measure(kv.Value.ToastBloatRatio.GetValueOrDefault(), _tag(TABLE_NAME_TAG, kv.Key),
            _tag(METHOD_TAG, BloatMeasurementMethods.ESTIMATE), _tag(PART_TAG, TOAST_PART))))
        .Concat(_measuredBloat.Select(kv =>
          _measure(kv.Value.Ratio, _tag(TABLE_NAME_TAG, kv.Key), _tag(METHOD_TAG, kv.Value.Method.ToValue()),
            _tag(PART_TAG, TABLE_PART)))),
      unit: "ratio",
      description: "Bytes per live row divided by what the rows need, 1.0 is lean, for the heap and the TOAST relation (part); the method tag says whether it was estimated or measured");

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

    // Table rewrites (#2319): every attempt, from the online step (pg_squeeze, pg_repack, blue-green) and the boot step
    // (VACUUM FULL), what each returned, how long it took, what the queue looked like, where each table's last rewrite
    // left it, and what a rewrite would return now.
    _rewrites = meter.CreatePassiveCounter<long>(
      "whizbang.table.rewrites",
      unit: "{rewrite}",
      description: "Attempted table rewrites, by table, tool, reason and outcome (rewritten, no_gain, failed)");

    _reclaimedBytes = meter.CreatePassiveCounter<long>(
      "whizbang.table.rewrite.reclaimed_bytes",
      unit: BYTES_UNIT,
      description: "Bytes a completed rewrite returned: the table's whole size (heap, TOAST and indexes) before less after, at least 0");

    _rewriteDuration = meter.CreateHistogram<double>(
      "whizbang.table.rewrite.duration",
      unit: "ms",
      description: "How long each attempted table rewrite took, by table, tool and outcome");

    meter.CreateObservableGauge(
      "whizbang.table.rewrite.queue",
      observeValues: () => RewriteQueueStates
        .Where(_rewriteQueue.ContainsKey)
        .Select(state => _measure(_rewriteQueue[state], _tag("state", state))),
      unit: "{table}",
      description: "The latest online rewrite cycle's queue: requested, candidate, held_by_floor (held back by the post-rewrite floor) and over_budget");

    meter.CreateObservableGauge(
      "whizbang.table.rewrite.floor",
      observeValues: () => _rewriteFloors.Select(kv => _measure(kv.Value, _tag(TABLE_NAME_TAG, kv.Key))),
      unit: "ratio",
      description: "The bloat ratio each table's latest recorded rewrite left it at, the floor the post-rewrite guard measures regrowth from");

    meter.CreateObservableGauge(
      "whizbang.table.reclaimable_bytes",
      observeValues: () => _tableStorage.SelectMany(kv => _reclaimable(kv.Key, kv.Value)),
      unit: BYTES_UNIT,
      description: "Bytes a rewrite would free now, by table and part (heap, toast), from the TOAST-aware bloat estimate");

    // Which event types the event store's bytes belong to, and where each event's bytes sit: payload and metadata in the
    // body table, the pointer row every event keeps in the event store, and the metadata the outbox carries inline.
    // That is the reading that decides what to exclude from audit, mark ephemeral or slim. Sampled on one instance per
    // window, so at most one instance reports it at a time.
    meter.CreateObservableGauge(
      "whizbang.event_store.sampled_bytes_per_event",
      observeValues: () => _eventTypeBytes.SelectMany(kv => _sampledParts(kv.Value)
        .Select(part => _measure(part.Bytes.MeanBytes, _tag(EVENT_TYPE_TAG, kv.Key), _tag(PART_TAG, part.Name)))),
      unit: BYTES_UNIT,
      description: "Mean stored bytes per row of each event type, by part: payload, metadata, event-store pointer row and outbox metadata, from a sample");

    meter.CreateObservableGauge(
      "whizbang.event_store.estimated_type_bytes",
      observeValues: () => _eventTypeBytes.SelectMany(kv => _sampledParts(kv.Value)
        .Select(part => _measure(part.Bytes.EstimatedTotalBytes, _tag(EVENT_TYPE_TAG, kv.Key), _tag(PART_TAG, part.Name)))),
      unit: BYTES_UNIT,
      description: "Stored bytes of every event of each event type, by part, extrapolated from a sample");
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

  /// <summary>
  /// The parts of <paramref name="bytes"/> the sample read rows of. A part with none has no mean, and a zero would claim a
  /// measurement that was not made, so it is left out.
  /// </summary>
  private static IEnumerable<(string Name, EventPartBytes Bytes)> _sampledParts(EventTypeBytes bytes) {
    (string Name, EventPartBytes Bytes)[] parts = [
      (EventStoreBytesParts.DATA, bytes.Data),
      (EventStoreBytesParts.METADATA, bytes.Metadata),
      (EventStoreBytesParts.POINTER_ROW, bytes.PointerRow),
      (EventStoreBytesParts.OUTBOX_METADATA, bytes.OutboxMetadata),
    ];
    return parts.Where(part => part.Bytes.SampledRows > 0);
  }

  private IEnumerable<Measurement<long>> _reclaimable(string table, TableStorageStatistics storage) {
    if (storage.ReclaimableHeapBytes is long heap) {
      yield return _measure(heap, _tag(TABLE_NAME_TAG, table), _tag(PART_TAG, HEAP_PART));
    }
    if (storage.ReclaimableToastBytes is long toast) {
      yield return _measure(toast, _tag(TABLE_NAME_TAG, table), _tag(PART_TAG, TOAST_PART));
    }
  }

  /// <summary>The <c>tool</c> tag for a rewriter strategy: pg_squeeze, pg_repack, vacuum_full or blue_green.</summary>
  /// <param name="strategy">The <see cref="Whizbang.Core.Workers.ITableRewriter.Strategy"/>.</param>
  /// <returns>The tag value; a strategy the library does not name is reported as it is, lower-cased.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Observability/TableRewriteMetricsTests.cs:EachStrategy_IsTaggedByItsToolAsync</tests>
  public static string ToolOf(string strategy) => strategy switch {
    Whizbang.Core.Workers.TableRewriteStrategies.PG_SQUEEZE => "pg_squeeze",
    Whizbang.Core.Workers.TableRewriteStrategies.PG_REPACK => "pg_repack",
    Whizbang.Core.Workers.TableRewriteStrategies.VACUUM_FULL => "vacuum_full",
    Whizbang.Core.Workers.TableRewriteStrategies.BLUE_GREEN => "blue_green",
    _ => strategy.ToLowerInvariant(),
  };

  /// <summary>The bytes a rewrite returned: <paramref name="before"/> less <paramref name="after"/>, at least 0; null unless both were read.</summary>
  /// <param name="before">The table's whole size before the rewrite.</param>
  /// <param name="after">Its whole size after.</param>
  /// <returns>The reclaimed bytes, or null.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Observability/TableRewriteMetricsTests.cs:Reclaimed_IsTheDropClampedAtZero_AndNeedsBothSizesAsync</tests>
  public static long? Reclaimed(long? before, long? after) =>
    before is long b && after is long a ? Math.Max(b - a, 0) : null;

  /// <summary>
  /// Records one attempted rewrite (#2319): a count by table, tool, reason and outcome; its duration by table, tool and
  /// outcome; and, for one that ended rewritten with both sizes read, the bytes it returned by table and tool.
  /// </summary>
  /// <param name="attempt">The attempt.</param>
  /// <docs>operations/observability/metrics#table-rewrites</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/TableRewriteMetricsTests.cs</tests>
  public void RecordRewrite(TableRewriteAttempt attempt) {
    ArgumentNullException.ThrowIfNull(attempt);
    var tool = ToolOf(attempt.Strategy);
    var outcome = _outcomeTag(attempt.Outcome);
    var reason = attempt.Reason == Whizbang.Core.Messaging.TableRewriteReason.FillFactorDecrease ? "fillfactor" : "bloat";
    _rewrites.Add(1, _tags(_tag(TABLE_NAME_TAG, attempt.TableName), _tag(TOOL_TAG, tool), _tag("reason", reason), _tag("outcome", outcome)));
    _rewriteDuration.Record(attempt.Duration.TotalMilliseconds,
      _tags(_tag(TABLE_NAME_TAG, attempt.TableName), _tag(TOOL_TAG, tool), _tag("outcome", outcome)));
    if (attempt.Outcome == TableRewriteMetricOutcome.Rewritten && attempt.ReclaimedBytes is long reclaimed) {
      _reclaimedBytes.Add(reclaimed, _tags(_tag(TABLE_NAME_TAG, attempt.TableName), _tag(TOOL_TAG, tool)));
    }
  }

  private static string _outcomeTag(TableRewriteMetricOutcome outcome) => outcome switch {
    TableRewriteMetricOutcome.Rewritten => "rewritten",
    TableRewriteMetricOutcome.NoGain => "no_gain",
    _ => "failed",
  };

  /// <summary>The tags of a recorded measurement, with the <c>database</c> tag once it is known, as the gauges carry it.</summary>
  private TagList _tags(params ReadOnlySpan<KeyValuePair<string, object?>> tags) {
    var list = new TagList(tags);
    if (_database is { } database) {
      list.Add(DATABASE_TAG, database);
    }
    return list;
  }

  /// <summary>
  /// Replaces the online rewrite cycle's queue reading (#2319). Called by the online rewrite step once per cycle.
  /// </summary>
  /// <param name="requested">The tables in the rewrite queue.</param>
  /// <param name="candidates">The queued tables offered for a rewrite this cycle.</param>
  /// <param name="heldByFloor">The tables at or above the threshold held back by their post-rewrite floor.</param>
  /// <param name="overBudget">The offered tables passed over because they did not fit the cycle's byte budget.</param>
  /// <docs>operations/observability/metrics#table-rewrites</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/TableRewriteMetricsTests.cs:TheQueueGauge_ReportsEachStateAsync</tests>
  public void UpdateRewriteQueue(int requested, int candidates, int heldByFloor, int overBudget) {
    _rewriteQueue[RewriteQueueStates[0]] = requested;
    _rewriteQueue[RewriteQueueStates[1]] = candidates;
    _rewriteQueue[RewriteQueueStates[2]] = heldByFloor;
    _rewriteQueue[RewriteQueueStates[3]] = overBudget;
  }

  /// <summary>Replaces the post-rewrite floors. Called by <see cref="TableStatisticsCollector"/>.</summary>
  /// <param name="floors">The ratio each table's latest rewrite left it at.</param>
  /// <remarks>A table missing from <paramref name="floors"/> stops reporting.</remarks>
  /// <docs>operations/observability/metrics#table-rewrites</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/TableRewriteMetricsTests.cs:TheFloorGauge_ReportsTheLatestFloors_AndDropsAStaleTableAsync</tests>
  public void UpdateRewriteFloors(IReadOnlyDictionary<string, double> floors) => _replace(_rewriteFloors, floors);

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

  /// <summary>Replaces the event-type sample; an empty one clears it.</summary>
  /// <param name="sample">The latest sample per event type, by part.</param>
  /// <remarks>A part with no sampled rows is not reported on either event-type gauge.</remarks>
  /// <docs>operations/observability/metrics#event-type-bytes-parts</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsMetricsEventTypeTests.cs</tests>
  public void UpdateEventTypeBytes(IReadOnlyDictionary<string, EventTypeBytes> sample) => _replace(_eventTypeBytes, sample);

  /// <summary>
  /// Test seam: reads back a published ratio. Observable gauges are only sampled by an active
  /// meter listener, so asserting on the cache is the direct way to prove the value was fed.
  /// </summary>
  internal double? GetBloatRatioForTest(string table) =>
    _tableBloat.TryGetValue(table, out var v) ? v : null;

  internal double? GetRewriteFloorForTest(string table) =>
    _rewriteFloors.TryGetValue(table, out var v) ? v : null;

  internal BloatMeasurement? GetMeasuredBloatForTest(string table) =>
    _measuredBloat.TryGetValue(table, out var v) ? v : null;
}
