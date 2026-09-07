using System.Collections.Generic;
using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Coverage-round tests for the three ObservableGauge callbacks in <see cref="TableStatisticsMetrics"/>.
/// <see cref="TableStatisticsMetricsTests"/> only stores values into the cache and asserts that no
/// exception is thrown -- it never attaches a MeterListener or calls RecordObservableInstruments, so
/// the gauge lambdas that actually read the cache and shape each Measurement are never executed.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsMetrics.cs</code-under-test>
public class TableStatisticsMetricsCoverageTests {

  // Each test below gets its own Meter via TestMeterFactory and filters the listener by that exact
  // Meter instance (reference equality), not by METER_NAME string equality. TableStatisticsMetrics
  // registers its three gauges once per instance under the same fixed name, and instances from other
  // tests in the suite stay alive for the process lifetime with their callbacks still registered --
  // filtering by name would let a sibling test's cached values leak into this test's
  // RecordObservableInstruments() poll (the same trap that forces StreamIntegrityMetricsCoverageTests
  // to collect-and-Contains instead of asserting a single last value). Filtering by instance avoids
  // the trap entirely because only measurements from this test's own Meter reach the listener.

  // If this gauge stopped reporting -- or dropped the table_name tag -- an operator watching disk
  // pressure would see one indistinguishable total instead of which table is growing, and could not
  // tell a runaway event-store table from a healthy one before the alarm fires on total disk instead.
  [Test]
  public async Task EstimatedBytesGauge_ReportsPerTableSizeTaggedByTableNameAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new TableStatisticsMetrics(new WhizbangMetrics(factory));
    var meter = factory.CreatedMeters[0];
    var readings = new List<(string Name, long Value, string? Table)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter == meter) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
      string? table = null;
      foreach (var tag in tags) {
        if (tag.Key == "table_name") { table = tag.Value?.ToString(); }
      }
      readings.Add((instrument.Name, value, table));
    });
    listener.Start();

    metrics.UpdateTableSizes(new Dictionary<string, long> { ["wh_coverage_table"] = 654321 });
    listener.RecordObservableInstruments();

    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.table.estimated_bytes" && r.Value == 654321 && r.Table == "wh_coverage_table"))
      .IsTrue().Because("disk-pressure alerting must resolve to the specific table growing, not a total");
  }

  // If this gauge stopped reporting -- or dropped the queue_name tag -- a backed-up inbox or outbox
  // would look identical to an empty one until messages start expiring, because this per-queue depth
  // series is the only signal that one lane is draining slower than it fills.
  [Test]
  public async Task EstimatedDepthGauge_ReportsPerQueueDepthTaggedByQueueNameAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new TableStatisticsMetrics(new WhizbangMetrics(factory));
    var meter = factory.CreatedMeters[0];
    var readings = new List<(string Name, long Value, string? Queue)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter == meter) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
      string? queue = null;
      foreach (var tag in tags) {
        if (tag.Key == "queue_name") { queue = tag.Value?.ToString(); }
      }
      readings.Add((instrument.Name, value, queue));
    });
    listener.Start();

    metrics.UpdateQueueDepths(new Dictionary<string, long> { ["coverage-outbox"] = 913 });
    listener.RecordObservableInstruments();

    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.queue.estimated_depth" && r.Value == 913 && r.Queue == "coverage-outbox"))
      .IsTrue().Because("a stuck queue must be attributable to its specific inbox/outbox lane, not lost in a combined figure");
  }

  // If this gauge stopped reporting -- or dropped the table_name tag -- an operator could not tell
  // which table's heap has bloated past its live-row size, and a dropped column silently inflating
  // every existing row forever would go unnoticed until reads are visibly slow, long after a rewrite
  // was due.
  [Test]
  public async Task BloatRatioGauge_ReportsPerTableRatioTaggedByTableNameAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new TableStatisticsMetrics(new WhizbangMetrics(factory));
    var meter = factory.CreatedMeters[0];
    var readings = new List<(string Name, double Value, string? Table)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter == meter) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => {
      string? table = null;
      foreach (var tag in tags) {
        if (tag.Key == "table_name") { table = tag.Value?.ToString(); }
      }
      readings.Add((instrument.Name, value, table));
    });
    listener.Start();

    metrics.UpdateTableBloat(new Dictionary<string, double> { ["wh_coverage_table"] = 4.75 });
    listener.RecordObservableInstruments();

    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.table.bloat_ratio" && r.Value == 4.75 && r.Table == "wh_coverage_table"))
      .IsTrue().Because("a sustained multiple over 1.0 is the only signal that a table needs a rewrite instead of another vacuum");
  }
}
