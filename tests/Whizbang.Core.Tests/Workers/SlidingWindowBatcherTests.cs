using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Change-level tests for <see cref="SlidingWindowBatcher{T}"/>. Uses real <see cref="TimeProvider.System"/>
/// with small delays for deterministic timer scheduling — FakeTimeProvider was too flaky here
/// because Task.Yield() doesn't guarantee the consumer task has actually called
/// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> before the test advances
/// fake time. Real-time delays in the 5-100ms range are reliable, fast, and observable.
/// </summary>
public class SlidingWindowBatcherTests {
  private static readonly int[] _expected123 = [1, 2, 3];
  private static readonly int[] _expected01234 = [0, 1, 2, 3, 4];
  private static readonly int[] _expected12 = [1, 2];
  private static readonly int[] _expected34 = [3, 4];

  private static (Channel<int> ch, SlidingWindowBatcher<int> batcher) _setup(
      SlidingWindowBatcherOptions? options = null) {
    var ch = Channel.CreateUnbounded<int>();
    var batcher = new SlidingWindowBatcher<int>(
      ch.Reader,
      options ?? new SlidingWindowBatcherOptions {
        MaxSize = 100,
        SlidingWindow = TimeSpan.FromMilliseconds(50),
        MaxWait = TimeSpan.FromSeconds(1)
      });
    return (ch, batcher);
  }

  /// <summary>
  /// Sliding-window debounce: 3 arrivals within the quiet window. After arrivals stop,
  /// the window expires and the batch flushes with all 3.
  /// </summary>
  [Test]
  public async Task ReadBatches_ThreeArrivalsWithinSlidingWindow_FlushesAsOneBatchAsync() {
    var (ch, batcher) = _setup(new SlidingWindowBatcherOptions {
      MaxSize = 100,
      SlidingWindow = TimeSpan.FromMilliseconds(80),
      MaxWait = TimeSpan.FromSeconds(2)
    });
    var firstBatch = new TaskCompletionSource<IReadOnlyList<int>>();
    var cts = new CancellationTokenSource();
    var consumeTask = Task.Run(async () => {
      await foreach (var batch in batcher.ReadBatchesAsync(cts.Token)) {
        firstBatch.TrySetResult(batch);
        return;
      }
    });

    await ch.Writer.WriteAsync(1);
    await Task.Delay(20);
    await ch.Writer.WriteAsync(2);
    await Task.Delay(20);
    await ch.Writer.WriteAsync(3);
    // No more arrivals — sliding window (80ms) will expire ~80ms after the third arrival.

    var batch = await firstBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cts.Cancel();
    try { await consumeTask; } catch (OperationCanceledException) { }

    await Assert.That(batch.SequenceEqual(_expected123)).IsTrue();
  }

  /// <summary>
  /// MaxSize cap: items arrive faster than the sliding window. Batch flushes immediately at
  /// MaxSize (5).
  /// </summary>
  [Test]
  public async Task ReadBatches_HitsMaxSize_FlushesImmediatelyAsync() {
    var (ch, batcher) = _setup(new SlidingWindowBatcherOptions {
      MaxSize = 5,
      SlidingWindow = TimeSpan.FromMilliseconds(500),
      MaxWait = TimeSpan.FromSeconds(10)
    });
    var firstBatch = new TaskCompletionSource<IReadOnlyList<int>>();
    var cts = new CancellationTokenSource();
    var consumeTask = Task.Run(async () => {
      await foreach (var batch in batcher.ReadBatchesAsync(cts.Token)) {
        firstBatch.TrySetResult(batch);
        return;
      }
    });

    for (var i = 0; i < 5; i++) {
      await ch.Writer.WriteAsync(i);
    }

    // Should flush at MaxSize=5, NOT wait for SlidingWindow=500ms.
    var batch = await firstBatch.Task.WaitAsync(TimeSpan.FromMilliseconds(400));
    cts.Cancel();
    try { await consumeTask; } catch (OperationCanceledException) { }

    await Assert.That(batch.SequenceEqual(_expected01234)).IsTrue();
  }

  /// <summary>
  /// MaxWait hard cap: arrivals keep coming inside the sliding window so the debounce never
  /// expires, but MaxWait flushes the batch eventually.
  /// </summary>
  [Test]
  public async Task ReadBatches_BusyProducer_FlushesAtMaxWaitAsync() {
    var (ch, batcher) = _setup(new SlidingWindowBatcherOptions {
      MaxSize = 100,
      SlidingWindow = TimeSpan.FromMilliseconds(150),
      MaxWait = TimeSpan.FromMilliseconds(250)
    });
    var firstBatch = new TaskCompletionSource<IReadOnlyList<int>>();
    var cts = new CancellationTokenSource();
    var consumeTask = Task.Run(async () => {
      await foreach (var batch in batcher.ReadBatchesAsync(cts.Token)) {
        firstBatch.TrySetResult(batch);
        return;
      }
    });

    // Steady arrivals every 50ms — sliding window (150ms) keeps resetting; MaxWait (250ms) wins.
    var producer = Task.Run(async () => {
      for (var i = 1; i <= 20 && !cts.IsCancellationRequested; i++) {
        try {
          await ch.Writer.WriteAsync(i, cts.Token);
        } catch (OperationCanceledException) { return; }
        await Task.Delay(50);
      }
    });

    var batch = await firstBatch.Task.WaitAsync(TimeSpan.FromSeconds(2));
    cts.Cancel();
    try { await consumeTask; } catch (OperationCanceledException) { }
    try { await producer; } catch (OperationCanceledException) { }

    // Batch should contain at least 3 items (250ms / 50ms ≈ 5, but timing variance gives 3-6).
    await Assert.That(batch.Count).IsGreaterThanOrEqualTo(3);
    await Assert.That(batch.Contains(1)).IsTrue();
  }

