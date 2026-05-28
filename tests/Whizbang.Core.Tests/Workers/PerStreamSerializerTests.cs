using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the stream-affinity invariants for <see cref="PerStreamSerializer{T}"/> — the
/// in-process per-stream FIFO that guarantees same-stream items are processed serially
/// regardless of how transport delivered them. See plans/stream-affinity-everywhere.md.
/// </summary>
public class PerStreamSerializerTests {
  private readonly Uuid7IdProvider _idProvider = new();

  private sealed record StreamItem(Guid? StreamId, Guid MessageId, string Tag = "");

  // ===== Same-stream serial ordering =====

  [Test]
  public async Task SameStream_SequentialEnqueue_ProcessesInArrivalOrderAsync() {
    var streamId = _idProvider.NewGuid();
    var seen = new List<Guid>();
    var lockObj = new object();

    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: async (item, ct) => {
        await Task.Yield();
        lock (lockObj) {
          seen.Add(item.MessageId);
        }
      });

    var i1 = new StreamItem(streamId, _idProvider.NewGuid());
    var i2 = new StreamItem(streamId, _idProvider.NewGuid());
    var i3 = new StreamItem(streamId, _idProvider.NewGuid());

    await sut.EnqueueAsync(i1);
    await sut.EnqueueAsync(i2);
    await sut.EnqueueAsync(i3);
    await sut.FlushAndStopAsync();

    await Assert.That(seen).IsEquivalentTo([i1.MessageId, i2.MessageId, i3.MessageId]);
  }

  // ===== Different streams parallel =====

  [Test]
  public async Task DifferentStreams_ProcessConcurrentlyAsync() {
    var streamA = _idProvider.NewGuid();
    var streamB = _idProvider.NewGuid();

    var aStarted = new TaskCompletionSource();
    var bStarted = new TaskCompletionSource();
    var canFinish = new TaskCompletionSource();

    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: async (item, ct) => {
        if (item.Tag == "A") {
          aStarted.TrySetResult();
        }
        if (item.Tag == "B") {
          bStarted.TrySetResult();
        }
        await canFinish.Task;
      });

    await sut.EnqueueAsync(new StreamItem(streamA, _idProvider.NewGuid(), "A"));
    await sut.EnqueueAsync(new StreamItem(streamB, _idProvider.NewGuid(), "B"));

    // Both processors must have started before either is allowed to finish.
    // Different-stream → parallel: this assertion times out if they were serialized.
    await Task.WhenAll(aStarted.Task, bStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

    canFinish.SetResult();
    await sut.FlushAndStopAsync();
  }

  // ===== Null stream id routes to default channel =====

  [Test]
  public async Task NullStreamId_RoutesToDefaultChannel_PreservesArrivalOrderAsync() {
    var seen = new List<Guid>();
    var lockObj = new object();

    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: async (item, ct) => {
        await Task.Yield();
        lock (lockObj) {
          seen.Add(item.MessageId);
        }
      });

    var i1 = new StreamItem(null, _idProvider.NewGuid());
    var i2 = new StreamItem(null, _idProvider.NewGuid());
    var i3 = new StreamItem(null, _idProvider.NewGuid());

    await sut.EnqueueAsync(i1);
    await sut.EnqueueAsync(i2);
    await sut.EnqueueAsync(i3);
    await sut.FlushAndStopAsync();

    await Assert.That(seen).IsEquivalentTo([i1.MessageId, i2.MessageId, i3.MessageId]);
  }

  // ===== Sort-on-drain: brief enqueue race resolves to message-id order =====

  [Test]
  public async Task SortComparer_ShuffledEnqueueWithinDrainWindow_ProcessesInComparerOrderAsync() {
    var streamId = _idProvider.NewGuid();
    var seen = new List<Guid>();
    var lockObj = new object();

    var i1 = new StreamItem(streamId, _idProvider.NewGuid());
    var i2 = new StreamItem(streamId, _idProvider.NewGuid());
    var i3 = new StreamItem(streamId, _idProvider.NewGuid());

    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: x => x.StreamId,
      processor: async (item, ct) => {
        await Task.Yield();
        lock (lockObj) {
          seen.Add(item.MessageId);
        }
      },
      options: new PerStreamSerializerOptions {
        DrainBatchWindow = TimeSpan.FromMilliseconds(100),
      },
      sortComparer: Comparer<StreamItem>.Create((a, b) => a.MessageId.CompareTo(b.MessageId)));

    // Enqueue out of order — within the drain window the items get batched, then sorted.
    await sut.EnqueueAsync(i3);
    await sut.EnqueueAsync(i1);
    await sut.EnqueueAsync(i2);

    await sut.FlushAndStopAsync();

    await Assert.That(seen).IsEquivalentTo([i1.MessageId, i2.MessageId, i3.MessageId]);
  }

  // ===== Shutdown drains pending =====

  [Test]
  public async Task FlushAndStopAsync_DrainsPendingItemsBeforeReturningAsync() {
    var streamId = _idProvider.NewGuid();
    var processed = 0;

    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: async (item, ct) => {
        await Task.Delay(10, ct);
        Interlocked.Increment(ref processed);
      });

    for (var i = 0; i < 10; i++) {
      await sut.EnqueueAsync(new StreamItem(streamId, _idProvider.NewGuid()));
    }

    await sut.FlushAndStopAsync();

    await Assert.That(processed).IsEqualTo(10);
  }

  // ===== Idle stream eviction =====

  [Test]
  public async Task IdleStream_PastEvictionWindow_DisposesChannelOnSweepAsync() {
    var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var streamId = _idProvider.NewGuid();

    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: async (item, ct) => { await Task.Yield(); },
      options: new PerStreamSerializerOptions {
        IdleEvictionWindow = TimeSpan.FromSeconds(5),
        IdleSweepInterval = TimeSpan.FromSeconds(1),
      },
      timeProvider: fakeTime);

    await sut.EnqueueAsync(new StreamItem(streamId, _idProvider.NewGuid()));
    await sut.WaitForIdleAsync(TimeSpan.FromSeconds(2));

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(1);

    fakeTime.Advance(TimeSpan.FromSeconds(10));   // past eviction
    await sut.RunIdleSweepNowAsync();

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(0);
  }

  // ===== Error isolation =====

  [Test]
  public async Task ProcessorThrows_OneStreamFails_OtherStreamsContinueAsync() {
    var streamA = _idProvider.NewGuid();
    var streamB = _idProvider.NewGuid();
    var bProcessed = 0;

    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: async (item, ct) => {
        await Task.Yield();
        if (item.Tag == "A") {
          throw new InvalidOperationException("simulated");
        }
        Interlocked.Increment(ref bProcessed);
      });

    await sut.EnqueueAsync(new StreamItem(streamA, _idProvider.NewGuid(), "A"));
    await sut.EnqueueAsync(new StreamItem(streamB, _idProvider.NewGuid(), "B"));
    await sut.EnqueueAsync(new StreamItem(streamB, _idProvider.NewGuid(), "B"));

    await sut.FlushAndStopAsync();

    // Stream B kept processing despite stream A throwing.
    await Assert.That(bProcessed).IsEqualTo(2);
  }
}
