using System.Collections.Generic;
using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Coverage-round test for <see cref="DeadLetterMetrics.RecordReleaseWave"/>, the one DLQ-canary
/// method neither <see cref="DeadLetterMetricsTests"/> nor <see cref="DeadLetterMetricsEmissionTests"/>
/// calls: both exercise promotion and cohort verdicts, but never the trickle-release step a Mixed
/// verdict starts, so the private release-waves counter it feeds is never observed.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/DeadLetterMetrics.cs</code-under-test>
public class DeadLetterMetricsCoverageTests {

  // A trickle release only earns the next doubling by staying clean; if this counter stopped
  // reporting, or lost the cohort/outcome tags, an operator could not tell a campaign that is
  // quietly widening its release from one that already washed back and needs attention -- both
  // would look like the same flat line on the DLQ dashboard.
  [Test]
  public async Task RecordReleaseWave_CountsCleanAndHaltedOutcomesTaggedByCohortAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new DeadLetterMetrics(new WhizbangMetrics(factory));
    var meter = factory.CreatedMeters[0];
    var readings = new List<(string Name, long Value, string? Cohort, string? Outcome)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter == meter) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
      string? cohort = null, outcome = null;
      foreach (var tag in tags) {
        if (tag.Key == "cohort") { cohort = tag.Value?.ToString(); }
        if (tag.Key == "outcome") { outcome = tag.Value?.ToString(); }
      }
      readings.Add((instrument.Name, value, cohort, outcome));
    });
    listener.Start();

    metrics.RecordReleaseWave("order-timeout-fingerprint", clean: true);
    metrics.RecordReleaseWave("order-timeout-fingerprint", clean: false);

    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.dead_letters.release_waves" && r.Value == 1
      && r.Cohort == "order-timeout-fingerprint" && r.Outcome == "clean"))
      .IsTrue().Because("a wave that stayed out is what lets the next wave double -- losing it hides whether a trickle is actually progressing");
    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.dead_letters.release_waves" && r.Value == 1
      && r.Cohort == "order-timeout-fingerprint" && r.Outcome == "halted"))
      .IsTrue().Because("a washed-back wave halts the trickle and needs an operator's attention; without the halted tag it is indistinguishable from a clean wave on the same chart");
  }
}
