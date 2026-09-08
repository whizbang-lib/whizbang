using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The liveness meters are passive: the worker and the monitor bump plain counters as things
/// happen, and the meter reports them only when a collector polls. A watchdog beat means a
/// regular beat went missing; a retracted death means a live instance was briefly called dead.
/// Both are the visible trace of the failure mode these changes remove, and both must reach a
/// dashboard without any of the code paths that record them doing any metrics work of their own.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/InstanceLivenessMetrics.cs</code-under-test>
[Category("Core")]
[Category("Observability")]
public class InstanceLivenessMetricsTests {

  [Test]
  public async Task Counters_ReportWhatWasAddedWhenPolledAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new InstanceLivenessMetrics(new WhizbangMetrics(factory));
    var meter = factory.CreatedMeters[0];

    metrics.WatchdogBeats.Add(1);
    metrics.WatchdogBeats.Add(1);
    metrics.SlowBeats.Add(1);
    metrics.DeathsAnnounced.Add(3);
    metrics.DeathsRetracted.Add(1);

    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.liveness.watchdog_beats")).IsEqualTo(2);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.liveness.slow_beats")).IsEqualTo(1);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.liveness.deaths_announced")).IsEqualTo(3);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.liveness.deaths_retracted")).IsEqualTo(1);
  }

  [Test]
  public async Task Meter_IsNamedForTheLivenessDomainAsync() {
    using var factory = new TestMeterFactory();
    _ = new InstanceLivenessMetrics(new WhizbangMetrics(factory));

    await Assert.That(factory.CreatedMeters[0].Name).IsEqualTo(InstanceLivenessMetrics.METER_NAME);
  }

  [Test]
  public async Task WithoutAMeterFactory_StillConstructsAsync() {
    // Hosts without OpenTelemetry wiring still run the worker; the meters fall back to a bare
    // Meter rather than making liveness depend on observability being configured.
    var metrics = new InstanceLivenessMetrics(new WhizbangMetrics());

    metrics.WatchdogBeats.Add(1);

    await Assert.That(metrics.WatchdogBeats).IsNotNull();
  }

  [Test]
  public async Task NullWhizbangMetrics_ThrowsAsync() {
    await Assert.That(() => new InstanceLivenessMetrics(null!)).Throws<ArgumentNullException>();
  }
}
