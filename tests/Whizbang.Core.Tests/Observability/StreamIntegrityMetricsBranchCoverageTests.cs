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
/// Branch backfill for <see cref="StreamIntegrityMetrics.UpdateLedgerGauges"/>: a null snapshot
/// resets the gauges to the empty reading instead of storing null.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/StreamIntegrityMetrics.cs</code-under-test>
public class StreamIntegrityMetricsBranchCoverageTests {

  [Test]
  public async Task UpdateLedgerGauges_NullSnapshot_ResetsToEmptyAsync() {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    metrics.UpdateLedgerGauges(new LedgerGaugeSnapshot { UnhealedBuckets = 7, RepairExhausted = 2, OldestUnhealedAgeSeconds = 30 });

    metrics.UpdateLedgerGauges(null!);

    await Assert.That(metrics.CurrentLedgerGaugesForTest).IsSameReferenceAs(LedgerGaugeSnapshot.Empty)
      .Because("a null snapshot must fall back to the empty reading, never leave the gauge callbacks a null to dereference");
    await Assert.That(metrics.CurrentLedgerGaugesForTest.UnhealedBuckets).IsEqualTo(0L);
  }
}
