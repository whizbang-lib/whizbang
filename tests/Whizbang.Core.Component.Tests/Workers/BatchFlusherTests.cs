// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coalescing and shutdown of <see cref="BatchFlusher{T}"/>. Every flusher here runs on a fake clock (#1868): the
/// coalesce window and the shutdown drain bound are timers on that clock, so a window closes only when the test moves
/// time, and only after the test has seen that timer armed. No test waits on real time.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/BatchFlusher.cs</code-under-test>
[NotInParallel(Order = 100)]
public class BatchFlusherTests {

  [Test]
  [Timeout(30000)]
  public async Task SingleItem_FlushesAfterCoalesceWindowAsync(CancellationToken cancellationToken) {
    var batchTcs = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var clock = new ArmingClock();
    var window = TimeSpan.FromMilliseconds(25);
    await using var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        batchTcs.TrySetResult(items);
        return Task.CompletedTask;
      },
      options: new BatchFlusherOptions {
        CoalesceWindowMs = 25,
        MaxBatchSize = 100,
        ImmediateFlushThreshold = 50
      },
      logger: NullLogger.Instance,
      timeProvider: clock);

    await flusher.Writer.WriteAsync(42, cancellationToken);

    // The flusher holds the item while the window is open: the window is a timer on the flusher's clock.
    await clock.WaitForArmedAsync(window, cancellationToken);
    await Assert.That(batchTcs.Task.IsCompleted).IsFalse()
      .Because("a lone item waits for company until its coalesce window closes");

    clock.Advance(window);

    var batch = await batchTcs.Task.WaitAsync(cancellationToken);
    await Assert.That(batch.Count).IsEqualTo(1);
    await Assert.That(batch[0]).IsEqualTo(42);
  }

  [Test]
  [Timeout(30000)]
  public async Task ItemsArrivingWithinTheWindow_JoinTheBatchUntilTheWindowClosesAsync(CancellationToken cancellationToken) {
    // Both items are written before the window can close, and the threshold is out of reach, so the batch takes
    // everything waiting, finds the channel empty, waits again, and ends only when the clock closes the window.
    var batchTcs = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var clock = new ArmingClock();
    var window = TimeSpan.FromMilliseconds(25);
    await using var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        batchTcs.TrySetResult(items);
        return Task.CompletedTask;
      },
      options: new BatchFlusherOptions { CoalesceWindowMs = 25, MaxBatchSize = 100, ImmediateFlushThreshold = 10 },
      logger: NullLogger.Instance,
      timeProvider: clock);

    await flusher.Writer.WriteAsync(1, cancellationToken);
    await flusher.Writer.WriteAsync(2, cancellationToken);
    await clock.WaitForArmedAsync(window, cancellationToken);
    clock.Advance(window);

    var batch = await batchTcs.Task.WaitAsync(cancellationToken);
    await Assert.That(batch).IsEquivalentTo([1, 2])
      .Because("an item written while the window is open joins the batch the window is holding");
  }

  [Test]
  [Timeout(30000)]
  public async Task ImmediateThresholdReached_FlushesEarlyAsync(CancellationToken cancellationToken) {
    var batches = new List<IReadOnlyList<int>>();
    var twoBatches = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        lock (batches) {
          batches.Add([.. items]);
          if (batches.Count == 2) {
            twoBatches.TrySetResult();
          }
        }
        return Task.CompletedTask;
      },
      options: new BatchFlusherOptions {
        CoalesceWindowMs = 5_000,             // The clock never moves, so the window never closes
        MaxBatchSize = 100,
        ImmediateFlushThreshold = 5
      },
      logger: NullLogger.Instance,
      timeProvider: new ArmingClock());

    for (var i = 0; i < 10; i++) {
      await flusher.Writer.WriteAsync(i, cancellationToken);
    }

    await twoBatches.Task.WaitAsync(cancellationToken);
    List<IReadOnlyList<int>> seen;
    lock (batches) { seen = [.. batches]; }
    await Assert.That(seen[0]).IsEquivalentTo([0, 1, 2, 3, 4])
      .Because("with the window held open, only the threshold can end a batch, and it ends it at exactly five");
    await Assert.That(seen[1]).IsEquivalentTo([5, 6, 7, 8, 9]);
  }

  [Test]
  [Timeout(30000)]
  public async Task MaxBatchSizeRespectedAsync(CancellationToken cancellationToken) {
    var batchTcs = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        batchTcs.TrySetResult(items);
        return Task.CompletedTask;
      },
      options: new BatchFlusherOptions {
        CoalesceWindowMs = 5_000,
        MaxBatchSize = 3,
        ImmediateFlushThreshold = 100  // never via threshold; only via cap
      },
      logger: NullLogger.Instance,
      timeProvider: new ArmingClock());

    for (var i = 0; i < 10; i++) {
      await flusher.Writer.WriteAsync(i, cancellationToken);
    }

    var batch = await batchTcs.Task.WaitAsync(cancellationToken);
    await Assert.That(batch.Count).IsEqualTo(3)
      .Because("the window never closes on a clock that never moves, so the cap is what ends the batch");
  }

  [Test]
  [Timeout(30000)]
  public async Task FirstItemAtTheImmediateThreshold_FlushesWithoutOpeningAWindowAsync(CancellationToken cancellationToken) {
    // A threshold of one is reached by the first item itself, so there is nothing to wait for. The flusher used to
    // open the window anyway, because it checked the threshold only on items read after the first.
    var batchTcs = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var clock = new ArmingClock();
    await using var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        batchTcs.TrySetResult(items);
        return Task.CompletedTask;
      },
      options: new BatchFlusherOptions { CoalesceWindowMs = 5_000, MaxBatchSize = 100, ImmediateFlushThreshold = 1 },
      logger: NullLogger.Instance,
      timeProvider: clock);

    await flusher.Writer.WriteAsync(7, cancellationToken);

    var batch = await batchTcs.Task.WaitAsync(cancellationToken);
    await Assert.That(batch).IsEquivalentTo([7]);
    await Assert.That(clock.DrainArmed()).IsEmpty()
      .Because("a batch already at its threshold flushes at once, without arming a coalesce window");
  }

  [Test]
  [Timeout(30000)]
  public async Task NoCoalesceWindow_FlushesTheFirstItemWithoutWaitingAsync(CancellationToken cancellationToken) {
    var batchTcs = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var clock = new ArmingClock();
    await using var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        batchTcs.TrySetResult(items);
        return Task.CompletedTask;
      },
      options: new BatchFlusherOptions { CoalesceWindowMs = 0, MaxBatchSize = 100, ImmediateFlushThreshold = 50 },
      logger: NullLogger.Instance,
      timeProvider: clock);

    await flusher.Writer.WriteAsync(8, cancellationToken);

    var batch = await batchTcs.Task.WaitAsync(cancellationToken);
    await Assert.That(batch).IsEquivalentTo([8]);
    await Assert.That(clock.DrainArmed()).IsEmpty()
      .Because("a window of zero is no window: the item is flushed without arming a timer");
  }

  [Test]
  [Timeout(30000)]
  public async Task DisposeAsync_WhileAWindowIsOpen_FlushesWithoutWaitingForTheWindowAsync(CancellationToken cancellationToken) {
    // Shutdown completes the writer; a batch waiting for company will get none, so it is flushed at once instead of
    // holding host shutdown for the rest of its window.
    var flushed = new List<int>();
    var clock = new ArmingClock();
    var window = TimeSpan.FromMilliseconds(600_000);
    var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        lock (flushed) { flushed.AddRange(items); }
        return Task.CompletedTask;
      },
      options: new BatchFlusherOptions { CoalesceWindowMs = 600_000, MaxBatchSize = 100, ImmediateFlushThreshold = 50 },
      logger: NullLogger.Instance,
      timeProvider: clock);

    await flusher.Writer.WriteAsync(9, cancellationToken);
    await clock.WaitForArmedAsync(window, cancellationToken);

    await flusher.DisposeAsync();

    List<int> seen;
    lock (flushed) { seen = [.. flushed]; }
    await Assert.That(seen).IsEquivalentTo([9])
      .Because("the item was flushed by the drain while its window was still open on a clock that never moved");
  }

  [Test]
  public async Task Constructor_WithoutAClock_RunsOnTheSystemClockAsync() {
    // The clock parameter is optional: a flusher built without one keeps the system clock, so existing callers are
    // unchanged. Nothing here waits on it; disposing drains what was written.
    var flushed = new List<int>();
    var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        lock (flushed) { flushed.AddRange(items); }
        return Task.CompletedTask;
      },
      options: new BatchFlusherOptions { CoalesceWindowMs = 0, MaxBatchSize = 100, ImmediateFlushThreshold = 50 },
      logger: NullLogger.Instance,
      timeProvider: null);

    await flusher.Writer.WriteAsync(3);
    await flusher.DisposeAsync();

    List<int> seen;
    lock (flushed) { seen = [.. flushed]; }
    await Assert.That(seen).IsEquivalentTo([3]);
  }

  [Test]
  public async Task DisposeAsync_CompletesLoopAsync() {
    var flusher = new BatchFlusher<int>(
      flush: (_, _) => Task.CompletedTask,
      options: new BatchFlusherOptions(),
      logger: NullLogger.Instance,
      timeProvider: new ArmingClock());

    await flusher.DisposeAsync();
    await Assert.That(flusher.StoppedSignal.IsCompletedSuccessfully).IsTrue();
  }

  /// <summary>
  /// Host teardown disposes the flusher through more than one path (the owning worker's StopAsync
  /// AND container disposal). The second call must be a no-op — cancelling the already-disposed
  /// stop CTS throws ObjectDisposedException and nondeterministically fails whatever test happens
  /// to tear the host down.
  /// </summary>
  [Test]
  public async Task DisposeAsync_CalledTwice_IsIdempotentAsync() {
    var flusher = new BatchFlusher<int>(
      flush: (_, _) => Task.CompletedTask,
      options: new BatchFlusherOptions {
        CoalesceWindowMs = 25,
        MaxBatchSize = 100,
        ImmediateFlushThreshold = 50
      },
      logger: NullLogger.Instance,
      timeProvider: new ArmingClock());

    await flusher.DisposeAsync();
    await flusher.DisposeAsync(); // must not throw ObjectDisposedException

    await Assert.That(flusher.StoppedSignal.IsCompletedSuccessfully).IsTrue();
  }

  [Test]
  public async Task DisposeAsync_FlushesItemsStillQueuedAsync() {
    // Shutdown must not discard buffered work. Five workers share this flusher -- lease
    // renewals, inbox handler commits, perspective completions, outbox completions and message
    // failures -- so anything dropped here turns into expired leases, reprocessed messages,
    // stalled cursors and messages stuck in-flight, once per deployment and with nothing logged.
    var inFlush = new TaskCompletionSource();
    var release = new TaskCompletionSource();
    var flushed = new List<int>();
    var gate = new Lock();

    var flusher = new BatchFlusher<int>(
      flush: async (items, _) => {
        lock (gate) { flushed.AddRange(items); }
        if (!inFlush.Task.IsCompleted) {
          inFlush.SetResult();
          await release.Task;
        }
      },
      options: new BatchFlusherOptions {
        ChannelCapacity = 100,
        MaxBatchSize = 10,
        CoalesceWindowMs = 1,
        ImmediateFlushThreshold = 1,
      },
      logger: NullLogger.Instance,
      timeProvider: new ArmingClock());

    // Item 1 gets picked up and parks the loop inside the flush callback.
    await flusher.Writer.WriteAsync(1);
    await inFlush.Task;

    // These queue behind it, still in the channel when shutdown begins.
    await flusher.Writer.WriteAsync(2);
    await flusher.Writer.WriteAsync(3);

    var dispose = flusher.DisposeAsync();
    release.SetResult();
    await dispose;

    List<int> seen;
    lock (gate) { seen = [.. flushed]; }
    await Assert.That(seen).Contains(2)
      .Because("an item accepted by the writer before shutdown has to reach the flush callback");
    await Assert.That(seen).Contains(3);
  }


  [Test]
  [Timeout(30000)]
  public async Task DisposeAsync_WhenAFlushWillNotReturn_StillCompletesAsync(
      CancellationToken testToken) {
    // Draining on shutdown is what stops buffered work being discarded, but the drain cannot be
    // unbounded: a flush callback blocked on a database that has already gone away would hold
    // host shutdown open indefinitely, and the process would have to be killed. The drain is
    // therefore bounded, and past the bound the loop is cancelled and the remainder dropped --
    // losing a batch is recoverable, a host that will not stop is not.
    var inFlush = new TaskCompletionSource();
    var release = new TaskCompletionSource();
    var clock = new ArmingClock();
    var drainBound = TimeSpan.FromMilliseconds(200);

    var flusher = new BatchFlusher<int>(
      flush: async (_, ct) => {
        inFlush.TrySetResult();
        // Parks until the drain gives up and cancels, which is the path under test.
        await release.Task.WaitAsync(ct);
      },
      options: new BatchFlusherOptions {
        ChannelCapacity = 100,
        MaxBatchSize = 10,
        CoalesceWindowMs = 1,
        ImmediateFlushThreshold = 1,
        DrainTimeoutMs = 200,
      },
      logger: NullLogger.Instance,
      timeProvider: clock);

    await flusher.Writer.WriteAsync(1, testToken);
    await inFlush.Task.WaitAsync(testToken);

    // The drain bound is a timer on the flusher's clock: once shutdown has armed it, moving the clock past it is what
    // gives up on the flush. Before the bound existed this never returned, and the assertion below was never reached.
    var dispose = flusher.DisposeAsync().AsTask();
    await clock.WaitForArmedAsync(drainBound, testToken);
    clock.Advance(drainBound);
    await dispose.WaitAsync(testToken);

    release.TrySetResult();

    await Assert.That(flusher.StoppedSignal.IsCompleted).IsTrue()
      .Because("shutdown finished despite a flush that never returned on its own");
  }

}
