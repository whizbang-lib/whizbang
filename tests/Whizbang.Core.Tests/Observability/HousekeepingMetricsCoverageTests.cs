using System.Collections.Generic;
using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Coverage-round test for <see cref="HousekeepingMetrics.RecordItems"/> and the get-only
/// <see cref="HousekeepingMetrics.Items"/> counter it reads. Every test in
/// <see cref="HousekeepingMetricsTests"/> drives the coordinator's TryBegin/End pair, which reports
/// arbitration decisions and slot occupancy but never item volume, so RecordItems -- and the Items
/// property getter it exercises -- are never called.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/HousekeepingMetrics.cs</code-under-test>
public class HousekeepingMetricsCoverageTests {

  // If RecordItems stopped incrementing -- or dropped the activity tag -- "what did housekeeping
  // actually do" would go blank for that activity even while Decisions/Running still show it
  // holding the slot: an operator could not tell an activity that is genuinely idle from one that is
  // silently failing to report the work it did. A zero-count cycle must stay silent too, or every
  // idle tick would inflate the rollup and hide a real drop in throughput behind constant noise.
  [Test]
  public async Task RecordItems_AddsCountTaggedByActivity_AndSkipsZeroCountCyclesAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new HousekeepingMetrics(new WhizbangMetrics(factory), idleTracker: null);
    var meter = factory.CreatedMeters[0];
    var readings = new List<(string Name, long Value, string? Activity)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter == meter) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
      string? activity = null;
      foreach (var tag in tags) {
        if (tag.Key == "activity") { activity = tag.Value?.ToString(); }
      }
      readings.Add((instrument.Name, value, activity));
    });
    listener.Start();

    metrics.RecordItems(HousekeepingCoordinator.Activity.DeadLetterRecovery, 37);
    metrics.RecordItems(HousekeepingCoordinator.Activity.Maintenance, 0);

    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.housekeeping.items" && r.Value == 37 && r.Activity == "DeadLetterRecovery"))
      .IsTrue().Because("the volume rollup must attribute items processed to the activity that did the work");
    await Assert.That(readings.Any(r => r.Name == "whizbang.housekeeping.items" && r.Activity == "Maintenance"))
      .IsFalse().Because("a zero-count cycle is not activity; recording it would inflate the rollup on every idle tick");
  }
}
