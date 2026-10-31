// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the contract of <see cref="SlidingWindowApplyBatchStrategy"/> — the
/// perspective-apply boundary batcher (slice 22c). Per-stream-keyed sliding window:
/// same-stream drain signals coalesce into one flush; different streams batch
/// independently. Defaults 300 ms / 3 s / 1000.
/// </summary>
public class SlidingWindowApplyBatchStrategyTests {
  /// <summary>
  /// The batch window. Tests that wait on a window run the batcher on a <see cref="BatchWindowClock"/>, so the window
  /// closes only when the test advances the clock past it, never because a loaded runner was slow.
  /// </summary>
  private static readonly TimeSpan _window = TimeSpan.FromMilliseconds(50);

  private readonly Uuid7IdProvider _idProvider = new();

  [Test]
  [Timeout(30000)]
  public async Task AppendAsync_SameStream_MultipleSignals_CoalesceIntoOneFlushAsync(CancellationToken cancellationToken) {
    var flushed = new ConcurrentBag<(Guid StreamId, int Count)>();
    var flushedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var streamId = _idProvider.NewGuid();
    var clock = new BatchWindowClock(_window);

    await using var sut = new SlidingWindowApplyBatchStrategy(
      flush: (sid, count, _) => {
        flushed.Add((sid, count));
        flushedSignal.TrySetResult();
        return Task.CompletedTask;
      },
      logger: NullLogger<SlidingWindowApplyBatchStrategy>.Instance,
      options: new SlidingWindowApplyOptions {
        SlidingWindow = _window,
        MaxWait = TimeSpan.FromMilliseconds(500),
        MaxSize = 100,
      },
      timeProvider: clock);

    // Each signal is drained, and its window armed, before the next one is made, so all three are in the open
    // batch when the clock closes the window.
    await sut.AppendAsync(streamId, cancellationToken);
    await clock.Armed(1).WaitAsync(cancellationToken);
    await sut.AppendAsync(streamId, cancellationToken);
    await clock.Armed(2).WaitAsync(cancellationToken);
    await sut.AppendAsync(streamId, cancellationToken);
    await clock.Armed(3).WaitAsync(cancellationToken);

    // Close the sliding window and wait for its flush.
    clock.Advance(_window);
    await flushedSignal.Task.WaitAsync(cancellationToken);

    var arr = flushed.ToArray();
    await Assert.That(arr.Length).IsEqualTo(1);
    await Assert.That(arr[0].StreamId).IsEqualTo(streamId);
    await Assert.That(arr[0].Count).IsEqualTo(3);
  }

