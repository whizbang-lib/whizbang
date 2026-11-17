// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// #2365, owner decision: <c>whizbang.maintenance.eligible_not_yet_deleted</c>, the streams due for a cut that the seal
/// gate holds, per task, from a bounded count. No reading reports no series.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/MaintenanceMetrics.cs</code-under-test>
/// <docs>resilience/stream-integrity#integrity-instruments</docs>
public class MaintenanceMetricsEligibleTests {
  private const string ELIGIBLE = "whizbang.maintenance.eligible_not_yet_deleted";

  private static MaintenanceMetrics _metrics() =>
    new(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  [Test]
  public async Task AReading_IsOneSeriesPerTask_WithItsCap_AndAWithdrawnOneIsNoneAsync() {
    var metrics = _metrics();
    var meter = metrics.RowsAffected.Meter;
    await Assert.That(ProbeMeterReader.ReadSeries(meter, ELIGIBLE)).IsEmpty().Because("no reading is not zero");

    metrics.UpdateCutsAwaitingSeal(new CutsAwaitingSeal {
      Reported = true,
      Tasks = new Dictionary<string, PendingRows>(StringComparer.Ordinal) {
        ["truncate"] = new(4, false),
        ["archive"] = new(1000, true),
      },
    });

    var series = ProbeMeterReader.ReadSeries(meter, ELIGIBLE)
      .ToDictionary(s => (string)s.Tags["task"]!, s => (s.Value, (bool)s.Tags["capped"]!));
    await Assert.That(series["truncate"]).IsEqualTo((4L, false));
    await Assert.That(series["archive"]).IsEqualTo((1000L, true));

    metrics.UpdateCutsAwaitingSeal(null);
    await Assert.That(ProbeMeterReader.ReadSeries(meter, ELIGIBLE)).IsEmpty()
      .Because("an instance without the maintainer duty withdraws the reading");
  }
}
