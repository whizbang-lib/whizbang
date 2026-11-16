// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// #2273: repaired events are counted by how their receptors were decided, state only or run through receptors, and why,
/// so an operator can see a repair that is (or is not) running reactions again.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/StreamIntegrityMetrics.cs</code-under-test>
/// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
[Category("Unit")]
[Category("Observability")]
public class StreamIntegrityMetricsRepairedEventsTests {
  private const string INSTRUMENT = "whizbang.stream_integrity.repaired_events";

  private static long _read(System.Diagnostics.Metrics.Meter meter, string delivery, string reason) =>
    ProbeMeterReader.ReadSeries(meter, INSTRUMENT)
      .Where(s => Equals(s.Tags.GetValueOrDefault("delivery"), delivery) && Equals(s.Tags.GetValueOrDefault("reason"), reason))
      .Sum(s => s.Value);

  [Test]
  public async Task RepairedEvents_AreCountedByDeliveryAndReasonAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(factory));
    var meter = factory.CreatedMeters.Single(m => m.Name == StreamIntegrityMetrics.METER_NAME);

    metrics.RecordRepairedEvents(RepairReceptorReason.NeverHandled, 2);
    metrics.RecordRepairedEvents(RepairReceptorReason.OutsideLookback, 5);
    metrics.RecordRepairedEvents(RepairReceptorReason.AlreadyHandled, 1);

    await Assert.That(_read(meter, "receptors", "never_handled")).IsEqualTo(2L);
    await Assert.That(_read(meter, "state_only", "outside_lookback")).IsEqualTo(5L);
    await Assert.That(_read(meter, "state_only", "already_handled")).IsEqualTo(1L);
  }

  [Test]
  public async Task EveryReason_ExistsAtZero_BeforeAnythingIsRepairedAsync() {
    using var factory = new TestMeterFactory();
    _ = new StreamIntegrityMetrics(new WhizbangMetrics(factory));
    var meter = factory.CreatedMeters.Single(m => m.Name == StreamIntegrityMetrics.METER_NAME);

    var reasons = ProbeMeterReader.ReadSeries(meter, INSTRUMENT)
      .Select(s => s.Tags.GetValueOrDefault("reason") as string)
      .Where(r => r is not null)
      .ToHashSet();

    await Assert.That(reasons).IsEquivalentTo(new HashSet<string?> {
      "requested_state_only", "no_lookback", "no_origin_time", "outside_lookback", "before_ledger_coverage",
      "ledger_unavailable", "already_handled", "never_handled",
    }).Because("a dashboard reads a zero for each reason, not a missing series");
  }
}
