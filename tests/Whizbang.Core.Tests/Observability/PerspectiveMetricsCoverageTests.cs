using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Coverage round 23 tail: <see cref="PerspectiveMetrics.SetPendingEvents"/> and the
/// <c>whizbang.perspective.pending_events</c> observable gauge callback it feeds. No existing
/// test in PerspectiveMetricsTests.cs / PerspectiveRewindMetricsTests.cs calls SetPendingEvents or
/// polls the gauge, so neither the setter nor the observe-value lambda has ever run.
/// </summary>
/// <remarks>
/// Uses a <see cref="TestMeterFactory"/>-isolated <see cref="Meter"/> and filters the raw
/// <see cref="MeterListener"/> by that Meter's object reference — the established fix for
/// observable-instrument tests in this file's siblings, since MeterListener polls per Meter and a
/// name-only filter would also fire every other live PerspectiveMetrics instance's callback.
/// </remarks>
[Category("Core")]
[Category("Observability")]
public class PerspectiveMetricsCoverageTests {

  /// <summary>
  /// Operator impact: the perspective backlog gauge is how an operator sees "is the perspective
  /// worker falling behind" on a dashboard, without attaching a debugger. If SetPendingEvents
  /// stopped updating the cached value, or the gauge stopped reading it back, the dashboard would
  /// quietly freeze at whatever the last-correct value happened to be — indistinguishable from a
  /// genuinely healthy, unchanging backlog.
  /// </summary>
  [Test]
  public async Task SetPendingEvents_ObservableGaugeReportsTheCurrentValueAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    var meter = factory.CreatedMeters[0];

    var seen = new List<long>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter == meter) { l.EnableMeasurementEvents(instrument); }
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => {
      if (instrument.Name == "whizbang.perspective.pending_events") {
        lock (seen) { seen.Add(value); }
      }
    });
    listener.Start();

    metrics.SetPendingEvents(42);
    listener.RecordObservableInstruments();

    long[] afterFirstPoll;
    lock (seen) { afterFirstPoll = [.. seen]; }
    await Assert.That(afterFirstPoll).Count().IsEqualTo(1);
    await Assert.That(afterFirstPoll[0]).IsEqualTo(42L)
      .Because("the gauge must report exactly the last value SetPendingEvents cached, not a "
             + "stale or zeroed reading");

    metrics.SetPendingEvents(7);
    listener.RecordObservableInstruments();

    long[] afterSecondPoll;
    lock (seen) { afterSecondPoll = [.. seen]; }
    await Assert.That(afterSecondPoll[^1]).IsEqualTo(7L)
      .Because("the gauge reflects CURRENT state on every poll, not the first-ever value — a "
             + "drained backlog must show as drained, not stuck at its historical peak");
  }
}
