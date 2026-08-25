using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// An adaptive governor must be observable, or it cannot be operated.
/// </summary>
/// <remarks>
/// <para>
/// A controller that silently changes concurrency is undebuggable in production. If it converges
/// to its floor on a healthy system, or pins to its ceiling and exhausts a connection pool, the
/// only visible evidence is the damage — slow drains, contention elsewhere — with no way to tell
/// the governor caused it.
/// </para>
/// <para>
/// Width is exported as an observable gauge rather than a counter because it is a LEVEL, not an
/// accumulation: what matters when reading a dashboard is "how wide is it right now", and a
/// counter cannot answer that. Adjustments are a counter tagged by direction, because their value
/// is in the rate and the asymmetry — a governor shrinking far more often than it grows is
/// oscillating, which is the failure mode this design is most exposed to.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/GovernorMetrics.cs</code-under-test>
[Category("Observability")]
public class GovernorMetricsTests {

  /// <summary>
  /// Starts a listener scoped to ONE governor series.
  /// </summary>
  /// <remarks>
  /// The listener can only subscribe by meter NAME, and every <see cref="GovernorMetrics"/>
  /// instance publishes under the same one. Under parallel execution that means
  /// <c>RecordObservableInstruments</c> polls the gauges of every other live instance too, and a
  /// test reading "the first width measurement" can read a sibling test's governor. That is a real
  /// cross-test leak, not a flake: it failed only in CI, where these tests actually overlap.
  ///
  /// Each test therefore registers under its own series name and the callback drops measurements
  /// tagged for anything else, so a test observes only what it created.
  /// </remarks>
  private static (GovernorMetrics Metrics, MeterListener Listener, List<(string Name, long Value, string? Direction)> Captured) _listen(string series) {
    var captured = new List<(string, long, string?)>();
    var metrics = new GovernorMetrics(new WhizbangMetrics());
    var listener = new MeterListener {
      InstrumentPublished = (inst, l) => {
        if (inst.Meter.Name == GovernorMetrics.METER_NAME) { l.EnableMeasurementEvents(inst); }
      },
    };
    listener.SetMeasurementEventCallback<long>((inst, val, tags, _) => {
      string? governor = null;
      string? direction = null;
      foreach (var t in tags) {
        if (t.Key == "governor") { governor = t.Value?.ToString(); }
        if (t.Key == "direction") { direction = t.Value?.ToString(); }
      }
      // Someone else's series — including another test's governor on the shared meter name.
      if (!string.Equals(governor, series, StringComparison.Ordinal)) {
        return;
      }
      lock (captured) { captured.Add((inst.Name, val, direction)); }
    });
    listener.Start();
    return (metrics, listener, captured);
  }

  [Test]
  public async Task Width_IsExportedAsAGaugeThatReflectsCurrentStateAsync() {
    const string SERIES = "width-reflects-current-state";
    var (metrics, listener, captured) = _listen(SERIES);
    var governor = new ThroughputGovernor(floor: 4, ceiling: 64);
    metrics.Track(SERIES, governor);

    listener.RecordObservableInstruments();

    var width = captured.FirstOrDefault(c => c.Name.Contains("width", StringComparison.Ordinal));
    await Assert.That(width.Name).IsNotNull()
      .Because("without the current width on a dashboard there is no way to tell an adaptive "
             + "governor from a broken one — both just look like a slow system");
    await Assert.That(width.Value).IsEqualTo(4)
      .Because("it must report the governor's ACTUAL width, not a configured maximum");

    listener.Dispose();
  }

  [Test]
  public async Task WidthGauge_FollowsTheGovernorAsItAdaptsAsync() {
    const string SERIES = "width-gauge-follows-adaptation";
    var (metrics, listener, captured) = _listen(SERIES);
    var governor = new ThroughputGovernor(floor: 2, ceiling: 64);
    metrics.Track(SERIES, governor);

    var perCycle = 100;
    for (var i = 0; i < 12; i++) {
      governor.Observe(new GovernorSignal(5000, false, TimeSpan.FromMilliseconds(100), perCycle));
      perCycle += 60;
    }
    listener.RecordObservableInstruments();

    var width = captured.Last(c => c.Name.Contains("width", StringComparison.Ordinal));
    await Assert.That(width.Value).IsGreaterThan(2)
      .Because("a gauge pinned to the starting value would be worse than no metric — it would "
             + "report a healthy narrow width while the governor had actually moved");

    listener.Dispose();
  }

  [Test]
  public async Task Adjustments_AreCountedByDirectionAsync() {
    const string SERIES = "adjustments-counted-by-direction";
    var (metrics, listener, captured) = _listen(SERIES);

    metrics.RecordAdjustment(SERIES, from: 4, to: 5);
    metrics.RecordAdjustment(SERIES, from: 5, to: 3);

    var adjustments = captured.Where(c => c.Name.Contains("adjust", StringComparison.Ordinal)).ToList();
    await Assert.That(adjustments.Count).IsGreaterThanOrEqualTo(2);
    await Assert.That(adjustments.Any(a => a.Direction == "grew")).IsTrue();
    await Assert.That(adjustments.Any(a => a.Direction == "shrank")).IsTrue()
      .Because("direction is the whole diagnostic: a governor shrinking far more often than it "
             + "grows is oscillating, and an undirected count cannot show that");

    listener.Dispose();
  }

  [Test]
  public async Task MeterIsRegisteredSoConsumersGetItAutomaticallyAsync() {
    _ = new GovernorMetrics(new WhizbangMetrics());

    await Assert.That(WhizbangMeters.All).Contains(GovernorMetrics.METER_NAME)
      .Because("a meter absent from the central list is one every consumer must remember to add "
             + "by hand — which is exactly the failure that list exists to prevent");
  }
}
