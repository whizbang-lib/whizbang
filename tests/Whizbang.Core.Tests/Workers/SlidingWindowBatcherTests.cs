using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Change-level tests for <see cref="SlidingWindowBatcher{T}"/>. Drives the batcher with a
/// fake <see cref="TimeProvider"/> + an in-memory channel so timing is deterministic
/// (per <c>feedback_no_timing_tests</c> — no real <see cref="Task.Delay"/> or wall clock).
/// </summary>
public class SlidingWindowBatcherTests {
  private static readonly int[] _expected123 = [1, 2, 3];
  private static readonly int[] _expected01234 = [0, 1, 2, 3, 4];
  private static readonly int[] _expected12 = [1, 2];
  private static readonly int[] _expected34 = [3, 4];

  private static (Channel<int> ch, FakeTimeProvider time, SlidingWindowBatcher<int> batcher) _setup(
      SlidingWindowBatcherOptions? options = null) {
    var ch = Channel.CreateUnbounded<int>();
    var time = new FakeTimeProvider();
    var batcher = new SlidingWindowBatcher<int>(
      ch.Reader,
      options ?? new SlidingWindowBatcherOptions {
        MaxSize = 100,
        SlidingWindow = TimeSpan.FromMilliseconds(50),
        MaxWait = TimeSpan.FromSeconds(1)
      },
      time);
    return (ch, time, batcher);
  }

  /// <summary>
  /// Sliding window debounce: 3 arrivals at t=0, t=20, t=40 (each within the 50ms quiet window).
  /// After the third, no more arrivals → window expires at t=90ms → batch of 3 flushes.
  /// </summary>
  [Test]
  public async Task ReadBatches_ThreeArrivalsWithinSlidingWindow_FlushesAsOneBatchAsync() {
    var (ch, time, batcher) = _setup();
    var cts = new CancellationTokenSource();
    var batches = new List<IReadOnlyList<int>>();

    var consumeTask = Task.Run(async () => {
      await foreach (var batch in batcher.ReadBatchesAsync(cts.Token)) {
        batches.Add(batch);
        return; // exit after first batch
      }
    });

    // t=0: first arrival
    await ch.Writer.WriteAsync(1);
    // Let consumer reach the wait state.
    await Task.Yield();
    // t=20ms: second arrival
    time.Advance(TimeSpan.FromMilliseconds(20));
    await ch.Writer.WriteAsync(2);
    await Task.Yield();
    // t=40ms: third arrival
    time.Advance(TimeSpan.FromMilliseconds(20));
    await ch.Writer.WriteAsync(3);
    await Task.Yield();
    // t=90ms: sliding window expires (40 + 50 quiet)
    time.Advance(TimeSpan.FromMilliseconds(50));

    await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
    cts.Cancel();

    await Assert.That(batches).Count().IsEqualTo(1);
    await Assert.That(batches[0].SequenceEqual(_expected123)).IsTrue();
  }

  /// <summary>
  /// MaxSize cap: 100 items arrive in quick succession. Batch must flush at exactly 100,
  /// not wait for the sliding window.
  /// </summary>
  [Test]
  public async Task ReadBatches_HitsMaxSize_FlushesImmediatelyAsync() {
    var (ch, time, batcher) = _setup(new SlidingWindowBatcherOptions {
      MaxSize = 5,
      SlidingWindow = TimeSpan.FromMilliseconds(50),
      MaxWait = TimeSpan.FromSeconds(1)
    });
    var cts = new CancellationTokenSource();
    var batches = new List<IReadOnlyList<int>>();
    var consumeTask = Task.Run(async () => {
      await foreach (var batch in batcher.ReadBatchesAsync(cts.Token)) {
        batches.Add(batch);
        return;
      }
    });

    for (var i = 0; i < 5; i++) {
      await ch.Writer.WriteAsync(i);
    }

    await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
    cts.Cancel();
    await Assert.That(batches[0].SequenceEqual(_expected01234)).IsTrue();
  }

  /// <summary>
  /// MaxWait hard cap: arrivals keep coming faster than SlidingWindow can debounce, but the
  /// total wait must not exceed MaxWait. If 5 arrivals come at 100ms intervals with
  /// SlidingWindow=200ms (each new arrival resets the quiet window), the only thing that
  /// flushes is MaxWait. We set MaxWait=300ms so flushing happens at t=300ms regardless of arrivals.
  /// </summary>
  [Test]
  public async Task ReadBatches_BusyProducer_FlushesAtMaxWaitAsync() {
    var (ch, time, batcher) = _setup(new SlidingWindowBatcherOptions {
      MaxSize = 100,
      SlidingWindow = TimeSpan.FromMilliseconds(200),
      MaxWait = TimeSpan.FromMilliseconds(300)
    });
    var cts = new CancellationTokenSource();
    var batches = new List<IReadOnlyList<int>>();
    var consumeTask = Task.Run(async () => {
      await foreach (var batch in batcher.ReadBatchesAsync(cts.Token)) {
        batches.Add(batch);
        return;
      }
    });

    // Steady arrivals every 100ms. Sliding window (200ms) keeps resetting; only MaxWait (300ms) flushes.
    await ch.Writer.WriteAsync(1);
    await Task.Yield();
    time.Advance(TimeSpan.FromMilliseconds(100));
    await ch.Writer.WriteAsync(2);
    await Task.Yield();
    time.Advance(TimeSpan.FromMilliseconds(100));
    await ch.Writer.WriteAsync(3);
    await Task.Yield();
    // At t=200ms now. Sliding window would not fire until t=400ms (200 + 200 reset). Advance
    // to t=300 to trigger MaxWait.
    time.Advance(TimeSpan.FromMilliseconds(100));

    await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
    cts.Cancel();
    await Assert.That(batches[0]).Contains(1).And.Contains(2).And.Contains(3);
  }

