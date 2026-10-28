// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Change-level tests for <see cref="SlidingWindowBatcher{T}"/>. Every test runs the batcher on a fake clock, so a
/// window or maximum wait closes only when the test advances the clock, never because a loaded runner was slow. A
/// test that moves time does it from <see cref="WaitHookReader"/>, which runs on the batcher's own thread as each
/// wait for the next item begins: the batcher arms the window's deadline before that wait (pinned by
/// <see cref="ReadBatches_WindowElapsesAsTheInWindowWaitBegins_StillFlushesAsync"/>), so an advance made there always
/// finds the deadline it means to move past.
/// </summary>
public class SlidingWindowBatcherTests {
  private static readonly int[] _expected123 = [1, 2, 3];
  private static readonly int[] _expectedOne = [1];
  private static readonly int[] _expected01234 = [0, 1, 2, 3, 4];
  private static readonly int[] _expected12 = [1, 2];
  private static readonly int[] _expected34 = [3, 4];

  private static readonly SlidingWindowBatcherOptions _defaultOptions = new() {
    MaxSize = 100,
    SlidingWindow = TimeSpan.FromMilliseconds(50),
    MaxWait = TimeSpan.FromSeconds(1)
  };

  /// <summary>
  /// A batcher over a fresh channel on a frozen clock. <paramref name="onWait"/> runs on the batcher's thread as each
  /// wait for the next item begins, numbered from one: the first wait of a batch is made with no deadline armed, and
  /// each later one with the batch's deadline already armed.
  /// </summary>
  private static (Channel<int> ch, SlidingWindowBatcher<int> batcher) _setup(
      SlidingWindowBatcherOptions? options = null, Action<Channel<int>, FakeTimeProvider, int>? onWait = null) {
    var ch = Channel.CreateUnbounded<int>();
    var clock = new FakeTimeProvider();
    ChannelReader<int> reader = onWait is null
      ? ch.Reader
      : new WaitHookReader(ch.Reader, wait => onWait(ch, clock, wait));
    var batcher = new SlidingWindowBatcher<int>(reader, options ?? _defaultOptions, clock);
    return (ch, batcher);
  }

  /// <summary>
  /// Sliding-window debounce: 3 arrivals within the quiet window. After arrivals stop,
  /// the window expires and the batch flushes with all 3.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ReadBatches_ThreeArrivalsWithinSlidingWindow_FlushesAsOneBatchAsync(CancellationToken cancellationToken) {
    var window = TimeSpan.FromMilliseconds(500);
    var gap = TimeSpan.FromMilliseconds(20);
    // Items 2 and 3 each arrive 20 ms into the window the previous arrival opened; once the third is in, the
    // window passes with no further arrival.
    var (ch, batcher) = _setup(new SlidingWindowBatcherOptions {
      MaxSize = 100,
      SlidingWindow = window,
      MaxWait = TimeSpan.FromSeconds(5)
    }, (channel, time, wait) => {
      if (wait is 2 or 3) {
        time.Advance(gap);
        channel.Writer.TryWrite(wait);
      } else if (wait == 4) {
        time.Advance(window);
      }
    });
    var firstBatch = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var consumeTask = Task.Run(async () => firstBatch.TrySetResult(await batcher.ReadBatchesAsync(cts.Token).FirstAsync(cts.Token)), cancellationToken);

    await ch.Writer.WriteAsync(1, cancellationToken);

    var batch = await firstBatch.Task.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    try { await consumeTask; } catch (OperationCanceledException) { /* cancellation is the expected way out */ }

