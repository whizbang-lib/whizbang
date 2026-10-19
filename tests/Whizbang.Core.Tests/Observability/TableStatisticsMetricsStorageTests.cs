// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// What a table's size is made of, how many rows it holds, and which tables are still reported.
/// </summary>
/// <remarks>
/// Each test reads the gauges the way an exporter does, through a listener on this test's own meter, so
/// what is asserted is what an operator's dashboard would receive.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsMetrics.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Observability/ITableStatisticsProvider.cs</code-under-test>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsMetricsStorageTests {
  private static readonly TableStorageStatistics _eventBody = new(
    HeapBytes: 8_192, ToastBytes: 1_048_576, IndexBytes: 16_384, LiveRows: 120, BloatRatio: null);

  private static readonly TableStorageStatistics _orders = new(
    HeapBytes: 409_600, ToastBytes: 8_192, IndexBytes: 73_728, LiveRows: 5_000, BloatRatio: 1.25);

  [Test]
  public async Task TotalBytes_IsTheSumOfTheThreeComponentsAsync() {
    await Assert.That(_eventBody.TotalBytes).IsEqualTo(8_192L + 1_048_576L + 16_384L)
      .Because("estimated_bytes keeps meaning the whole table, so the split has to add back up to it");
  }

  [Test]
  public async Task ComponentBytes_ReportsHeapToastAndIndexesPerTableAsync() {
    using var gauges = new Gauges();

    gauges.Metrics.UpdateTableStorage(new Dictionary<string, TableStorageStatistics> {
      ["wh_event_body"] = _eventBody,
      ["wh_per_orders"] = _orders,
    });
    var readings = gauges.Read();

    await Assert.That(readings).Contains(("whizbang.table.component_bytes", 1_048_576d, "wh_event_body", "toast"))
      .Because("an event store whose bytes are in large documents shows it as TOAST, which is the case "
             + "only the split can reveal");
    await Assert.That(readings).Contains(("whizbang.table.component_bytes", 8_192d, "wh_event_body", "heap"));
    await Assert.That(readings).Contains(("whizbang.table.component_bytes", 16_384d, "wh_event_body", "indexes"));
    await Assert.That(readings).Contains(("whizbang.table.component_bytes", 409_600d, "wh_per_orders", "heap"));
    await Assert.That(readings).Contains(("whizbang.table.component_bytes", 8_192d, "wh_per_orders", "toast"));
    await Assert.That(readings).Contains(("whizbang.table.component_bytes", 73_728d, "wh_per_orders", "indexes"));
    await Assert.That(readings.Count(r => r.Name == "whizbang.table.component_bytes")).IsEqualTo(6)
      .Because("each table reports exactly three components, which bounds the series at three per table");
  }

  [Test]
  public async Task LiveRows_ReportsTheRowCountPerTableAsync() {
    using var gauges = new Gauges();

    gauges.Metrics.UpdateTableStorage(new Dictionary<string, TableStorageStatistics> {
      ["wh_event_body"] = _eventBody,
      ["wh_per_orders"] = _orders,
    });
    var readings = gauges.Read();

    await Assert.That(readings).Contains(("whizbang.table.live_rows", 120d, "wh_event_body", (string?)null))
      .Because("bytes over rows is bytes per row, and both over time are growth");
    await Assert.That(readings).Contains(("whizbang.table.live_rows", 5_000d, "wh_per_orders", (string?)null));
  }

  [Test]
  public async Task ATableMissingFromTheNextReading_StopsReportingAsync() {
    // A table that was dropped, or that fell into the summed "other" series because the cap was
    // reached, must stop reporting its last value. A stale series would be counted twice in every sum
    // a dashboard takes, once under its own name and once inside "other", and never go away.
    using var gauges = new Gauges();
    gauges.Metrics.UpdateTableStorage(new Dictionary<string, TableStorageStatistics> {
      ["wh_event_body"] = _eventBody,
      ["wh_per_orders"] = _orders,
    });
    gauges.Metrics.UpdateTableSizes(new Dictionary<string, long> {
      ["wh_event_body"] = _eventBody.TotalBytes,
      ["wh_per_orders"] = _orders.TotalBytes,
    });
    gauges.Metrics.UpdateTableBloat(new Dictionary<string, double> { ["wh_per_orders"] = 1.25 });

    gauges.Metrics.UpdateTableStorage(new Dictionary<string, TableStorageStatistics> {
      ["wh_event_body"] = _eventBody,
    });
    gauges.Metrics.UpdateTableSizes(new Dictionary<string, long> { ["wh_event_body"] = _eventBody.TotalBytes });
    gauges.Metrics.UpdateTableBloat(new Dictionary<string, double>());
    var readings = gauges.Read();

    await Assert.That(readings.Any(r => r.Table == "wh_per_orders")).IsFalse()
      .Because("every table gauge reports the latest reading, not every table ever seen");
    await Assert.That(readings).Contains(("whizbang.table.estimated_bytes", (double)_eventBody.TotalBytes, "wh_event_body", (string?)null));
    await Assert.That(readings).Contains(("whizbang.table.live_rows", 120d, "wh_event_body", (string?)null));
  }

  /// <summary>Listens to one metrics instance's own meter, so other tests' instances cannot leak in.</summary>
  private sealed class Gauges : IDisposable {
    private readonly TestMeterFactory _factory = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, double Value, string? Table, string? Component)> _readings = [];

    public Gauges() {
      Metrics = new TableStatisticsMetrics(new WhizbangMetrics(_factory));
      var meter = _factory.CreatedMeters[0];
      _listener.InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter == meter) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => _record(instrument, value, tags));
      _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => _record(instrument, value, tags));
      _listener.Start();
    }

    public TableStatisticsMetrics Metrics { get; }

    public List<(string Name, double Value, string? Table, string? Component)> Read() {
      _readings.Clear();
      _listener.RecordObservableInstruments();
      return [.. _readings];
    }

    private void _record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      string? table = null;
      string? component = null;
      foreach (var tag in tags) {
        if (tag.Key == "table_name") {
          table = tag.Value?.ToString();
        } else if (tag.Key == "component") {
          component = tag.Value?.ToString();
        }
      }
      _readings.Add((instrument.Name, value, table, component));
    }

    public void Dispose() {
      _listener.Dispose();
      _factory.Dispose();
    }
  }
}
