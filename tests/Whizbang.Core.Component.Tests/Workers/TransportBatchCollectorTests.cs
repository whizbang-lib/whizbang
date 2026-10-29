// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for <see cref="TransportBatchCollector{T}"/> — the transport-level batch collector
/// that buffers messages and flushes them via a callback when a batch is ready.
/// <para>
/// Three flush triggers (whichever fires first):
/// <list type="bullet">
///   <item>Batch size: <c>BatchSize</c> messages accumulated → immediate flush</item>
///   <item>Sliding window: <c>SlideMs</c> ms since last enqueue → flush partial batch</item>
///   <item>Hard max: <c>MaxWaitMs</c> ms since first message in batch → flush regardless</item>
/// </list>
/// </para>
/// <para>
/// Every collector here runs on a fake clock (#1868). <see cref="TransportBatchCollector{T}.Enqueue"/> arms the
/// windows before it returns, so a test advances the clock right after enqueueing, and a window closes only when the
/// test moves time, never because a loaded runner was slow. A flush runs on the thread pool; each test waits on the
/// flush callback or on <see cref="TransportBatchCollector{T}.InFlightFlushes"/> having drained, never on a delay.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportBatchCollector.cs</code-under-test>
[Category("Workers")]
public class TransportBatchCollectorTests {
  private const int SLIDE_MS = 50;
  private const int MAX_WAIT_MS = 1_000;
  private const int FAR_AWAY_MS = 600_000;

  // ========================================
  // Batch Size Trigger
  // ========================================

  [Test]
  [Timeout(30000)]
  public async Task Enqueue_FlushesImmediatelyWhenBatchSizeReachedAsync(CancellationToken cancellationToken) {
    // Arrange
    var flushedBatches = new List<IReadOnlyList<int>>();
    var flushSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var options = new TransportBatchOptions { BatchSize = 3, SlideMs = FAR_AWAY_MS, MaxWaitMs = FAR_AWAY_MS };

    await using var collector = new TransportBatchCollector<int>(options, batch => {
      flushedBatches.Add(batch);
      flushSignal.TrySetResult();
      return Task.CompletedTask;
    }, new ArmingClock());

    // Act — enqueue exactly batchSize items; the clock never moves, so only the size can flush
    collector.Enqueue(1);
    collector.Enqueue(2);
    collector.Enqueue(3);

    await flushSignal.Task.WaitAsync(cancellationToken);

    // Assert — single flush with all 3 items
    await Assert.That(flushedBatches).Count().IsEqualTo(1);
    await Assert.That(flushedBatches[0]).Count().IsEqualTo(3);
    await Assert.That(flushedBatches[0][0]).IsEqualTo(1);
    await Assert.That(flushedBatches[0][1]).IsEqualTo(2);
    await Assert.That(flushedBatches[0][2]).IsEqualTo(3);
  }

  [Test]
  [Timeout(30000)]
  public async Task Enqueue_DoesNotFlushBelowBatchSizeAsync(CancellationToken cancellationToken) {
    // Arrange
    var flushCount = 0;
    var clock = new ArmingClock();
    var options = new TransportBatchOptions { BatchSize = 10, SlideMs = SLIDE_MS, MaxWaitMs = MAX_WAIT_MS };

    await using var collector = new TransportBatchCollector<int>(options, _ => {
      Interlocked.Increment(ref flushCount);
      return Task.CompletedTask;
    }, clock);

    // Act — enqueue fewer than batchSize and move the clock to one tick short of the sliding window
    collector.Enqueue(1);
    collector.Enqueue(2);
    clock.Advance(TimeSpan.FromMilliseconds(SLIDE_MS - 1));
    // Any flush a trigger started is tracked; once the record is empty, none is still on its way
    await collector.InFlightFlushes.DrainAsync(cancellationToken);

    // Assert — no flush: below the batch size, and neither window has closed
    await Assert.That(Volatile.Read(ref flushCount)).IsEqualTo(0);
  }

  // ========================================
  // Sliding Window Trigger
  // ========================================

  [Test]
  [Timeout(30000)]
  public async Task Enqueue_FlushesOnSlidingWindowTimeoutAsync(CancellationToken cancellationToken) {
    // Arrange
    var flushedBatches = new List<IReadOnlyList<int>>();
    var flushSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var clock = new ArmingClock();
    var options = new TransportBatchOptions { BatchSize = 1000, SlideMs = SLIDE_MS, MaxWaitMs = MAX_WAIT_MS };

    await using var collector = new TransportBatchCollector<int>(options, batch => {
      flushedBatches.Add(batch);
      flushSignal.TrySetResult();
      return Task.CompletedTask;
    }, clock);

    // Act — enqueue a few items, then let the sliding window pass with no further arrival
    collector.Enqueue(1);
    collector.Enqueue(2);
    clock.Advance(TimeSpan.FromMilliseconds(SLIDE_MS));

    await flushSignal.Task.WaitAsync(cancellationToken);

    // Assert — flushed after sliding window with the 2 items
    await Assert.That(flushedBatches).Count().IsEqualTo(1);
    await Assert.That(flushedBatches[0]).Count().IsEqualTo(2);
  }

