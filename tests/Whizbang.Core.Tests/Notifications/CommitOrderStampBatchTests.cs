// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Unit tests for <see cref="CommitOrderStampBatch"/>: how large the next stamp call asks for, given
/// what the last one returned (#1060). Each call costs a scan of the whole pending set, so the number of
/// calls is what a drain's cost is made of.
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
/// <tests>Whizbang.Core/Notifications/CommitOrderStampBatch.cs</tests>
public class CommitOrderStampBatchTests {
  private const int CONFIGURED = 1_000;
  private const int CEILING = 25_000;

  /// <summary>The sizes a drain climbs through, so the expectation is not a constant array argument.</summary>
  private static readonly int[] _climb = [2_000, 4_000, 8_000, 16_000, 25_000, 25_000, 25_000, 25_000];

  [Test]
  public async Task Next_AFullBatch_GrowsAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(1_000, 1_000, CONFIGURED, CEILING)).IsEqualTo(2_000)
      .Because("a call that filled its limit left rows behind, and the next scan should carry more of "
        + "them, since the scan costs the same either way");

  [Test]
  public async Task Next_AShortBatch_ReturnsToTheConfiguredSizeAsync() {
    await Assert.That(CommitOrderStampBatch.Next(16_000, 9, CONFIGURED, CEILING)).IsEqualTo(CONFIGURED)
      .Because("fewer rows than asked for means the pending set is drained to the fence, so latency "
        + "matters again and a large batch only delays the next stamp");
    await Assert.That(CommitOrderStampBatch.Next(16_000, 0, CONFIGURED, CEILING)).IsEqualTo(CONFIGURED)
      .Because("zero is the fenced case: nothing could be stamped, and growing would not help");
  }

  [Test]
  public async Task Next_GrowthStopsAtTheCeilingAsync() {
    await Assert.That(CommitOrderStampBatch.Next(16_000, 16_000, CONFIGURED, CEILING)).IsEqualTo(CEILING)
      .Because("doubling past the ceiling would overshoot it; the step is clamped, not skipped");
    await Assert.That(CommitOrderStampBatch.Next(CEILING, CEILING, CONFIGURED, CEILING)).IsEqualTo(CEILING)
      .Because("at the ceiling a full batch keeps the ceiling: a batch holds its rows locked for the "
        + "length of its call, so the bound exists to be respected rather than approached");
  }

  [Test]
  public async Task Next_ClimbsFromTheConfiguredSizeToTheCeilingInAFewCallsAsync() {
    // The point of the change, as a sequence: a drain reaches the ceiling quickly rather than spending
    // its scans on small batches.
    var sizes = new List<int>();
    var size = CONFIGURED;
    for (var i = 0; i < 8; i++) {
      size = CommitOrderStampBatch.Next(size, size, CONFIGURED, CEILING);
      sizes.Add(size);
    }

    await Assert.That(sizes).IsEquivalentTo(_climb)
      .Because("five calls reach the ceiling, so a four-million row backlog costs on the order of "
        + "hundreds of scans rather than thousands");
  }

  [Test]
  public async Task Next_ACeilingBelowTheConfiguredSize_HonorsTheConfiguredSizeAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(5_000, 5_000, 5_000, 1_000)).IsEqualTo(5_000)
      .Because("raising the steady-state size past a ceiling nobody revisited must not silently shrink "
        + "every call to the stale ceiling");

  [Test]
  public async Task Next_DoesNotOverflowOnAHugeCurrentAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(int.MaxValue, int.MaxValue, CONFIGURED, CEILING))
      .IsEqualTo(CEILING)
      .Because("the growth is computed wide and clamped, so a size near the maximum cannot wrap to a "
        + "negative batch — which the database would reject rather than ignore");

  // ------------------------------------------------------------------
  // #1217: a call that times out is canceled and stamps nothing, so the same size can never succeed.

  /// <summary>The sizes a backoff from the ceiling falls through, so the expectation is not a constant array argument.</summary>
  private static readonly int[] _backoff = [12_500, 6_250, 3_125, 1_562, 781, 390, 195, 97, 48, 24, 12, 6, 3, 1, 1];

  [Test]
  public async Task AfterTimeout_HalvesTheBatchAsync() =>
    await Assert.That(CommitOrderStampBatch.AfterTimeout(CEILING)).IsEqualTo(12_500)
      .Because("a canceled call rolls back everything it did, so retrying the same size repeats the same timeout; "
        + "on a deployed environment one stamper did that every 30 s for ten hours after an import");

  [Test]
  public async Task AfterTimeout_FallsBelowTheConfiguredSizeAndStopsAtOneAsync() {
    var sizes = new List<int>();
    var size = CEILING;
    for (var i = 0; i < _backoff.Length; i++) {
      size = CommitOrderStampBatch.AfterTimeout(size);
      sizes.Add(size);
    }

    await Assert.That(sizes).IsEquivalentTo(_backoff)
      .Because("the backoff looks for a size that completes, so it passes the configured size when it must, and a "
        + "batch of one is the smallest call that can still make progress");
  }

  [Test]
  public async Task Next_AfterATimeout_DoesNotGrowBackToTheSizeThatTimedOutAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(12_500, 12_500, CONFIGURED, CEILING, timeoutCap: 12_500))
      .IsEqualTo(12_500)
      .Because("the size that timed out was twice this one, so doubling back to it would time out again and cost "
        + "another full timeout on every climb");

  [Test]
  public async Task Next_AfterATimeoutBelowTheConfiguredSize_StaysBelowItAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(500, 500, CONFIGURED, CEILING, timeoutCap: 500)).IsEqualTo(500)
      .Because("when even the configured size timed out, a full batch must not climb back to it");

  [Test]
  public async Task Next_ACapAboveTheGrowth_DoesNotHoldItBackAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(1_000, 1_000, CONFIGURED, CEILING, timeoutCap: 12_500))
      .IsEqualTo(2_000)
      .Because("the cap bounds the climb, it does not set its pace");

  [Test]
  public async Task Next_AShortBatchAfterATimeout_ReturnsToTheConfiguredSizeAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(500, 3, CONFIGURED, CEILING, timeoutCap: 500)).IsEqualTo(CONFIGURED)
      .Because("a short batch means the backlog is drained to the fence; the next burst starts at the configured "
        + "size, and the caller drops the cap with it");

  private static readonly TimeSpan _budget = TimeSpan.FromMilliseconds(2_500);

  [Test]
  public async Task Next_AFullBatchThatRanPastTheBudget_HalvesAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(16_000, 16_000, CONFIGURED, CEILING, timeoutCap: null,
        took: TimeSpan.FromSeconds(10), budget: _budget))
      .IsEqualTo(8_000)
      .Because("a call holds the role row for as long as it runs, and the holder renews only between calls; a "
        + "drain whose calls outlasted the lease lost the role to its own lapse four times in two minutes (#1327)");

  [Test]
  public async Task Next_AFullBatchPastHalfTheBudget_HoldsItsSizeAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(8_000, 8_000, CONFIGURED, CEILING, timeoutCap: null,
        took: TimeSpan.FromMilliseconds(1_500), budget: _budget))
      .IsEqualTo(8_000)
      .Because("doubling a call that took more than half the budget would take about twice as long, past it");

  [Test]
  public async Task Next_AFullBatchWellInsideTheBudget_GrowsAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(8_000, 8_000, CONFIGURED, CEILING, timeoutCap: null,
        took: TimeSpan.FromSeconds(1), budget: _budget))
      .IsEqualTo(16_000)
      .Because("a call that doubled would still finish inside the budget, so the drain pays the fixed cost fewer times");

  [Test]
  public async Task Next_AFullBatchExactlyAtTheBudget_HoldsItsSizeAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(8_000, 8_000, CONFIGURED, CEILING, timeoutCap: null,
        took: _budget, budget: _budget))
      .IsEqualTo(8_000)
      .Because("the budget is the most a call may take, so a call that took exactly that is not shrunk");

  [Test]
  public async Task Next_AShortBatchThatRanLong_ReturnsToTheConfiguredSizeAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(16_000, 9, CONFIGURED, CEILING, timeoutCap: null,
        took: TimeSpan.FromSeconds(10), budget: _budget))
      .IsEqualTo(CONFIGURED)
      .Because("a short batch means the backlog is drained; the next burst starts at the configured size whatever "
        + "the last call cost");

  [Test]
  public async Task Next_ABatchOfOneThatRanLong_StaysAtOneAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(1, 1, CONFIGURED, CEILING, timeoutCap: null,
        took: TimeSpan.FromSeconds(10), budget: _budget))
      .IsEqualTo(1)
      .Because("one row is the smallest call that still makes progress");

  [Test]
  public async Task Next_ARunLongCallWithinTheCap_StillGrowsNoFurtherThanTheCapAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(1_000, 1_000, CONFIGURED, CEILING, timeoutCap: 1_000,
        took: TimeSpan.FromMilliseconds(100), budget: _budget))
      .IsEqualTo(1_000)
      .Because("a fast call does not lift the cap an earlier slow or timed-out call set; only a drained set does");

  [Test]
  public async Task DefaultCallBudget_IsHalfTheDefaultRenewalIntervalAsync() =>
    await Assert.That(CommitOrderStampBatch.DefaultCallBudget)
      .IsEqualTo(new Whizbang.Core.Startup.RoleAssignmentOptions().RenewInterval / 2)
      .Because("a holder renews its lease only between calls, so calls within half the renewal interval keep every "
        + "gap between renewals well inside the lease");
}