  [Test]
  [Timeout(30000)]
  public async Task AppendAsync_DifferentStreams_FlushIndependentlyAsync(CancellationToken cancellationToken) {
    var flushed = new ConcurrentBag<(Guid StreamId, int Count)>();
    var streamA = _idProvider.NewGuid();
    var streamB = _idProvider.NewGuid();
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var flushCount = 0;
    var clock = new BatchWindowClock(_window);

    await using var sut = new SlidingWindowApplyBatchStrategy(
      flush: (sid, count, _) => {
        flushed.Add((sid, count));
        if (System.Threading.Interlocked.Increment(ref flushCount) == 2) {
          done.TrySetResult();
        }
        return Task.CompletedTask;
      },
      logger: NullLogger<SlidingWindowApplyBatchStrategy>.Instance,
      options: new SlidingWindowApplyOptions {
        SlidingWindow = _window,
        MaxWait = TimeSpan.FromMilliseconds(500),
        MaxSize = 100,
      },
      timeProvider: clock);

    // Stream A's worker arms the first two windows (its second signal restarts its window); stream B's worker arms
    // the third. One advance then closes both streams' open windows together.
    await sut.AppendAsync(streamA, cancellationToken);
    await clock.Armed(1).WaitAsync(cancellationToken);
    await sut.AppendAsync(streamA, cancellationToken);
    await clock.Armed(2).WaitAsync(cancellationToken);
    await sut.AppendAsync(streamB, cancellationToken);
    await clock.Armed(3).WaitAsync(cancellationToken);

    clock.Advance(_window);
    await done.Task.WaitAsync(cancellationToken);

    var arr = flushed.ToArray();
    await Assert.That(arr.Length).IsEqualTo(2);
    // Each stream should produce one flush with the right count.
    var streamAFlush = arr.Single(t => t.StreamId == streamA);
    var streamBFlush = arr.Single(t => t.StreamId == streamB);
    await Assert.That(streamAFlush.Count).IsEqualTo(2);
    await Assert.That(streamBFlush.Count).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task FlushAndStopAsync_DrainsPendingBuffersBeforeReturningAsync(CancellationToken cancellationToken) {
    var flushed = new ConcurrentBag<Guid>();
    var streamId = _idProvider.NewGuid();

    var sut = new SlidingWindowApplyBatchStrategy(
      flush: (sid, _, _) => {
        flushed.Add(sid);
        return Task.CompletedTask;
      },
      logger: NullLogger<SlidingWindowApplyBatchStrategy>.Instance,
      options: new SlidingWindowApplyOptions {
        SlidingWindow = TimeSpan.FromSeconds(30),
        MaxWait = TimeSpan.FromSeconds(60),
        MaxSize = 1000,
      },
      // A clock nobody advances: no window can close, so only FlushAndStopAsync can force the flush.
      timeProvider: new FakeTimeProvider());

    await sut.AppendAsync(streamId, cancellationToken);
    await sut.AppendAsync(streamId, cancellationToken);

    await sut.FlushAndStopAsync(cancellationToken);

    await Assert.That(flushed.ToArray().Length).IsGreaterThanOrEqualTo(1);
    await Assert.That(flushed.ToArray()[0]).IsEqualTo(streamId);
  }

  [Test]
  public async Task AppendAsync_AfterDispose_ThrowsObjectDisposedAsync() {
    var sut = new SlidingWindowApplyBatchStrategy(
      flush: (_, _, _) => Task.CompletedTask,
      logger: NullLogger<SlidingWindowApplyBatchStrategy>.Instance);
    await sut.DisposeAsync();

    await Assert.That(async () => await sut.AppendAsync(_idProvider.NewGuid()))
      .Throws<ObjectDisposedException>();
  }

  /// <summary>
  /// Covers the idle-sweep timer callback path (static lambda → _fireAndForgetIdleSweep →
  /// _runIdleSweepAsync). Advancing the clock by one IdleSweepInterval fires the timer, and by
  /// then the buffer has been quiet for longer than the IdleEvictionWindow, so it is evicted.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task IdleSweep_EvictsInactiveStreamBuffersAsync(CancellationToken cancellationToken) {
    var flushed = new ConcurrentBag<Guid>();
    var streamId = _idProvider.NewGuid();
    var flushedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var window = TimeSpan.FromMilliseconds(10);
    var sweepInterval = TimeSpan.FromSeconds(1);
    var clock = new BatchWindowClock(window);

    await using var sut = new SlidingWindowApplyBatchStrategy(
      flush: (sid, _, _) => {
        flushed.Add(sid);
        flushedSignal.TrySetResult();
        return Task.CompletedTask;
      },
      logger: NullLogger<SlidingWindowApplyBatchStrategy>.Instance,
      options: new SlidingWindowApplyOptions {
        SlidingWindow = window,
        MaxWait = TimeSpan.FromMilliseconds(50),
        MaxSize = 100,
        IdleSweepInterval = sweepInterval,
        IdleEvictionWindow = sweepInterval,
      },
      timeProvider: clock);

    await sut.AppendAsync(streamId, cancellationToken);
    await clock.Armed(1).WaitAsync(cancellationToken);
    clock.Advance(window);
    await flushedSignal.Task.WaitAsync(cancellationToken);
    await Assert.That(sut.ActiveStreamCount).IsEqualTo(1)
      .Because("the stream stays mapped after its flush until a sweep finds it idle");

    // The sweep timer's callback runs inside the advance and records the sweep it starts; wait for that sweep.
    // The sweep reads _streams from the active map and evicts the entry whose LastActivity is now older than
    // the IdleEvictionWindow.
    clock.Advance(sweepInterval);
    await sut.IdleSweeps.DrainAsync(cancellationToken);

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(0);
  }

