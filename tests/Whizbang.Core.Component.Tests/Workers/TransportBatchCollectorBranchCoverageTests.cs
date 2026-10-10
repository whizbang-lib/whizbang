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

  [Test]
  public async Task DisposeAsync_WithFlushSubscriber_ReportsTheFlushedBatchSizeAsync() {
    // The other dispose tests either have no subscriber or a flush that fails, so the notification
    // is never raised to anyone. A successful flush must report its size to the subscriber.
    var reported = new List<int>();
    var collector = new TransportBatchCollector<int>(_neverOnItsOwn(), _ => Task.CompletedTask);
    collector.OnBatchFlushed += count => { lock (reported) { reported.Add(count); } };

    collector.Enqueue(1);
    collector.Enqueue(2);
    collector.Enqueue(3);
    await collector.DisposeAsync();

    List<int> observed;
    lock (reported) { observed = [.. reported]; }
    await Assert.That(observed).IsEquivalentTo([3])
      .Because("a successful flush reports exactly once, with the number of messages it delivered");
  }

  [Test]
  public async Task FlushFailsWhileRunning_ReArmsTheSlideTimerAndRetriesTheBatchAsync() {
    // Every other failing-flush test fails at dispose, when the slide timer is already gone. While the
    // collector is running, a failed flush re-queues the batch and re-arms the slide timer, and that
    // timer's tick is what delivers the retry.
    var attempts = 0;
    var delivered = new List<int>();
    var retried = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    // Size 1 flushes on the enqueue itself; the short slide only matters once the failure re-arms it,
    // and the hard max is far out of reach so only the re-armed slide timer can deliver the retry.
    await using var collector = new TransportBatchCollector<int>(
      new TransportBatchOptions { BatchSize = 1, SlideMs = 1, MaxWaitMs = 600_000 },
      batch => {
        if (Interlocked.Increment(ref attempts) == 1) {
          return Task.FromException(new InvalidOperationException("broker unavailable"));
        }
        lock (delivered) { delivered.AddRange(batch); }
        return Task.CompletedTask;
      });
    collector.OnBatchFlushed += count => retried.TrySetResult(count);

    collector.Enqueue(42);

    var flushedCount = await retried.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(flushedCount).IsEqualTo(1);
    await Assert.That(Volatile.Read(ref attempts)).IsEqualTo(2)
      .Because("the first attempt failed and the re-armed slide timer drove exactly one retry");
    List<int> observed;
    lock (delivered) { observed = [.. delivered]; }
    await Assert.That(observed).IsEquivalentTo([42])
      .Because("the failed batch is kept and delivered by the retry rather than dropped");
  }
}
