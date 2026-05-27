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
}