  /// <summary>
  /// Empty channel close: no batches are yielded.
  /// </summary>
  [Test]
  public async Task ReadBatches_ChannelCompletesEmpty_TerminatesWithNoBatchesAsync() {
    var (ch, batcher) = _setup();
    var batches = new List<IReadOnlyList<int>>();

    var task = Task.Run(async () => {
      await foreach (var batch in batcher.ReadBatchesAsync()) {
        batches.Add(batch);
      }
    });

    ch.Writer.Complete();
    await task.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(batches).IsEmpty();
  }

  /// <summary>
  /// Channel closes mid-batch: pending items are still yielded.
  /// </summary>
  [Test]
  public async Task ReadBatches_ChannelClosesWithPendingItems_FlushesPendingBatchAsync() {
    var (ch, batcher) = _setup();
    var batches = new List<IReadOnlyList<int>>();

    var task = Task.Run(async () => {
      await foreach (var batch in batcher.ReadBatchesAsync()) {
        batches.Add(batch);
      }
    });

    await ch.Writer.WriteAsync(1);
    await ch.Writer.WriteAsync(2);
    await Task.Delay(10);
    ch.Writer.Complete();

    await task.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(batches).Count().IsEqualTo(1);
    await Assert.That(batches[0].SequenceEqual(_expected12)).IsTrue();
  }

  /// <summary>
  /// Cancellation: cancelling mid-wait terminates the enumerator promptly.
  /// </summary>
  [Test]
  public async Task ReadBatches_CancellationDuringWait_TerminatesAsync() {
    var (ch, batcher) = _setup();
    var cts = new CancellationTokenSource();
    var task = Task.Run(async () => {
      await foreach (var _ in batcher.ReadBatchesAsync(cts.Token)) {
      }
    });

    await ch.Writer.WriteAsync(1);
    await Task.Delay(10);
    cts.Cancel();

    await task.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(task.IsCompleted).IsTrue();
  }

  /// <summary>
  /// Multiple sequential batches: after one batch flushes, the next arrival starts a fresh
  /// batch with its own sliding window.
  /// </summary>
  [Test]
  public async Task ReadBatches_MultipleSequentialBatches_EachIndependentAsync() {
    var (ch, batcher) = _setup(new SlidingWindowBatcherOptions {
      MaxSize = 100,
      SlidingWindow = TimeSpan.FromMilliseconds(60),
      MaxWait = TimeSpan.FromSeconds(2)
    });
    var batch1 = new TaskCompletionSource<IReadOnlyList<int>>();
    var batch2 = new TaskCompletionSource<IReadOnlyList<int>>();
    var cts = new CancellationTokenSource();
    var task = Task.Run(async () => {
      var seen = 0;
      await foreach (var batch in batcher.ReadBatchesAsync(cts.Token)) {
        seen++;
        if (seen == 1) { batch1.TrySetResult(batch); } else if (seen == 2) { batch2.TrySetResult(batch); return; }
      }
    });

    // Batch 1: items 1, 2 — window expires after 60ms quiet
    await ch.Writer.WriteAsync(1);
    await ch.Writer.WriteAsync(2);
    var b1 = await batch1.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(b1.SequenceEqual(_expected12)).IsTrue();

    // Batch 2: items 3, 4
    await ch.Writer.WriteAsync(3);
    await ch.Writer.WriteAsync(4);
    var b2 = await batch2.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(b2.SequenceEqual(_expected34)).IsTrue();

    cts.Cancel();
    try { await task; } catch (OperationCanceledException) { }
  }

  /// <summary>
  /// Constructor: invalid <c>MaxSize</c> throws.
  /// </summary>
  [Test]
  public async Task Constructor_InvalidMaxSize_ThrowsAsync() {
    var ch = Channel.CreateUnbounded<int>();
    await Assert.That(() => new SlidingWindowBatcher<int>(
      ch.Reader,
      new SlidingWindowBatcherOptions { MaxSize = 0 }))
      .Throws<ArgumentOutOfRangeException>();
  }

  /// <summary>
  /// Constructor: <c>SlidingWindow</c> larger than <c>MaxWait</c> throws.
  /// </summary>
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
