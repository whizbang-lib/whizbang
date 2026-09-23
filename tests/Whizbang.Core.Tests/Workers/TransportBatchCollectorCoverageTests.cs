using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tail-of-round coverage for <see cref="TransportBatchCollector{T}"/>'s double-dispose guard.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportBatchCollector.cs</code-under-test>
[Category("Workers")]
public class TransportBatchCollectorCoverageTests {

  /// <summary>
  /// A second <c>DisposeAsync</c> must be a no-op: without the guard it would re-flush whatever
  /// pending messages a concurrent enqueue had added between the two calls, delivering the same
  /// message twice to the downstream inbox insert.
  /// </summary>
  [Test]
  public async Task DisposeAsync_CalledTwice_OnlyFlushesOnceAsync() {
    var flushCount = 0;
    var flushedCounts = new List<int>();
    var collector = new TransportBatchCollector<int>(
      new TransportBatchOptions { BatchSize = 1000, SlideMs = 5000, MaxWaitMs = 10000 },
      batch => {
        Interlocked.Increment(ref flushCount);
        flushedCounts.Add(batch.Count);
        return Task.CompletedTask;
      });

    collector.Enqueue(1);
    collector.Enqueue(2);

    await collector.DisposeAsync();
    await collector.DisposeAsync();

    await Assert.That(flushCount).IsEqualTo(1)
      .Because("a second dispose must not re-flush the batch the first dispose already delivered");
    await Assert.That(flushedCounts[0]).IsEqualTo(2);
  }

  /// <summary>
  /// Target: <c>TransportBatchCollector.Enqueue</c>'s disposed guard.
  /// </summary>
  /// <remarks>
  /// A transport's receive handlers keep delivering for a short while after shutdown has begun —
  /// the broker's own in-flight callbacks do not stop the instant the host does. Those late
  /// enqueues must be dropped rather than accepted, because the timers that would have flushed
  /// them are already disposed: a message added to <c>_pending</c> after the final dispose flush
  /// has nothing left to deliver it, so it is silently lost while the broker has been told the
  /// collector took it. Dropping it instead leaves the broker's own redelivery to recover it.
  /// </remarks>
  [Test]
  public async Task Enqueue_AfterDispose_IsDroppedRatherThanBufferedWithNothingToFlushItAsync() {
    var flushedBatches = new List<int[]>();
    var gate = new Lock();
    var collector = new TransportBatchCollector<int>(
      new TransportBatchOptions { BatchSize = 1000, SlideMs = 5000, MaxWaitMs = 10000 },
      batch => {
        lock (gate) {
          flushedBatches.Add([.. batch]);
        }
        return Task.CompletedTask;
      });

    collector.Enqueue(1);
    await collector.DisposeAsync();

    // The broker's in-flight handler arriving after the host has torn the collector down.
    collector.Enqueue(2);
    // A second dispose is a no-op, so this cannot flush anything either — proving 2 was never
    // buffered rather than merely not flushed yet.
    await collector.DisposeAsync();

    int[][] observed;
    lock (gate) {
      observed = [.. flushedBatches];
    }

    await Assert.That(observed.Length).IsEqualTo(1)
      .Because("only the pre-dispose batch may be delivered");
    await Assert.That(observed[0]).IsEquivalentTo([1])
      .Because("a message enqueued after disposal has no timer left to flush it — accepting it "
             + "would swallow the message while the broker considers it handed over");
  }

  /// <summary>
  /// A slide tick that lands after disposal must not start another flush. The collector disposes
  /// its timers before the final flush, but a callback already dispatched by the runtime is
  /// past that point and arrives anyway; if it re-entered the flush it would retry a batch the
  /// owner has already stopped caring about, against a transport the owner is shutting down.
  /// </summary>
  /// <remarks>
  /// The failing first flush is what makes the disposed answer observable: it pushes the batch
  /// back into pending, so a tick that ran would have a real batch to deliver and the flush
  /// count would move. Without that setup the pending list is empty and a tick that ran would
  /// look exactly like one that did not.
  /// </remarks>
  [Test]
  public async Task SlideTimerTick_AfterDispose_DoesNotRetryTheFailedBatchAsync() {
    var flushAttempts = 0;
    var collector = new TransportBatchCollector<int>(
      new TransportBatchOptions { BatchSize = 1000, SlideMs = 5000, MaxWaitMs = 10000 },
      _ => {
        Interlocked.Increment(ref flushAttempts);
        return Task.FromException(new InvalidOperationException("transport refused the batch"));
      });

    collector.Enqueue(1);

    // The dispose-time flush is the first attempt, and it fails, so the batch goes back to pending.
    await Assert.That(async () => await collector.DisposeAsync())
      .Throws<InvalidOperationException>()
      .Because("a flush that the transport refuses has to surface; swallowing it here would hide "
        + "the very state this test then depends on");
    await Assert.That(Volatile.Read(ref flushAttempts)).IsEqualTo(1)
      .Because("exactly one attempt so far — the one dispose made");

    collector.SlideTimerTick(null);

    await Assert.That(Volatile.Read(ref flushAttempts)).IsEqualTo(1)
      .Because("the tick returned at the disposed guard; had it gone on, the re-queued batch would "
        + "have been handed to the transport a second time after the owner shut the collector down");
  }
}