  [Test]
  [Timeout(30000)]
  public async Task Enqueue_SlidingWindowResetsOnNewMessageAsync(CancellationToken cancellationToken) {
    // Arrange
    var flushedBatches = new List<IReadOnlyList<int>>();
    var flushSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var clock = new ArmingClock();
    var options = new TransportBatchOptions { BatchSize = 1000, SlideMs = SLIDE_MS, MaxWaitMs = MAX_WAIT_MS };

    await using var collector = new TransportBatchCollector<int>(options, batch => {
      flushedBatches.Add(batch);
      flushSignal.TrySetResult();
      return Task.CompletedTask;
    }, clock);

    // Act — each item arrives one tick before the window the previous one opened would close. Together the gaps
    // exceed the window, so a window that did not restart on each arrival would have flushed the first item alone.
    var almostTheWindow = TimeSpan.FromMilliseconds(SLIDE_MS - 1);
    collector.Enqueue(1);
    clock.Advance(almostTheWindow);
    collector.Enqueue(2);
    clock.Advance(almostTheWindow);
    collector.Enqueue(3);
    clock.Advance(almostTheWindow);
    await collector.InFlightFlushes.DrainAsync(cancellationToken);

    await Assert.That(flushedBatches).IsEmpty()
      .Because("Sliding window should reset on each new message");

    // The window the last arrival opened closes one tick later, and flushes all three together
    clock.Advance(TimeSpan.FromMilliseconds(1));
    await flushSignal.Task.WaitAsync(cancellationToken);

    await Assert.That(flushedBatches).Count().IsEqualTo(1);
    await Assert.That(flushedBatches[0]).IsEquivalentTo([1, 2, 3]);
  }

  // ========================================
  // Hard Max Trigger
  // ========================================

