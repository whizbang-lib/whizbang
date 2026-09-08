using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Sagas.Observability;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks meter name and instrument registration. A meter rename or
/// dropped instrument here silently breaks every consumer's
/// OpenTelemetry exporter wiring.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaMetricsTests {

  [Test]
  public async Task Constructor_EveryCounterReportsZeroAtTheFirstCollectionAsync() {
    // Issue #711: a pushed counter exports no series until its first measurement, so a service that
    // has run no saga since its restart would show no saga meter at all. The counters are passive:
    // the meter reports every series at collection, so they read as zero from construction.
    var seededAtZero = new HashSet<string>(StringComparer.Ordinal);
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == SagaMetrics.METER_NAME) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => {
      if (value == 0) {
        lock (seededAtZero) {
          seededAtZero.Add(instrument.Name);
        }
      }
    });
    listener.Start();

    _ = new SagaMetrics(new WhizbangMetrics());
    listener.RecordObservableInstruments();   // what an exporter does at collection

    foreach (var counter in new[] {
      "whizbang.sagas.initiated", "whizbang.sagas.completed", "whizbang.sagas.failed",
      "whizbang.sagas.hooks_completed", "whizbang.sagas.hooks_failed", "whizbang.sagas.items_reset" }) {
      await Assert.That(seededAtZero).Contains(counter)
        .Because("a quiet saga subsystem must read as zero on a dashboard, not as a missing meter");
    }
  }

  [Test]
  public async Task Constructor_CreatesAllInstrumentsAsync() {
    var metrics = new SagaMetrics(new WhizbangMetrics());

    await Assert.That(metrics.SagasInitiated).IsNotNull();
    await Assert.That(metrics.SagasCompleted).IsNotNull();
    await Assert.That(metrics.SagasFailed).IsNotNull();
    await Assert.That(metrics.SagaDurationSeconds).IsNotNull();
    await Assert.That(metrics.ItemsCompletedPerSaga).IsNotNull();
    await Assert.That(metrics.ItemsFailedPerSaga).IsNotNull();
    await Assert.That(metrics.HooksCompleted).IsNotNull();
    await Assert.That(metrics.HooksFailed).IsNotNull();
    await Assert.That(metrics.ItemsReset).IsNotNull();
  }
}
