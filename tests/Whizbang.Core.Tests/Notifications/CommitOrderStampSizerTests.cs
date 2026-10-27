// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Unit tests for <see cref="CommitOrderStampSizer"/>: the commit-order stamper's batch size carried across a run of
/// calls. Every duration is given, never measured, so each sequence is the same on any machine.
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
/// <tests>Whizbang.Core/Notifications/CommitOrderStampSizer.cs</tests>
public class CommitOrderStampSizerTests {
  private const int CONFIGURED = 1_000;
  private const int CEILING = 25_000;
  private static readonly TimeSpan _budget = TimeSpan.FromMilliseconds(2_500);
  private static readonly TimeSpan _cheap = TimeSpan.FromMilliseconds(100);

  [Test]
  public async Task Size_StartsAtTheConfiguredSizeAsync() =>
    await Assert.That(new CommitOrderStampSizer(CONFIGURED, CEILING).Size).IsEqualTo(CONFIGURED);

  [Test]
  public async Task AfterCall_AFullCheapCall_GrowsAsync() {
    var sizer = new CommitOrderStampSizer(CONFIGURED, CEILING);

    sizer.AfterCall(stamped: CONFIGURED, took: _cheap, budget: _budget);

    await Assert.That(sizer.Size).IsEqualTo(2_000)
      .Because("a full call well inside the budget left rows behind and would still fit doubled");
  }

  [Test]
  public async Task AfterCall_AFirstFullCallPastTheBudget_HalvesAsync() {
    var sizer = new CommitOrderStampSizer(CONFIGURED, CEILING);

    sizer.AfterCall(stamped: CONFIGURED, took: TimeSpan.FromSeconds(10), budget: _budget);

    await Assert.That(sizer.Size).IsEqualTo(500)
      .Because("with nothing to compare it to, a call past the budget is assumed to cost what it stamps (#1327)");
  }

  [Test]
  public async Task AfterCall_AShortCall_ReturnsToTheConfiguredSizeAsync() {
    var sizer = new CommitOrderStampSizer(CONFIGURED, CEILING);
    sizer.AfterCall(stamped: CONFIGURED, took: _cheap, budget: _budget);

    sizer.AfterCall(stamped: 3, took: TimeSpan.FromSeconds(10), budget: _budget);

    await Assert.That(sizer.Size).IsEqualTo(CONFIGURED)
      .Because("a short call means the pending set is drained to the fence, whatever the call cost");
  }

  [Test]
  public async Task AfterTimeout_HalvesAsync() {
    var sizer = new CommitOrderStampSizer(CONFIGURED, CEILING);

    sizer.AfterTimeout();

    await Assert.That(sizer.Size).IsEqualTo(500)
      .Because("a call that timed out rolled back, so the same size cannot succeed (#1217)");
  }
}