    await Assert.That(batch.SequenceEqual(_expected123)).IsTrue();
  }

  /// <summary>
  /// MaxSize cap: items arrive faster than the sliding window. Batch flushes immediately at
  /// MaxSize (5).
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ReadBatches_HitsMaxSize_FlushesImmediatelyAsync(CancellationToken cancellationToken) {
    // The clock is never advanced, so neither the window nor MaxWait can close the batch: the only way it arrives
    // at all is the MaxSize flush.
    var (ch, batcher) = _setup(new SlidingWindowBatcherOptions {
      MaxSize = 5,
      SlidingWindow = TimeSpan.FromSeconds(30),
      MaxWait = TimeSpan.FromSeconds(60)
    });
    var firstBatch = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var consumeTask = Task.Run(async () => firstBatch.TrySetResult(await batcher.ReadBatchesAsync(cts.Token).FirstAsync(cts.Token)), cancellationToken);

    for (var i = 0; i < 5; i++) {
      await ch.Writer.WriteAsync(i, cancellationToken);
    }

    // Flushes at MaxSize=5 without waiting for the 30 s window.
    var batch = await firstBatch.Task.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    try { await consumeTask; } catch (OperationCanceledException) { /* cancellation is the expected way out */ }

    await Assert.That(batch.SequenceEqual(_expected01234)).IsTrue();
  }

  /// <summary>
  /// MaxWait hard cap: arrivals keep coming inside the sliding window so the debounce never
  /// expires, but MaxWait flushes the batch eventually.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ReadBatches_BusyProducer_FlushesAtMaxWaitAsync(CancellationToken cancellationToken) {
    // A busy producer: an item every 600 ms against a 1000 ms window, so the debounce never expires. The fourth
    // in-window wait comes 1800 ms after the first arrival with the window open until 2800 ms; advancing to the
    // 2000 ms MaxWait, short of that, flushes only if MaxWait is honored. Were it not, the batch would never
    // arrive and the test would fail on its timeout.
    var gap = TimeSpan.FromMilliseconds(600);
    var maxWait = TimeSpan.FromMilliseconds(2000);
    var (ch, batcher) = _setup(new SlidingWindowBatcherOptions {
      MaxSize = 100,
      SlidingWindow = TimeSpan.FromMilliseconds(1000),
      MaxWait = maxWait
    }, (channel, time, wait) => {
      if (wait is >= 2 and <= 4) {
        time.Advance(gap);
        channel.Writer.TryWrite(wait);
      } else if (wait == 5) {
        time.Advance(maxWait - (gap * 3));
      }
    });
    var firstBatch = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var consumeTask = Task.Run(async () => firstBatch.TrySetResult(await batcher.ReadBatchesAsync(cts.Token).FirstAsync(cts.Token)), cancellationToken);

    await ch.Writer.WriteAsync(1, cancellationToken);

    var batch = await firstBatch.Task.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    try { await consumeTask; } catch (OperationCanceledException) { /* cancellation is the expected way out */ }

    // MaxWait flushed a multi-item batch that includes the first arrival, and it flushed because
    // of MaxWait rather than MaxSize (count stays well under the 100 cap).
    await Assert.That(batch.Count).IsEqualTo(4);
    await Assert.That(batch.Count).IsLessThan(100);
    await Assert.That(batch.Contains(1)).IsTrue();
  }

  /// <summary>
  /// Empty channel close: no batches are yielded.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ReadBatches_ChannelCompletesEmpty_TerminatesWithNoBatchesAsync(CancellationToken cancellationToken) {
    var (ch, batcher) = _setup();
    var batches = new List<IReadOnlyList<int>>();

    var task = Task.Run(async () => {
      await foreach (var batch in batcher.ReadBatchesAsync(cancellationToken)) {
        batches.Add(batch);
      }
    }, cancellationToken);

    ch.Writer.Complete();
    await task.WaitAsync(cancellationToken);
    await Assert.That(batches).IsEmpty();
  }

  /// <summary>
  /// Channel closes mid-batch: pending items are still yielded.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ReadBatches_ChannelClosesWithPendingItems_FlushesPendingBatchAsync(CancellationToken cancellationToken) {
    // The channel closes while the batch is waiting inside its window, which the frozen clock holds open
    var windowOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var (ch, batcher) = _setup(onWait: (_, _, wait) => {
      if (wait == 2) {
        windowOpen.TrySetResult();
      }
    });
    var batches = new List<IReadOnlyList<int>>();

    var task = Task.Run(async () => {
      await foreach (var batch in batcher.ReadBatchesAsync(cancellationToken)) {
        batches.Add(batch);
      }
    }, cancellationToken);

    await ch.Writer.WriteAsync(1, cancellationToken);
    await ch.Writer.WriteAsync(2, cancellationToken);
    await windowOpen.Task.WaitAsync(cancellationToken);
    ch.Writer.Complete();

    await task.WaitAsync(cancellationToken);
    await Assert.That(batches).Count().IsEqualTo(1);
    await Assert.That(batches[0].SequenceEqual(_expected12)).IsTrue();
  }

  /// <summary>
  /// Cancellation: cancelling mid-wait terminates the enumerator promptly.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ReadBatches_CancellationDuringWait_TerminatesAsync(CancellationToken cancellationToken) {
    // Canceled while the batch is waiting inside its window, which the frozen clock holds open
    var windowOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var (ch, batcher) = _setup(onWait: (_, _, wait) => {
      if (wait == 2) {
        windowOpen.TrySetResult();
      }
    });
    using var cts = new CancellationTokenSource();
    var task = Task.Run(async () => {
      await foreach (var _ in batcher.ReadBatchesAsync(cts.Token)) { /* no-op callback */ }
    }, cancellationToken);

    await ch.Writer.WriteAsync(1, cancellationToken);
    await windowOpen.Task.WaitAsync(cancellationToken);
    await cts.CancelAsync();

    await task.WaitAsync(cancellationToken);
    await Assert.That(task.IsCompleted).IsTrue();
  }

  /// <summary>
  /// Multiple sequential batches: after one batch flushes, the next arrival starts a fresh
  /// batch with its own sliding window.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ReadBatches_MultipleSequentialBatches_EachIndependentAsync(CancellationToken cancellationToken) {
    var window = TimeSpan.FromMilliseconds(60);
    // Wait 1 finds items 1 and 2 already written. Wait 2 is batch 1's in-window wait: the window passes quiet.
    // Wait 3 is the next batch's first wait, made with no deadline armed: items 3 and 4 arrive. Wait 4 is batch 2's
    // in-window wait: its own window passes quiet.
    var (ch, batcher) = _setup(new SlidingWindowBatcherOptions {
      MaxSize = 100,
      SlidingWindow = window,
      MaxWait = TimeSpan.FromSeconds(2)
    }, (channel, time, wait) => {
      if (wait is 2 or 4) {
        time.Advance(window);
      } else if (wait == 3) {
        channel.Writer.TryWrite(3);
        channel.Writer.TryWrite(4);
      }
    });
    var batch1 = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var batch2 = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    // Batch 1: items 1, 2, written before the batcher starts reading
    await ch.Writer.WriteAsync(1, cancellationToken);
    await ch.Writer.WriteAsync(2, cancellationToken);
    var task = Task.Run(async () => {
      var seen = 0;
      await foreach (var batch in batcher.ReadBatchesAsync(cts.Token)) {
        seen++;
        if (seen == 1) { batch1.TrySetResult(batch); } else if (seen == 2) { batch2.TrySetResult(batch); return; }
      }
    }, cancellationToken);

    var b1 = await batch1.Task.WaitAsync(cancellationToken);
    await Assert.That(b1.SequenceEqual(_expected12)).IsTrue();

    // Batch 2: items 3, 4
    var b2 = await batch2.Task.WaitAsync(cancellationToken);
    await Assert.That(b2.SequenceEqual(_expected34)).IsTrue();

    await cts.CancelAsync();
    try { await task; } catch (OperationCanceledException) { /* cancellation is the expected way out */ }
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

  /// <summary>Wraps a reader and runs a callback on the consumer's thread as each wait begins.</summary>
  private sealed class WaitHookReader(ChannelReader<int> inner, Action<int> onWait) : ChannelReader<int> {
    private int _waits;
    public override bool TryRead(out int item) => inner.TryRead(out item);
    public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) {
      onWait(Interlocked.Increment(ref _waits));
      return inner.WaitToReadAsync(cancellationToken);
    }
  }

  /// <summary>
  /// The window's deadline must be armed before the batcher starts waiting for the next item. Time that passes
  /// between the two must count against the window: here the whole window elapses at the exact moment the
  /// in-window wait begins, and the batch must still flush. A deadline armed after the wait would be measured
  /// from the already-advanced clock and never fire, which is the ordering that made fake-clock tests of this
  /// class unreliable under load.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ReadBatches_WindowElapsesAsTheInWindowWaitBegins_StillFlushesAsync(CancellationToken cancellationToken) {
    var clock = new FakeTimeProvider();
    var ch = Channel.CreateUnbounded<int>();
    var reader = new WaitHookReader(ch.Reader, wait => {
      if (wait == 2) {
        clock.Advance(TimeSpan.FromMilliseconds(100)); // past the 50 ms sliding window, short of MaxWait
      }
    });
    var batcher = new SlidingWindowBatcher<int>(reader, new SlidingWindowBatcherOptions {
      MaxSize = 100,
      SlidingWindow = TimeSpan.FromMilliseconds(50),
      MaxWait = TimeSpan.FromSeconds(1)
    }, clock);
    ch.Writer.TryWrite(1);

    await using var batches = batcher.ReadBatchesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
    var moved = await batches.MoveNextAsync().AsTask().WaitAsync(cancellationToken);

    await Assert.That(moved).IsTrue();
    await Assert.That(batches.Current).IsEquivalentTo(_expectedOne);
  }
}