  /// <summary>
  /// Empty channel close: if the channel is completed before any item arrives, the enumerator
  /// terminates cleanly with no yielded batches.
  /// </summary>
  [Test]
  public async Task ReadBatches_ChannelCompletesEmpty_TerminatesWithNoBatchesAsync() {
    var (ch, time, batcher) = _setup();
    var batches = new List<IReadOnlyList<int>>();

    var task = Task.Run(async () => {
      await foreach (var batch in batcher.ReadBatchesAsync()) {
        batches.Add(batch);
      }
    });

    ch.Writer.Complete();
    await task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(batches).IsEmpty();
  }

  /// <summary>
  /// Channel closes mid-batch: items arrive, then channel is completed before the sliding
  /// window expires. The pending batch must still be yielded.
  /// </summary>
  [Test]
  public async Task ReadBatches_ChannelClosesWithPendingItems_FlushesPendingBatchAsync() {
    var (ch, time, batcher) = _setup();
    var batches = new List<IReadOnlyList<int>>();

    var task = Task.Run(async () => {
      await foreach (var batch in batcher.ReadBatchesAsync()) {
        batches.Add(batch);
      }
    });

    await ch.Writer.WriteAsync(1);
    await ch.Writer.WriteAsync(2);
    await Task.Yield();
    ch.Writer.Complete();

    await task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(batches).Count().IsEqualTo(1);
    await Assert.That(batches[0].SequenceEqual(_expected12)).IsTrue();
  }

  /// <summary>
  /// Cancellation: cancelling the token mid-wait must terminate the enumerator promptly.
  /// </summary>
  [Test]
  public async Task ReadBatches_CancellationDuringWait_TerminatesAsync() {
    var (ch, _, batcher) = _setup();
    var cts = new CancellationTokenSource();
    var task = Task.Run(async () => {
      await foreach (var _ in batcher.ReadBatchesAsync(cts.Token)) {
      }
    });

    await ch.Writer.WriteAsync(1);
    await Task.Yield();
    cts.Cancel();

    await task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(task.IsCompleted).IsTrue();
  }

  /// <summary>
  /// Multiple sequential batches: after the first batch flushes, the next arrival starts a
  /// fresh batch with its own sliding window.
  /// </summary>
  [Test]
  public async Task ReadBatches_MultipleSequentialBatches_EachIndependentAsync() {
    var (ch, time, batcher) = _setup();
    var cts = new CancellationTokenSource();
    var batches = new List<IReadOnlyList<int>>();
    var batch1Flushed = new TaskCompletionSource();
    var task = Task.Run(async () => {
      await foreach (var batch in batcher.ReadBatchesAsync(cts.Token)) {
        batches.Add(batch);
        if (batches.Count == 1) { batch1Flushed.TrySetResult(); }
        if (batches.Count == 2) { return; }
      }
    });

    // Batch 1: items 1, 2 → flush via sliding window
    await ch.Writer.WriteAsync(1);
    await ch.Writer.WriteAsync(2);
    await Task.Yield();
    time.Advance(TimeSpan.FromMilliseconds(60)); // > SlidingWindow

    // Deterministically wait for batch 1 to complete before starting batch 2.
    await batch1Flushed.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Batch 2: items 3, 4
    await ch.Writer.WriteAsync(3);
    await ch.Writer.WriteAsync(4);
    await Task.Yield();
    time.Advance(TimeSpan.FromMilliseconds(60));

    await task.WaitAsync(TimeSpan.FromSeconds(5));
    cts.Cancel();
    await Assert.That(batches).Count().IsEqualTo(2);
    await Assert.That(batches[0].SequenceEqual(_expected12)).IsTrue();
    await Assert.That(batches[1].SequenceEqual(_expected34)).IsTrue();
  }

  /// <summary>
  /// Constructor validation: invalid options throw ArgumentOutOfRangeException.
  /// </summary>
  [Test]
  public async Task Constructor_InvalidMaxSize_ThrowsAsync() {
    var ch = Channel.CreateUnbounded<int>();
    await Assert.That(() => new SlidingWindowBatcher<int>(
      ch.Reader,
      new SlidingWindowBatcherOptions { MaxSize = 0 }))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task Constructor_SlidingWindowGreaterThanMaxWait_ThrowsAsync() {
    var ch = Channel.CreateUnbounded<int>();
    await Assert.That(() => new SlidingWindowBatcher<int>(
      ch.Reader,
      new SlidingWindowBatcherOptions {
        SlidingWindow = TimeSpan.FromSeconds(2),
        MaxWait = TimeSpan.FromSeconds(1)
      }))
      .Throws<ArgumentOutOfRangeException>();
  }
}
