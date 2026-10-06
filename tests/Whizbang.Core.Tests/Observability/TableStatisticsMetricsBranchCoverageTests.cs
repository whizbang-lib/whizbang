// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Branch backfill for <see cref="TableStatisticsMetrics"/>' bloat read-back: a table that was
/// never published reads as absent, not as a zero ratio.
/// </summary>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsMetricsBranchCoverageTests {

  [Test]
  public async Task GetBloatRatio_UnpublishedTable_ReadsAsAbsentAsync() {
    var metrics = new TableStatisticsMetrics(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    metrics.UpdateTableBloat(new Dictionary<string, double> { ["wh_event_store"] = 1.5 });

    await Assert.That(metrics.GetBloatRatioForTest("wh_outbox")).IsNull()
      .Because("a table the collector never reported must read as unknown, which a 0.0 ratio would misstate as lean");
    await Assert.That(metrics.GetBloatRatioForTest("wh_event_store")).IsEqualTo(1.5);
  }
}