  [Test]
  [Timeout(30000)]
  public async Task Enqueue_FlushesOnHardMaxTimeoutAsync(CancellationToken cancellationToken) {
    // Arrange
    var flushedBatches = new List<IReadOnlyList<int>>();
    var flushSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var clock = new ArmingClock();
    var options = new TransportBatchOptions { BatchSize = 1000, SlideMs = FAR_AWAY_MS, MaxWaitMs = 100 };

    await using var collector = new TransportBatchCollector<int>(options, batch => {
      flushedBatches.Add(batch);
      flushSignal.TrySetResult();
      return Task.CompletedTask;
    }, clock);

    // Act — enqueue one item; the hard max closes at 100ms, long before the sliding window would
    collector.Enqueue(1);
    clock.Advance(TimeSpan.FromMilliseconds(100));

    await flushSignal.Task.WaitAsync(cancellationToken);

    // Assert — flushed after hard max
    await Assert.That(flushedBatches).Count().IsEqualTo(1);
    await Assert.That(flushedBatches[0]).Count().IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task Enqueue_HardMaxIsNotPostponedByLaterArrivalsAsync(CancellationToken cancellationToken) {
    // The sliding window restarts on every arrival, so a steady trickle would hold it open forever; the hard max is
    // measured from the FIRST message of the batch and is what bounds the latency under that load.
    var flushedBatches = new List<IReadOnlyList<int>>();
    var flushSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var clock = new ArmingClock();
    var options = new TransportBatchOptions { BatchSize = 1000, SlideMs = SLIDE_MS, MaxWaitMs = 100 };

    await using var collector = new TransportBatchCollector<int>(options, batch => {
      flushedBatches.Add(batch);
      flushSignal.TrySetResult();
      return Task.CompletedTask;
    }, clock);

    // Arrivals at 0, 40 and 80 ms keep the 50 ms sliding window open past 100 ms (it would next close at 130 ms)
    collector.Enqueue(1);
    clock.Advance(TimeSpan.FromMilliseconds(40));
    collector.Enqueue(2);
    clock.Advance(TimeSpan.FromMilliseconds(40));
    collector.Enqueue(3);
    clock.Advance(TimeSpan.FromMilliseconds(20));

    await flushSignal.Task.WaitAsync(cancellationToken);

    await Assert.That(flushedBatches).Count().IsEqualTo(1);
    await Assert.That(flushedBatches[0]).IsEquivalentTo([1, 2, 3])
      .Because("the hard max closed 100 ms after the first arrival, while the sliding window was still open");
  }

  // ========================================
  // Flush Callback Receives All Messages
  // ========================================

  [Test]
  [Timeout(30000)]
  public async Task FlushCallback_ReceivesAllBatchedMessagesInOrderAsync(CancellationToken cancellationToken) {
    // Arrange
    var flushedBatches = new List<IReadOnlyList<string>>();
    var flushSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var options = new TransportBatchOptions { BatchSize = 5, SlideMs = FAR_AWAY_MS, MaxWaitMs = FAR_AWAY_MS };

    await using var collector = new TransportBatchCollector<string>(options, batch => {
      flushedBatches.Add(batch);
      flushSignal.TrySetResult();
      return Task.CompletedTask;
    }, new ArmingClock());

    // Act
    collector.Enqueue("alpha");
    collector.Enqueue("bravo");
    collector.Enqueue("charlie");
    collector.Enqueue("delta");
    collector.Enqueue("echo");

    await flushSignal.Task.WaitAsync(cancellationToken);

    // Assert — all 5 in order
    await Assert.That(flushedBatches[0]).Count().IsEqualTo(5);
    await Assert.That(flushedBatches[0][0]).IsEqualTo("alpha");
    await Assert.That(flushedBatches[0][4]).IsEqualTo("echo");
  }

  // ========================================
  // Dispose Flushes Remaining
  // ========================================

  [Test]
  public async Task DisposeAsync_FlushesRemainingMessagesAsync() {
    // Arrange
    var flushedBatches = new List<IReadOnlyList<int>>();
    var options = new TransportBatchOptions { BatchSize = 1000, SlideMs = SLIDE_MS, MaxWaitMs = MAX_WAIT_MS };

    var collector = new TransportBatchCollector<int>(options, batch => {
      flushedBatches.Add(batch);
      return Task.CompletedTask;
    }, new ArmingClock());

    // Act — enqueue items but don't trigger any flush (the clock never moves)
    collector.Enqueue(1);
    collector.Enqueue(2);
    collector.Enqueue(3);

    // Dispose should flush remaining
    await collector.DisposeAsync();

    // Assert
    await Assert.That(flushedBatches).Count().IsEqualTo(1);
    await Assert.That(flushedBatches[0]).Count().IsEqualTo(3);
  }

  // ========================================
  // Multiple Batches
  // ========================================

  [Test]
  [Timeout(30000)]
  public async Task Enqueue_MultipleBatchesFlushIndependentlyAsync(CancellationToken cancellationToken) {
    // Arrange
    var flushedBatches = new List<IReadOnlyList<int>>();
    var flushCount = 0;
    var firstFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var options = new TransportBatchOptions { BatchSize = 3, SlideMs = FAR_AWAY_MS, MaxWaitMs = FAR_AWAY_MS };

    await using var collector = new TransportBatchCollector<int>(options, batch => {
      flushedBatches.Add(batch);
      var count = Interlocked.Increment(ref flushCount);
      if (count == 1) {
        firstFlush.TrySetResult();
      }
      if (count == 2) {
        secondFlush.TrySetResult();
      }
      return Task.CompletedTask;
    }, new ArmingClock());

    // Act — enqueue 6 items → should trigger 2 batches of 3
    collector.Enqueue(1);
    collector.Enqueue(2);
    collector.Enqueue(3); // flush 1 triggers

    // Wait for first flush to complete before enqueueing second batch
    // so the pending list has been swapped and second batch starts fresh
    await firstFlush.Task.WaitAsync(cancellationToken);

    collector.Enqueue(4);
    collector.Enqueue(5);
    collector.Enqueue(6); // flush 2 triggers

    await secondFlush.Task.WaitAsync(cancellationToken);

    // Assert — total messages across both batches is 6
    await Assert.That(flushedBatches).Count().IsEqualTo(2);
    var totalMessages = flushedBatches[0].Count + flushedBatches[1].Count;
    await Assert.That(totalMessages).IsEqualTo(6);
  }

  // ========================================
  // Clock
  // ========================================

  [Test]
  public async Task Constructor_WithoutAClock_RunsOnTheSystemClockAsync() {
    // The clock parameter is optional: a collector built without one keeps the system clock, so existing callers are
    // unchanged. Nothing here waits on it; disposing delivers what is pending.
    var flushed = new List<int>();
    var collector = new TransportBatchCollector<int>(
      new TransportBatchOptions { BatchSize = 1000, SlideMs = FAR_AWAY_MS, MaxWaitMs = FAR_AWAY_MS },
      batch => {
        flushed.AddRange(batch);
        return Task.CompletedTask;
      },
      timeProvider: null);

    collector.Enqueue(7);
    await collector.DisposeAsync();

    await Assert.That(flushed).IsEquivalentTo([7]);
  }
}
