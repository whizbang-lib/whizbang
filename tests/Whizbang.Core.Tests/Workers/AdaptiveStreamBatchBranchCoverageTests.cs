// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A fetch issued with no cap carries no evidence of depth, so it must never earn growth, even
/// though any positive row count is "at least" a zero cap.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/AdaptiveStreamBatch.cs</code-under-test>
[Category("Workers")]
public class AdaptiveStreamBatchBranchCoverageTests {

  [Test]
  public async Task Observe_ZeroCapRequested_IsNotSaturationAndDoesNotGrowAsync() {
    var batch = new AdaptiveStreamBatch(ceiling: 1000, floor: 100, additiveStep: 100);

    batch.Observe(rowsReturned: 50, capRequested: 0, reclaimedRows: 0);

    await Assert.That(batch.Current).IsEqualTo(100)
      .Because("a zero cap is not a full page; growing on it would widen the page with no depth evidence");
  }

  [Test]
  public async Task Observe_PositiveCapFilled_GrowsByOneStepAsync() {
    var batch = new AdaptiveStreamBatch(ceiling: 1000, floor: 100, additiveStep: 100);

    batch.Observe(rowsReturned: 100, capRequested: 100, reclaimedRows: 0);

    await Assert.That(batch.Current).IsEqualTo(200);
  }
}
