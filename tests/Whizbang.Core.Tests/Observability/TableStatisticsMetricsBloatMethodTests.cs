// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Issue #1807: <c>whizbang.table.bloat_ratio</c> carries a <c>method</c> tag, so a dashboard can tell the catalog
/// estimate every statistics pass reports from a table the maintenance cycle measured with pgstattuple_approx.
/// </summary>
/// <remarks>Each test reads the gauge the way an exporter does, through a listener on this test's own meter.</remarks>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsMetrics.cs</code-under-test>
/// <docs>operations/observability/metrics#table-statistics</docs>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsMetricsBloatMethodTests {
  private const string GAUGE = "whizbang.table.bloat_ratio";

  [Test]
  public async Task TheStatisticsPassReadings_AreTaggedAsTheEstimateAsync() {
    using var gauges = new Gauges();
    gauges.Metrics.UpdateTableBloat(new Dictionary<string, double> { ["wh_outbox"] = 4.5 });

    var readings = gauges.Read();

    await Assert.That(readings).IsEquivalentTo([("wh_outbox", 4.5d, (string?)"estimate")])
      .Because("the statistics pass estimates from pg_stats for every table, and says so");
  }

  [Test]
  public async Task AMeasuredTable_IsASeriesOfItsOwn_TaggedWithItsMethodAsync() {
    using var gauges = new Gauges();
    gauges.Metrics.UpdateTableBloat(new Dictionary<string, double> { ["wh_outbox"] = 4.5 });
    gauges.Metrics.UpdateMeasuredTableBloat(new Dictionary<string, BloatMeasurement> {
      ["wh_outbox"] = new(1.4, BloatMeasurementMethod.Approx),
    });

    var readings = gauges.Read();

    await Assert.That(readings).IsEquivalentTo([
      ("wh_outbox", 4.5d, (string?)"estimate"),
      ("wh_outbox", 1.4d, (string?)"approx"),
    ]).Because("the estimate keeps reporting beside the measurement, so the two can be compared on one chart");
  }

  [Test]
  public async Task AMeasurementNoLongerReported_StopsReportingAsync() {
    using var gauges = new Gauges();
    gauges.Metrics.UpdateMeasuredTableBloat(new Dictionary<string, BloatMeasurement> {
      ["wh_outbox"] = new(5.2, BloatMeasurementMethod.Approx),
      ["wh_inbox"] = new(3.3, BloatMeasurementMethod.Approx),
    });
    gauges.Metrics.UpdateMeasuredTableBloat(new Dictionary<string, BloatMeasurement> {
      ["wh_inbox"] = new(3.1, BloatMeasurementMethod.Approx),
    });

    var readings = gauges.Read();

    await Assert.That(readings).IsEquivalentTo([("wh_inbox", 3.1d, (string?)"approx")])
      .Because("a table that was rewritten and left the queue is no longer measured, and its last value must not linger");
  }

  /// <summary>Listens to the bloat gauge on one metrics instance's own meter.</summary>
  private sealed class Gauges : IDisposable {
    private readonly TestMeterFactory _factory = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Table, double Value, string? Method)> _readings = [];

    public Gauges() {
      Metrics = new TableStatisticsMetrics(new WhizbangMetrics(_factory));
      var meter = _factory.CreatedMeters[0];
      _listener.InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter == meter && instrument.Name == GAUGE) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<double>((_, value, tags, _) => _record(value, tags));
      _listener.Start();
    }

    public TableStatisticsMetrics Metrics { get; }

    public List<(string Table, double Value, string? Method)> Read() {
      _readings.Clear();
      _listener.RecordObservableInstruments();
      return [.. _readings];
    }

    private void _record(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      string? table = null;
      string? method = null;
      foreach (var tag in tags) {
        if (tag.Key == "table_name") {
          table = tag.Value?.ToString();
        } else if (tag.Key == "method") {
          method = tag.Value?.ToString();
        }
      }
      _readings.Add((table ?? "", value, method));
    }

    public void Dispose() {
      _listener.Dispose();
      _factory.Dispose();
    }
  }
}
