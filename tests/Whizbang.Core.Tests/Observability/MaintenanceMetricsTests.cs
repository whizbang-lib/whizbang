using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Unit tests for <see cref="MaintenanceMetrics"/> — the per-task maintenance-cycle instruments
/// (row retention's fleet-visible "is the reap working" signal).
/// </summary>
/// <docs>fundamentals/perspectives/row-retention</docs>
public class MaintenanceMetricsTests {
  [Test]
  public async Task Instruments_HaveStableNamesAsync() {
    var metrics = new MaintenanceMetrics(new WhizbangMetrics());

    await Assert.That(metrics.RowsAffected.Name).IsEqualTo("whizbang.maintenance.rows_affected");
    await Assert.That(metrics.TaskDuration.Name).IsEqualTo("whizbang.maintenance.task_duration");
    await Assert.That(metrics.TaskDuration.Unit).IsEqualTo("ms");
  }

  [Test]
  public async Task Record_EmitsDurationAlways_RowsOnlyWhenPositiveAsync() {
    var metrics = new MaintenanceMetrics(new WhizbangMetrics());
    var rowMeasurements = new List<long>();
    var durationMeasurements = new List<double>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == MaintenanceMetrics.METER_NAME) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => rowMeasurements.Add(value));
    listener.SetMeasurementEventCallback<double>((_, value, _, _) => durationMeasurements.Add(value));
    listener.Start();

    metrics.Record("reap_expired_perspective_rows", rowsAffected: 0, durationMs: 12.5);
    metrics.Record("reap_expired_perspective_rows", rowsAffected: 42, durationMs: 8.0);

    await Assert.That(durationMeasurements.Count).IsEqualTo(2)
      .Because("duration records every cycle — the liveness half of the signal");
    await Assert.That(rowMeasurements).IsEquivalentTo(new long[] { 42 })
      .Because("zero-row cycles add nothing to the counter (no-op sweep is not signal)");
  }
}