  /// <summary>
  /// Triggers the `await _stopCts.CancelAsync()` branch in FlushAndStopAsync. Forcing a
  /// blocked worker keeps `Task.WhenAll` waiting until the caller's cancellation token
  /// fires, which exercises the OCE catch + CancelAsync path.
  /// </summary>
  /// <remarks>
  /// Both waits here are signals rather than durations. Sleeping to "let the flush start" and arming the caller's
  /// token on a timer made this fail on a loaded runner, where the flush had not begun inside the sleep: the test
  /// then asserted against a token no flush was ever handed, and read as a defect in the strategy.
  /// </remarks>
  [Test]
  [Timeout(30000)]
  public async Task FlushAndStopAsync_CallerCanceled_CancelsStopCtsAsync(CancellationToken cancellationToken) {
    var streamId = _idProvider.NewGuid();
    var keepFlushBusy = new TaskCompletionSource();
    var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var window = TimeSpan.FromMilliseconds(10);
    var clock = new BatchWindowClock(window);
    // The token the in-flight flush was handed — the strategy's own stop token.
    var flushToken = CancellationToken.None;
    var sut = new SlidingWindowApplyBatchStrategy(
      flush: async (_, _, ct) => {
        flushToken = ct;
        flushStarted.TrySetResult();
        await keepFlushBusy.Task.ConfigureAwait(false);
      },
      logger: NullLogger<SlidingWindowApplyBatchStrategy>.Instance,
      options: new SlidingWindowApplyOptions {
        SlidingWindow = window,
        MaxWait = TimeSpan.FromMilliseconds(50),
      },
      timeProvider: clock);

    await sut.AppendAsync(streamId, cancellationToken);
    await clock.Armed(1).WaitAsync(cancellationToken);
    clock.Advance(window);
    await flushStarted.Task.WaitAsync(cancellationToken);  // the flush is in flight and stuck, whatever the machine's load

    using var cts = new CancellationTokenSource();
    var stopping = sut.FlushAndStopAsync(cts.Token);
    await cts.CancelAsync();  // the caller gives up while that flush is still blocked
    try {
      await stopping;
    } catch (OperationCanceledException) {
      // expected when WaitAsync surfaces the cancellation
    }
    await Assert.That(flushToken.IsCancellationRequested).IsTrue()
      .Because("when the caller's token gives up first, the strategy cancels its OWN stop token so the "
             + "flush still in flight is told to abandon the drain. Without it that flush keeps running "
             + "past a shutdown nobody is waiting on any more.");

    // Release the stuck flush so the worker can drain.
    keepFlushBusy.TrySetResult();
  }

  /// <summary>
  /// <para>
  /// The idle sweep evicts a stream buffer in two steps — remove from the active map, then
  /// complete its writer. <c>AppendAsync</c> reads the map and then writes, so a caller can be
  /// holding a buffer the sweep completes in between, and the write throws
  /// <c>ChannelClosedException</c> straight at the caller. That is a real production race on the
  /// perspective apply path, not a test artifact: it surfaced as an intermittent CI failure in
  /// this class's own idle-sweep test, where a 20ms sweep interval made the window easy to hit
  /// under load.
  /// </para>
  /// <para>
  /// Append must survive it — a stream being evicted for idleness is a normal, expected event
  /// and must never fail the append that raced it. Deterministic: the seam completes a still-
  /// mapped writer, exactly the state the sweep produces, with no sleeps or scheduling luck.
  /// </para>
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task Append_WhenIdleSweepCompletedTheBufferItGrabbed_StillSucceedsAsync(CancellationToken cancellationToken) {
    var flushed = new ConcurrentBag<Guid>();
    var streamId = _idProvider.NewGuid();
    // The completed buffer flushes the first signal as its worker ends; the second flush is the one the append
    // that raced the eviction has to reach.
    var secondFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var window = TimeSpan.FromMilliseconds(10);
    var clock = new BatchWindowClock(window);

    await using var sut = new SlidingWindowApplyBatchStrategy(
      flush: (sid, _, _) => {
        flushed.Add(sid);
        if (flushed.Count == 2) {
          secondFlush.TrySetResult();
        }
        return Task.CompletedTask;
      },
      logger: NullLogger<SlidingWindowApplyBatchStrategy>.Instance,
      options: new SlidingWindowApplyOptions {
        SlidingWindow = window,
        MaxWait = TimeSpan.FromMilliseconds(50),
        MaxSize = 100,
        // Long enough that the sweep never fires during this test: the seam drives the
        // eviction state instead, so the assertion is about recovery, not about timing.
        IdleSweepInterval = TimeSpan.FromMinutes(5),
        IdleEvictionWindow = TimeSpan.FromMinutes(5),
      },
      timeProvider: clock);

    await sut.AppendAsync(streamId, cancellationToken);
    // The first buffer's worker has drained the signal and armed its window
    await clock.Armed(1).WaitAsync(cancellationToken);

    // Reproduce the losing interleave: the buffer this stream maps to is completed while still
    // mapped, so the next append grabs a dead channel.
    await Assert.That(sut.CompleteMappedWriterForTest(streamId)).IsTrue()
      .Because("test setup must have found and completed a live buffer");

    await sut.AppendAsync(streamId, cancellationToken);

    // The new buffer's worker arms its own window; closing it flushes the second signal
    await clock.Armed(2).WaitAsync(cancellationToken);
    clock.Advance(window);
    await secondFlush.Task.WaitAsync(cancellationToken);
    await Assert.That(flushed).Contains(streamId)
      .Because("an append that raced an idle eviction must still reach a flush, not throw");
  }
}
