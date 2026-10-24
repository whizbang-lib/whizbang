// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Issue #1717: every gauge on the table-statistics meter carries the database it was read from, so a host on several
/// databases reports a separate series per database instead of one series the databases overwrite.
/// </summary>
/// <remarks>
/// Each test reads the gauges the way an exporter does, through a listener on this test's own meter.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsMetrics.cs</code-under-test>
/// <docs>operations/observability/metrics#table-statistics</docs>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsMetricsDatabaseTests {
  private const string DATABASE = "orders_db";

  private static readonly string[] _gauges = [
    "whizbang.table.estimated_bytes",
    "whizbang.queue.estimated_depth",
    "whizbang.table.bloat_ratio",
    "whizbang.table.component_bytes",
    "whizbang.table.live_rows",
    "whizbang.table.unused_index_bytes",
    "whizbang.event_store.sampled_bytes_per_event",
    "whizbang.event_store.estimated_type_bytes",
  ];

  [Test]
  public async Task OnceTheDatabaseIsKnown_EveryGaugeCarriesItAsync() {
    using var gauges = new Gauges();
    gauges.Metrics.UpdateDatabase(DATABASE);
    _fillEveryGauge(gauges.Metrics);

    var readings = gauges.Read();

    await Assert.That(readings.Select(r => r.Name).Distinct().Order(StringComparer.Ordinal))
      .IsEquivalentTo(_gauges.Order(StringComparer.Ordinal))
      .Because("every gauge on the meter is read, so none can be missed by the assertion below");
    await Assert.That(readings.All(r => r.Database == DATABASE)).IsTrue()
      .Because("a series without its database is overwritten by the same table in another database");
  }

  [Test]
  public async Task TheDatabaseIsReadWhenTheGaugeIsObserved_SoALaterNameReachesEarlierReadingsAsync() {
    using var gauges = new Gauges();
    _fillEveryGauge(gauges.Metrics);

    gauges.Metrics.UpdateDatabase(DATABASE);
    var readings = gauges.Read();

    await Assert.That(readings.All(r => r.Database == DATABASE)).IsTrue()
      .Because("the event-type sample is written by another worker, which may write before the database is known");
  }

  [Test]
  public async Task WhileNoDatabaseIsKnown_NoGaugeCarriesTheTagAsync() {
    using var gauges = new Gauges();
    _fillEveryGauge(gauges.Metrics);

    var readings = gauges.Read();

    await Assert.That(readings.Count).IsGreaterThan(0);
    await Assert.That(readings.All(r => !r.Tagged)).IsTrue()
      .Because("a provider that cannot name its database reports what it always reported, rather than an empty name");
  }

  private static void _fillEveryGauge(TableStatisticsMetrics metrics) {
    metrics.UpdateTableSizes(new Dictionary<string, long> { ["wh_outbox"] = 8_192 });
    metrics.UpdateQueueDepths(new Dictionary<string, long> { ["outbox"] = 3 });
    metrics.UpdateTableBloat(new Dictionary<string, double> { ["wh_outbox"] = 1.2 });
    metrics.UpdateTableStorage(new Dictionary<string, TableStorageStatistics> {
      ["wh_outbox"] = new(HeapBytes: 4_096, ToastBytes: 0, IndexBytes: 4_096, LiveRows: 10, BloatRatio: 1.2),
    });
    metrics.UpdateUnusedIndexes(new Dictionary<string, UnusedIndex> { ["idx_outbox_topic"] = new("wh_outbox", 16_384) });
    metrics.UpdateEventTypeBytes(new Dictionary<string, EventTypeBytes> {
      ["Orders.OrderPlaced, Orders"] = new(SampledRows: 4, MeanDataBytes: 120, MeanMetadataBytes: 80, EstimatedTotalBytes: 800),
    });
  }

  /// <summary>Listens to every gauge on one metrics instance's own meter, so other tests' instances cannot leak in.</summary>
  private sealed class Gauges : IDisposable {
    private readonly TestMeterFactory _factory = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, bool Tagged, string? Database)> _readings = [];

    public Gauges() {
      Metrics = new TableStatisticsMetrics(new WhizbangMetrics(_factory));
      var meter = _factory.CreatedMeters[0];
      _listener.InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter == meter) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => _record(instrument, tags));
      _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => _record(instrument, tags));
      _listener.Start();
    }

    public TableStatisticsMetrics Metrics { get; }

    public List<(string Name, bool Tagged, string? Database)> Read() {
      _readings.Clear();
      _listener.RecordObservableInstruments();
      return [.. _readings];
    }

    private void _record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      var tagged = false;
      string? database = null;
      foreach (var tag in tags) {
        if (tag.Key == "database") {
          tagged = true;
          database = tag.Value?.ToString();
        }
      }
      _readings.Add((instrument.Name, tagged, database));
    }

    public void Dispose() {
      _listener.Dispose();
      _factory.Dispose();
    }
  }
}
