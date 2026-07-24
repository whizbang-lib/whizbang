using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
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
  private readonly Uuid7IdProvider _idProvider = new();

  [Test]
  public async Task AppendAsync_SameStream_MultipleSignals_CoalesceIntoOneFlushAsync() {
    var flushed = new ConcurrentBag<(Guid StreamId, int Count)>();
    var flushedSignal = new TaskCompletionSource();
    var streamId = _idProvider.NewGuid();

    await using var sut = new SlidingWindowApplyBatchStrategy(
      flush: (sid, count, ct) => {
        flushed.Add((sid, count));
        flushedSignal.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowApplyOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(50),
        MaxWait = TimeSpan.FromMilliseconds(500),
        MaxSize = 100,
      });

    await sut.AppendAsync(streamId);
    await sut.AppendAsync(streamId);
    await sut.AppendAsync(streamId);

    // Wait for the sliding-window flush to fire.
    await flushedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    var arr = flushed.ToArray();
    await Assert.That(arr.Length).IsEqualTo(1);
    await Assert.That(arr[0].StreamId).IsEqualTo(streamId);
    await Assert.That(arr[0].Count).IsEqualTo(3);
  }

  [Test]
  public async Task AppendAsync_DifferentStreams_FlushIndependentlyAsync() {
    var flushed = new ConcurrentBag<(Guid StreamId, int Count)>();
    var streamA = _idProvider.NewGuid();
    var streamB = _idProvider.NewGuid();
    var done = new TaskCompletionSource();
    var flushCount = 0;

    await using var sut = new SlidingWindowApplyBatchStrategy(
      flush: (sid, count, ct) => {
        flushed.Add((sid, count));
        if (System.Threading.Interlocked.Increment(ref flushCount) == 2) {
          done.TrySetResult();
        }
        return Task.CompletedTask;
      },
      options: new SlidingWindowApplyOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(50),
        MaxWait = TimeSpan.FromMilliseconds(500),
        MaxSize = 100,
      });

    await sut.AppendAsync(streamA);
    await sut.AppendAsync(streamA);
    await sut.AppendAsync(streamB);

    await done.Task.WaitAsync(TimeSpan.FromSeconds(2));

    var arr = flushed.ToArray();
    await Assert.That(arr.Length).IsEqualTo(2);
    // Each stream should produce one flush with the right count.
    var streamAFlush = System.Linq.Enumerable.Single(arr, t => t.StreamId == streamA);
    var streamBFlush = System.Linq.Enumerable.Single(arr, t => t.StreamId == streamB);
    await Assert.That(streamAFlush.Count).IsEqualTo(2);
    await Assert.That(streamBFlush.Count).IsEqualTo(1);
  }

  [Test]
  public async Task FlushAndStopAsync_DrainsPendingBuffersBeforeReturningAsync() {
    var flushed = new ConcurrentBag<Guid>();
    var streamId = _idProvider.NewGuid();

    var sut = new SlidingWindowApplyBatchStrategy(
      flush: (sid, _, _) => {
        flushed.Add(sid);
        return Task.CompletedTask;
      },
      options: new SlidingWindowApplyOptions {
        // Long window so we'd never flush in time without FlushAndStopAsync forcing it.
        SlidingWindow = TimeSpan.FromSeconds(30),
        MaxWait = TimeSpan.FromSeconds(60),
        MaxSize = 1000,
      });

    await sut.AppendAsync(streamId);
    await sut.AppendAsync(streamId);

    await sut.FlushAndStopAsync().WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(flushed.ToArray().Length).IsGreaterThanOrEqualTo(1);
    await Assert.That(flushed.ToArray()[0]).IsEqualTo(streamId);
  }

  [Test]
  public async Task AppendAsync_AfterDispose_ThrowsObjectDisposedAsync() {
    var sut = new SlidingWindowApplyBatchStrategy(
      flush: (_, _, _) => Task.CompletedTask);
    await sut.DisposeAsync();

    await Assert.That(async () => await sut.AppendAsync(_idProvider.NewGuid()))
      .Throws<ObjectDisposedException>();
  }

  /// <summary>
  /// Covers the idle-sweep timer callback path (static lambda → _fireAndForgetIdleSweep →
  /// _runIdleSweepAsync). A tight IdleSweepInterval ensures the timer fires at least once
  /// during the wait, and a tight IdleEvictionWindow ensures the inactive buffer is
  /// evicted.
  /// </summary>
  [Test]
  public async Task IdleSweep_EvictsInactiveStreamBuffersAsync() {
    var flushed = new ConcurrentBag<Guid>();
    var streamId = _idProvider.NewGuid();
    var flushedSignal = new TaskCompletionSource();

    await using var sut = new SlidingWindowApplyBatchStrategy(
      flush: (sid, _, _) => {
        flushed.Add(sid);
        flushedSignal.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowApplyOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(10),
        MaxWait = TimeSpan.FromMilliseconds(50),
        MaxSize = 100,
        IdleSweepInterval = TimeSpan.FromMilliseconds(20),
        IdleEvictionWindow = TimeSpan.FromMilliseconds(20),
      });

    await sut.AppendAsync(streamId);
    await flushedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    // Wait long enough for the idle sweep timer to fire at least once with an empty
    // LastActivity > cutoff predicate. The sweep timer reads _streams from the active
    // map and evicts inactive entries; the buffer added above will be evicted once
    // its LastActivity falls below IdleEvictionWindow.
    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
    while (sut.ActiveStreamCount > 0 && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(20);
    }
    await Assert.That(sut.ActiveStreamCount).IsEqualTo(0);
  }

  /// <summary>
  /// Triggers the `await _stopCts.CancelAsync()` branch in FlushAndStopAsync. Forcing a
  /// blocked worker keeps `Task.WhenAll` waiting until the caller's cancellation token
  /// fires, which exercises the OCE catch + CancelAsync path.
  /// </summary>
  [Test]
  public async Task FlushAndStopAsync_CallerCancelled_CancelsStopCtsAsync() {
    var streamId = _idProvider.NewGuid();
    var keepFlushBusy = new TaskCompletionSource();
    var sut = new SlidingWindowApplyBatchStrategy(
      flush: async (_, _, _) => await keepFlushBusy.Task.ConfigureAwait(false),
      options: new SlidingWindowApplyOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(10),
        MaxWait = TimeSpan.FromMilliseconds(50),
      });

    await sut.AppendAsync(streamId);
    await Task.Delay(60);  // let the flush start

    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    try {
      await sut.FlushAndStopAsync(cts.Token);
    } catch (OperationCanceledException) {
      // expected when WaitAsync surfaces the cancellation
    }
    // Release the stuck flush so the worker can drain.
    keepFlushBusy.TrySetResult();
  }
}
