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
  public async Task AfterCall_AFullCallPastHalfTheBudget_HoldsItsSizeAsync() {
    var sizer = new CommitOrderStampSizer(CONFIGURED, CEILING);

    sizer.AfterCall(stamped: CONFIGURED, took: TimeSpan.FromMilliseconds(1_500), budget: _budget);

    await Assert.That(sizer.Size).IsEqualTo(CONFIGURED)
      .Because("doubling a call that took more than half the budget would take about twice as long, past it");
  }

  [Test]
  public async Task AfterCall_AShortCall_ReturnsToTheConfiguredSizeAsync() {
    var sizer = new CommitOrderStampSizer(CONFIGURED, CEILING);
    sizer.AfterCall(stamped: CONFIGURED, took: _cheap, budget: _budget);

    sizer.AfterCall(stamped: 3, took: TimeSpan.FromSeconds(10), budget: _budget);

    await Assert.That(sizer.Size).IsEqualTo(CONFIGURED)
      .Because("a short call means the pending set is drained to the fence, whatever the call cost");
  }

  /// <summary>
  /// #1786: halving a call that cost about the same at half the size shows a cost that does not come from the size,
  /// so the batch stops shrinking there instead of falling to one row.
  /// </summary>
  [Test]
  public async Task AfterCall_HalvingDidNotMakeTheCallCheaper_StopsShrinkingAsync() {
    var sizer = new CommitOrderStampSizer(CONFIGURED, CEILING);
    sizer.AfterCall(stamped: 1_000, took: TimeSpan.FromMilliseconds(3_000), budget: _budget);
    sizer.AfterCall(stamped: 500, took: TimeSpan.FromMilliseconds(2_900), budget: _budget);
    var afterTheFirstComparison = sizer.Size;

    sizer.AfterCall(stamped: 500, took: TimeSpan.FromMilliseconds(3_000), budget: _budget);

    await Assert.That(afterTheFirstComparison).IsEqualTo(500)
      .Because("500 rows cost 2.9 s where 1,000 cost 3 s: almost all of a call is a cost the batch does not change");
    await Assert.That(sizer.Size).IsEqualTo(500)
      .Because("a call at the size where the cost stopped changing is not shrunk again, however long it took");
  }

  /// <summary>
  /// #1786, the sequence a deployed stamper logged: each call cost about 2.5 s however small, plus a share per row.
  /// Halving every call past the budget took it from 1,000 to one row, where it stayed while a whole-store backlog
  /// waited; the batch now stops where the share per row stops mattering.
  /// </summary>
  [Test]
  public async Task AfterCall_AFixedCostPastTheBudget_DoesNotCollapseToOneRowAsync() {
    var sizer = new CommitOrderStampSizer(CONFIGURED, CEILING);
    var sizes = new List<int>();

    for (var call = 0; call < 20; call++) {
      sizes.Add(sizer.Size);
      sizer.AfterCall(stamped: sizer.Size, took: _fixedPlusPerRow(sizer.Size), budget: _budget);
    }

    await Assert.That(sizes.Min()).IsEqualTo(125)
      .Because($"the sizes were {string.Join(", ", sizes)}: at 125 rows a call cost 3.4 s against 4.3 s at 250, so "
        + "most of it is the fixed part and a smaller batch would only pay it more often");
    await Assert.That(sizer.Size).IsEqualTo(125);
  }

  /// <summary>
  /// #1786: a cost that does come from the size is still halved until the call fits, as #1327 decided.
  /// </summary>
  [Test]
  public async Task AfterCall_ACostThatFollowsTheSize_KeepsHalvingAsync() {
    var sizer = new CommitOrderStampSizer(CONFIGURED, CEILING);
    sizer.AfterCall(stamped: 1_000, took: TimeSpan.FromSeconds(10), budget: _budget);

    sizer.AfterCall(stamped: 500, took: TimeSpan.FromSeconds(5), budget: _budget);

    await Assert.That(sizer.Size).IsEqualTo(250)
      .Because("half the rows cost half the time, so the next halving brings the call inside the budget");
  }

  /// <summary>
  /// #1786: once calls are cheap again (the migration I/O that slowed them is over), the batch grows past every size
  /// a slow call backed off to. The cap a slow call set used to hold until the backlog drained, which at one row per
  /// call it never did.
  /// </summary>
  [Test]
  public async Task AfterCall_CheapCallsAfterSlowOnes_GrowPastTheSizeTheSlowOnesBackedOffToAsync() {
    var sizer = new CommitOrderStampSizer(CONFIGURED, CEILING);
    sizer.AfterCall(stamped: 1_000, took: TimeSpan.FromSeconds(10), budget: _budget);
    sizer.AfterCall(stamped: 500, took: TimeSpan.FromSeconds(5), budget: _budget);

    for (var call = 0; call < 3; call++) {
      sizer.AfterCall(stamped: sizer.Size, took: _cheap, budget: _budget);
    }

    await Assert.That(sizer.Size).IsEqualTo(2_000)
      .Because("250 grew to 500, 1,000 and 2,000 on three calls well inside the budget");
  }

  /// <summary>
  /// #1786: a timeout halves the batch (#1217), and cheap calls afterwards grow it again rather than holding it at the
  /// halved size for the rest of the backlog.
  /// </summary>
  [Test]
  public async Task AfterCall_CheapCallsAfterATimeout_GrowAgainAsync() {
    var sizer = new CommitOrderStampSizer(CONFIGURED, CEILING);
    sizer.AfterTimeout();

    sizer.AfterCall(stamped: 500, took: _cheap, budget: _budget);
    sizer.AfterCall(stamped: 1_000, took: _cheap, budget: _budget);

    await Assert.That(sizer.Size).IsEqualTo(2_000)
      .Because("a call at half the size that timed out ran in a twenty-fifth of the budget, so whatever made the "
        + "larger call time out is gone");
  }

  /// <summary>
  /// #1786: the size where the cost stopped changing belongs to the conditions that measured it. A drained backlog, a
  /// timeout or a cheap call ends it, and a later slow call is halved from wherever it is.
  /// </summary>
  [Test]
  [Arguments("a drained backlog")]
  [Arguments("a timeout")]
  [Arguments("a cheap call")]
  public async Task AfterCall_TheFloorEndsWithTheConditionsThatSetItAsync(string ending) {
    var sizer = new CommitOrderStampSizer(CONFIGURED, CEILING);
    sizer.AfterCall(stamped: 1_000, took: TimeSpan.FromMilliseconds(3_000), budget: _budget);
    sizer.AfterCall(stamped: 500, took: TimeSpan.FromMilliseconds(2_900), budget: _budget);

    switch (ending) {
      case "a drained backlog":
        sizer.AfterCall(stamped: 7, took: _cheap, budget: _budget);
        break;
      case "a timeout":
        sizer.AfterTimeout();
        sizer.AfterTimeout();
        break;
      default:
        sizer.AfterCall(stamped: 500, took: _cheap, budget: _budget);
        break;
    }
    var before = sizer.Size;
    sizer.AfterCall(stamped: before, took: TimeSpan.FromSeconds(10), budget: _budget);

    await Assert.That(sizer.Size).IsEqualTo(before / 2)
      .Because($"after {ending}, nothing compares this slow call with the ones before it, so it is halved");
  }

  /// <summary>The cost the deployed stamper showed: about 2.5 s for any call, and 7.2 ms more for every row.</summary>
  private static TimeSpan _fixedPlusPerRow(int rows) =>
    TimeSpan.FromMilliseconds(2_500) + TimeSpan.FromMicroseconds(7_200L * rows);

  [Test]
  public async Task AfterTimeout_HalvesAsync() {
    var sizer = new CommitOrderStampSizer(CONFIGURED, CEILING);

    sizer.AfterTimeout();

    await Assert.That(sizer.Size).IsEqualTo(500)
      .Because("a call that timed out rolled back, so the same size cannot succeed (#1217)");
  }
}
