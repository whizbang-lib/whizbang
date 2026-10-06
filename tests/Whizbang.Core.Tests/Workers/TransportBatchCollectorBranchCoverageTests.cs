// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The final flush that <see cref="TransportBatchCollector{T}.DisposeAsync"/> performs after its
/// timers are gone: with nobody subscribed to the flush notification, and with a flush that fails.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportBatchCollector.cs</code-under-test>
[Category("Workers")]
public class TransportBatchCollectorBranchCoverageTests {

  // Size and timers far out of reach, so only the dispose-time flush can deliver the batch.
  private static TransportBatchOptions _neverOnItsOwn() => new() { BatchSize = 1000, SlideMs = 60_000, MaxWaitMs = 120_000 };

  [Test]
  public async Task DisposeAsync_NoFlushSubscriber_StillDeliversThePendingBatchAsync() {
    var delivered = new List<int>();
    var collector = new TransportBatchCollector<int>(_neverOnItsOwn(), batch => {
      delivered.AddRange(batch);
      return Task.CompletedTask;
    });

    collector.Enqueue(1);
    collector.Enqueue(2);
    await collector.DisposeAsync();

    await Assert.That(delivered).IsEquivalentTo([1, 2])
      .Because("the flush notification is optional; a collector nobody observes must still flush on dispose");
  }

  [Test]
  public async Task DisposeAsync_FinalFlushFails_SurfacesTheFailureInsteadOfReArmingAStoppedTimerAsync() {
    // A failed flush normally re-queues the batch and re-arms the slide timer for a retry. At dispose
    // the timers are already gone, so there is no retry to schedule: the failure goes to the caller.
    var attempts = 0;
    var collector = new TransportBatchCollector<int>(_neverOnItsOwn(), _ => {
      Interlocked.Increment(ref attempts);
      throw new InvalidOperationException("broker unavailable");
    });
    var flushedEvents = 0;
    collector.OnBatchFlushed += _ => Interlocked.Increment(ref flushedEvents);

    collector.Enqueue(7);

    await Assert.That(async () => await collector.DisposeAsync())
      .Throws<InvalidOperationException>()
      .Because("with no timer left to retry on, the final flush's failure must reach the disposer");
    await Assert.That(Volatile.Read(ref attempts)).IsEqualTo(1);
    await Assert.That(Volatile.Read(ref flushedEvents)).IsEqualTo(0)
      .Because("a failed flush is not reported as flushed");
  }
}
