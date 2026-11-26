// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The cursors the startup rewind scan found flagged (#2214), which the read-model barrier waits on. Only those
/// cursors: a key leaves the set when this worker rewinds it or a keyed re-read finds it no longer flagged, and
/// nothing flagged later ever joins it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/StartupRewindSet.cs</code-under-test>
[Category("Core")]
[Category("Workers")]
public class StartupRewindSetTests {
  private const string ORDERS = "Test.Orders";
  private const string INVOICES = "Test.Invoices";

  private static RewindCursorInfo _flagged(Guid streamId, string perspective) =>
    new(streamId, perspective, LastEventId: null, RewindTriggerEventId: Guid.CreateVersion7());

  private static PerspectiveCursorInfo _cursor(Guid streamId, string perspective, PerspectiveProcessingStatus status) =>
    new() { StreamId = streamId, PerspectiveName = perspective, Status = status };

  [Test]
  public async Task Construction_CountsDistinctKeysAndStreams_AndIsNotClearedAsync() {
    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();
    var set = new StartupRewindSet([_flagged(a, ORDERS), _flagged(a, INVOICES), _flagged(b, ORDERS), _flagged(b, ORDERS)]);

    await Assert.That(set.ScanTimeCount).IsEqualTo(3).Because("a key the scan reported twice is one cursor");
    await Assert.That(set.StreamCount).IsEqualTo(2);
    await Assert.That(set.RemainingCount).IsEqualTo(3);
    await Assert.That(set.ClearedCount).IsEqualTo(0);
    await Assert.That(set.AllCleared.IsCompleted).IsFalse();
  }

  [Test]
  public async Task Construction_WithNothingFlagged_IsClearedAtOnceAsync() {
    var set = new StartupRewindSet([]);

    await Assert.That(set.AllCleared.IsCompleted).IsTrue()
      .Because("an empty set has nothing to wait for, so a waiter on it must never hang");
  }

  [Test]
  public async Task MarkRewound_Completed_RemovesTheKey_AndTheLastOneClearsTheSetAsync() {
    var a = Guid.CreateVersion7();
    var set = new StartupRewindSet([_flagged(a, ORDERS), _flagged(a, INVOICES)]);

    await Assert.That(set.MarkRewound(a, ORDERS, PerspectiveProcessingStatus.Completed)).IsTrue();
    await Assert.That(set.AllCleared.IsCompleted).IsFalse().Because("one scan-time key is still flagged");
    await Assert.That(set.MarkRewound(a, INVOICES, PerspectiveProcessingStatus.Completed)).IsTrue();

    await Assert.That(set.AllCleared.IsCompleted).IsTrue();
    await Assert.That(set.ClearedCount).IsEqualTo(2);
  }

  [Test]
  public async Task MarkRewound_ForAKeyTheScanDidNotFlag_IsIgnoredAsync() {
    var a = Guid.CreateVersion7();
    var set = new StartupRewindSet([_flagged(a, ORDERS)]);

    await Assert.That(set.MarkRewound(Guid.CreateVersion7(), ORDERS, PerspectiveProcessingStatus.Completed)).IsFalse()
      .Because("a rewind flagged after the scan is not part of what the barrier waits on");
    await Assert.That(set.MarkRewound(a, ORDERS, PerspectiveProcessingStatus.Completed)).IsTrue();
    await Assert.That(set.MarkRewound(a, ORDERS, PerspectiveProcessingStatus.Completed)).IsFalse()
      .Because("a key leaves the set once");
  }

  [Test]
  public async Task MarkRewound_ThatDidNotComplete_LeavesTheKeyAsync() {
    var a = Guid.CreateVersion7();
    var set = new StartupRewindSet([_flagged(a, ORDERS)]);

    await Assert.That(set.MarkRewound(a, ORDERS, PerspectiveProcessingStatus.Failed)).IsFalse();
    await Assert.That(set.MarkRewound(a, ORDERS, PerspectiveProcessingStatus.Completed | PerspectiveProcessingStatus.RewindRequired)).IsFalse()
      .Because("a rewind that left the cursor flagged has not repaired it");

    await Assert.That(set.RemainingCount).IsEqualTo(1);
    await Assert.That(set.AllCleared.IsCompleted).IsFalse();
  }

