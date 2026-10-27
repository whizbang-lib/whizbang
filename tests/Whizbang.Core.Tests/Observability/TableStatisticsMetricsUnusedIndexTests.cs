// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Issue #1669: the indexes on the framework's tables that nothing has scanned since the statistics were last reset,
/// with what each one costs on disk, on the table-statistics meter.
/// </summary>
/// <remarks>
/// Each test reads the gauge the way an exporter does, through a listener on this test's own meter.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsMetrics.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Observability/ITableStatisticsProvider.cs</code-under-test>
/// <docs>operations/observability/metrics#unused-indexes</docs>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsMetricsUnusedIndexTests {
  private const string GAUGE = "whizbang.table.unused_index_bytes";

  [Test]
  public async Task EachUnusedIndex_ReportsItsBytes_UnderItsTableAndNameAsync() {
    using var gauges = new Gauges();

    gauges.Metrics.UpdateUnusedIndexes(new Dictionary<string, UnusedIndex> {
      ["idx_orders_customer"] = new("wh_per_orders", 81_920),
      ["idx_outbox_topic"] = new("wh_outbox", 16_384),
    });
    var readings = gauges.Read();

    await Assert.That(readings).Contains((GAUGE, 81_920d, "wh_per_orders", "idx_orders_customer"))
      .Because("the size is what keeping the index costs, and the table and name are what an operator looks up");
    await Assert.That(readings).Contains((GAUGE, 16_384d, "wh_outbox", "idx_outbox_topic"));
    await Assert.That(readings.Count).IsEqualTo(2);
  }

  [Test]
  public async Task AnIndexMissingFromTheNextReading_StopsReportingAsync() {
    // An index that a query has since used, or that was dropped, must not keep reporting its last size: the gauge is
    // read as "what is unused now", and a stale series would say an index in use is not.
    using var gauges = new Gauges();
    gauges.Metrics.UpdateUnusedIndexes(new Dictionary<string, UnusedIndex> {
      ["idx_orders_customer"] = new("wh_per_orders", 81_920),
      ["idx_outbox_topic"] = new("wh_outbox", 16_384),
    });

    gauges.Metrics.UpdateUnusedIndexes(new Dictionary<string, UnusedIndex> {
      ["idx_outbox_topic"] = new("wh_outbox", 16_384),
    });
    var readings = gauges.Read();

    await Assert.That(readings.Count).IsEqualTo(1);
    await Assert.That(readings).Contains((GAUGE, 16_384d, "wh_outbox", "idx_outbox_topic"));
  }

  /// <summary>Listens to one metrics instance's own meter, so other tests' instances cannot leak in.</summary>
  private sealed class Gauges : IDisposable {
    private readonly TestMeterFactory _factory = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, double Value, string? Table, string? Index)> _readings = [];

    public Gauges() {
      Metrics = new TableStatisticsMetrics(new WhizbangMetrics(_factory));
      var meter = _factory.CreatedMeters[0];
      _listener.InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter == meter && instrument.Name == GAUGE) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => _record(instrument, value, tags));
      _listener.Start();
    }

    public TableStatisticsMetrics Metrics { get; }

    public List<(string Name, double Value, string? Table, string? Index)> Read() {
      _readings.Clear();
      _listener.RecordObservableInstruments();
      return [.. _readings];
    }

    private void _record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      string? table = null;
      string? index = null;
      foreach (var tag in tags) {
        if (tag.Key == "table_name") {
          table = tag.Value?.ToString();
        } else if (tag.Key == "index_name") {
          index = tag.Value?.ToString();
        }
      }
      _readings.Add((instrument.Name, value, table, index));
    }

    public void Dispose() {
      _listener.Dispose();
      _factory.Dispose();
    }
  }
}
