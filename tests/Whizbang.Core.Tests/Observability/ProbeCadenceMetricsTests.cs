using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The probe cadence meters make an instance's idle footprint visible: one tick counter per
/// periodic probe tagged by whether the tick found work, plus a count of duty attempts answered
/// from memory because the duty was known to be held. The series for every known probe exist at
/// zero from construction, so a dashboard shows a probe that never ticks as a flat zero rather
/// than a missing series that reads as "not wired".
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/ProbeCadenceMetrics.cs</code-under-test>
[Category("Core")]
[Category("Observability")]
public class ProbeCadenceMetricsTests {

  [Test]
  public async Task KnownProbes_HaveBothOutcomeSeriesAtZeroFromConstructionAsync() {
    using var factory = new TestMeterFactory();
    _ = new ProbeCadenceMetrics(new WhizbangMetrics(factory));

    var ticks = ProbeMeterReader.Read(factory.CreatedMeters[0], "whizbang.probes.ticks");

    foreach (var probe in new[] {
      ProbeCadenceMetrics.PROBE_DURABLE_SIGNAL_TAIL,
      ProbeCadenceMetrics.PROBE_INSTANCE_LIFECYCLE,
      ProbeCadenceMetrics.PROBE_BACKLOG_AGE,
    }) {
      await Assert.That(ticks.TryGetValue((probe, ProbeCadenceMetrics.OUTCOME_WORK), out var work)).IsTrue()
        .Because($"the {probe} work series must exist before the first tick");
      await Assert.That(work).IsEqualTo(0);
      await Assert.That(ticks.TryGetValue((probe, ProbeCadenceMetrics.OUTCOME_IDLE), out var idle)).IsTrue()
        .Because($"the {probe} idle series must exist before the first tick");
      await Assert.That(idle).IsEqualTo(0);
    }
  }

  [Test]
  public async Task RecordTick_IncrementsTheSeriesForTheOutcomeAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new ProbeCadenceMetrics(new WhizbangMetrics(factory));

    metrics.RecordTick(ProbeCadenceMetrics.PROBE_DURABLE_SIGNAL_TAIL, foundWork: true);
    metrics.RecordTick(ProbeCadenceMetrics.PROBE_DURABLE_SIGNAL_TAIL, foundWork: false);
    metrics.RecordTick(ProbeCadenceMetrics.PROBE_DURABLE_SIGNAL_TAIL, foundWork: false);

    var ticks = ProbeMeterReader.Read(factory.CreatedMeters[0], "whizbang.probes.ticks");
    await Assert.That(ticks[(ProbeCadenceMetrics.PROBE_DURABLE_SIGNAL_TAIL, ProbeCadenceMetrics.OUTCOME_WORK)]).IsEqualTo(1);
    await Assert.That(ticks[(ProbeCadenceMetrics.PROBE_DURABLE_SIGNAL_TAIL, ProbeCadenceMetrics.OUTCOME_IDLE)]).IsEqualTo(2);
    await Assert.That(ticks[(ProbeCadenceMetrics.PROBE_INSTANCE_LIFECYCLE, ProbeCadenceMetrics.OUTCOME_IDLE)]).IsEqualTo(0)
      .Because("a tick on one probe must not bleed into another probe's series");
  }

  [Test]
  public async Task SuppressedDutyAttempts_AreCountedPerDutyAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new ProbeCadenceMetrics(new WhizbangMetrics(factory));

    metrics.SuppressedDutyAttempts.Add(1, new KeyValuePair<string, object?>("duty", "stamper"));
    metrics.SuppressedDutyAttempts.Add(1, new KeyValuePair<string, object?>("duty", "stamper"));
    metrics.SuppressedDutyAttempts.Add(1, new KeyValuePair<string, object?>("duty", "migrator"));

    var series = ProbeMeterReader.ReadSeries(factory.CreatedMeters[0], "whizbang.probes.suppressed_duty_attempts");
    await Assert.That(series.Single(s => s.Tags.TryGetValue("duty", out var d1) && Equals(d1, "stamper")).Value).IsEqualTo(2);
    await Assert.That(series.Single(s => s.Tags.TryGetValue("duty", out var d2) && Equals(d2, "migrator")).Value).IsEqualTo(1);
  }

  [Test]
  public async Task RecordTick_WithAnEmptyProbeName_ThrowsAsync() {
    var metrics = new ProbeCadenceMetrics(new WhizbangMetrics());

    await Assert.That(() => metrics.RecordTick("", foundWork: true)).Throws<ArgumentException>()
      .Because("an unnamed probe would collapse into one anonymous series nobody can read");
  }

  [Test]
  public async Task Meter_IsNamedForTheProbeDomainAsync() {
    using var factory = new TestMeterFactory();
    _ = new ProbeCadenceMetrics(new WhizbangMetrics(factory));

    await Assert.That(factory.CreatedMeters[0].Name).IsEqualTo(ProbeCadenceMetrics.METER_NAME);
  }

  [Test]
  public async Task NullWhizbangMetrics_ThrowsAsync() {
    await Assert.That(() => new ProbeCadenceMetrics(null!)).Throws<ArgumentNullException>();
  }
}