  [Test]
  public async Task RemainingStreamIds_NamesEachRemainingStreamOnceAsync() {
    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();
    var set = new StartupRewindSet([_flagged(a, ORDERS), _flagged(a, INVOICES), _flagged(b, ORDERS)]);
    set.MarkRewound(b, ORDERS, PerspectiveProcessingStatus.Completed);

    await Assert.That(set.RemainingStreamIds()).IsEquivalentTo([a])
      .Because("the keyed re-read asks only for streams that still hold a scan-time key");
  }

  [Test]
  public async Task ClearUnflagged_RemovesKeysNoLongerFlaggedOrGone_KeepsFlagged_AndIgnoresNewFlagsAsync() {
    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();
    var c = Guid.CreateVersion7();
    var set = new StartupRewindSet([_flagged(a, ORDERS), _flagged(b, ORDERS), _flagged(c, ORDERS)]);

    set.ClearUnflagged([
      _cursor(a, ORDERS, PerspectiveProcessingStatus.RewindRequired),   // still flagged: stays
      _cursor(b, ORDERS, PerspectiveProcessingStatus.Completed),        // cleared
      _cursor(a, INVOICES, PerspectiveProcessingStatus.RewindRequired), // flagged after the scan: never joins
      // c has no cursor any more: its stream was purged, so there is nothing left to repair
    ]);

    await Assert.That(set.RemainingCount).IsEqualTo(1);
    await Assert.That(set.RemainingStreamIds()).IsEquivalentTo([a]);
    await Assert.That(set.AllCleared.IsCompleted).IsFalse();

    set.ClearUnflagged([_cursor(a, ORDERS, PerspectiveProcessingStatus.Completed)]);

    await Assert.That(set.AllCleared.IsCompleted).IsTrue();
  }

  [Test]
  public async Task DescribeRemaining_ListsStreamAndPerspective_UpToTheLimitAsync() {
    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();
    var c = Guid.CreateVersion7();
    var set = new StartupRewindSet([_flagged(a, ORDERS), _flagged(b, ORDERS), _flagged(c, INVOICES)]);

    var all = set.DescribeRemaining(10);
    await Assert.That(all).Contains($"{a}/{ORDERS}");
    await Assert.That(all).Contains($"{b}/{ORDERS}");
    await Assert.That(all).Contains($"{c}/{INVOICES}");
    await Assert.That(all).DoesNotContain("more");

    var some = set.DescribeRemaining(2);
    await Assert.That(some).EndsWith("and 1 more");
  }

  /// <summary>
  /// #2601: the cap's Warning says how often the queued row of a cursor still rewinding was leased, and how often it
  /// failed: a row leased many times with no failure is one nothing ever runs. Leases recorded for a key not in the set are
  /// ignored, and the highest count recorded is the one named.
  /// </summary>
  [Test]
  public async Task DescribeRemaining_NamesHowOftenTheQueuedRowWasLeased_ForAKeyThatRecordedLeasesAsync() {
    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();
    var set = new StartupRewindSet([_flagged(a, ORDERS), _flagged(b, ORDERS)]);

    set.RecordLeases(a, ORDERS, attempts: 60, failures: 0);
    set.RecordLeases(a, ORDERS, attempts: 62, failures: 0);
    set.RecordLeases(a, ORDERS, attempts: 61, failures: 0);
    set.RecordLeases(Guid.CreateVersion7(), INVOICES, attempts: 9, failures: 0);

    var described = set.DescribeRemaining(10);
    await Assert.That(described).Contains($"{a}/{ORDERS} (queued row leased 62 times, 0 failures)");
    await Assert.That(described).Contains($"{b}/{ORDERS}");
    await Assert.That(described).DoesNotContain($"{b}/{ORDERS} (");
    await Assert.That(described).DoesNotContain(INVOICES);
  }
}
