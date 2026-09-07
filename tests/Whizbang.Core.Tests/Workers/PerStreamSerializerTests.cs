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

  // ===== Failure isolation and shutdown =====

  [Test]
  public async Task AProcessorThatThrows_DoesNotStopTheStreamsWorkerAsync() {
    // One stream's worker drains that stream serially. If a throwing item killed the worker, every
    // subsequent item for that stream would sit unprocessed for the life of the process while
    // other streams carried on — a partial outage that looks like nothing at all.
    var streamId = _idProvider.NewGuid();
    var processed = new List<Guid>();
    var lockObj = new object();

    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: (item, ct) => {
        lock (lockObj) { processed.Add(item.MessageId); }
        if (processed.Count == 1) {
          throw new InvalidOperationException("first item fails");
        }
        return Task.CompletedTask;
      });

    var failing = new StreamItem(streamId, _idProvider.NewGuid());
    var following = new StreamItem(streamId, _idProvider.NewGuid());
    await sut.EnqueueAsync(failing);
    await sut.EnqueueAsync(following);
    await sut.FlushAndStopAsync();

    int processedCount;
    lock (lockObj) { processedCount = processed.Count; }
    await Assert.That(processedCount).IsEqualTo(2)
      .Because("the item after a failure must still be processed — the caller's processor owns "
             + "retry and failure routing, this worker only has to stay alive");
  }

  [Test]
  public async Task StoppingWhileDraining_EndsWithoutSurfacingCancellationAsync() {
    // Shutdown mid-drain is not a processing failure and must not be reported as one, or every
    // deploy files an error per in-flight stream.
    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: (item, ct) => Task.CompletedTask);

    await sut.EnqueueAsync(new StreamItem(_idProvider.NewGuid(), _idProvider.NewGuid()));
    await sut.FlushAndStopAsync();
  }

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

  // ===== Guards and shutdown =====

  [Test]
  public async Task Constructor_WithNullSelector_ThrowsAsync() {
    await Assert.That(() => new PerStreamSerializer<StreamItem>(
        streamIdSelector: null!,
        processor: (_, _) => Task.CompletedTask))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullProcessor_ThrowsAsync() {
    await Assert.That(() => new PerStreamSerializer<StreamItem>(
        streamIdSelector: i => i.StreamId,
        processor: null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ActiveStreamCount_TracksDistinctStreamsAsync() {
    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: (_, _) => Task.CompletedTask);

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(0);

    await sut.EnqueueAsync(new StreamItem(_idProvider.NewGuid(), _idProvider.NewGuid()));
    await sut.EnqueueAsync(new StreamItem(_idProvider.NewGuid(), _idProvider.NewGuid()));

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(2);
  }

  [Test]
  public async Task EnqueueAsync_AfterStop_ThrowsObjectDisposedAsync() {
    var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: (_, _) => Task.CompletedTask);

    await sut.FlushAndStopAsync(CancellationToken.None);

    await Assert.That(async () =>
        await sut.EnqueueAsync(new StreamItem(null, _idProvider.NewGuid())))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task FlushAndStopAsync_CalledTwice_IsIdempotentAsync() {
    // DisposeAsync routes here too, so an explicit stop inside a using-block must not
    // double-dispose the stop token source.
    var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: (_, _) => Task.CompletedTask);

    await sut.FlushAndStopAsync(CancellationToken.None);
    await sut.FlushAndStopAsync(CancellationToken.None);
    await sut.DisposeAsync();

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(0);
  }

  [Test]
  public async Task FlushAndStopAsync_WithCanceledToken_AbandonsTheDrainAsync() {
    // Shutdown deadline reached with a processor still running: the drain is abandoned
    // and the stop token canceled, rather than waiting on it forever.
    var releaseProcessor = new TaskCompletionSource();
    var processorEntered = new TaskCompletionSource();

    var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: async (_, _) => {
        processorEntered.TrySetResult();
        await releaseProcessor.Task;
      });

    await sut.EnqueueAsync(new StreamItem(_idProvider.NewGuid(), _idProvider.NewGuid()));
    await processorEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await sut.FlushAndStopAsync(cts.Token);

    releaseProcessor.TrySetResult();
  }

  [Test]
  public async Task NullStreamId_SharesOneChannelAsync() {
    // A null stream id is not "no stream": those items still serialise against each
    // other, so they share the default keyed channel rather than one channel each.
    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: (_, _) => Task.CompletedTask);

    await sut.EnqueueAsync(new StreamItem(null, _idProvider.NewGuid()));
    await sut.EnqueueAsync(new StreamItem(null, _idProvider.NewGuid()));

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(1);
  }

  // ===== Idle sweep guards and races =====

  [Test]
  public async Task RunIdleSweepNowAsync_AfterDispose_DoesNotEvictStaleEntriesAsync() {
    // A sweep that races a completed shutdown must be inert. Without the disposed guard, a timer
    // callback firing after DisposeAsync would still walk _streams and tear down "idle" channels —
    // touching state that shutdown already finished with, right when nothing else is watching it.
    var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var streamId = _idProvider.NewGuid();

    var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: (_, _) => Task.CompletedTask,
      options: new PerStreamSerializerOptions {
        IdleEvictionWindow = TimeSpan.FromSeconds(1),
        IdleSweepInterval = TimeSpan.FromSeconds(1),
      },
      timeProvider: fakeTime);

    await sut.EnqueueAsync(new StreamItem(streamId, _idProvider.NewGuid()));
    await sut.DisposeAsync();

    fakeTime.Advance(TimeSpan.FromSeconds(10));   // well past the eviction window
    await sut.RunIdleSweepNowAsync();

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(1)
      .Because("the disposed guard must return before touching _streams — a post-dispose sweep " +
               "must leave an already-torn-down channel alone rather than reprocessing it");
  }

  [Test]
  public async Task ActiveStream_WithinEvictionWindow_SurvivesSweepAsync() {
    // Mirror of IdleStream_PastEvictionWindow_DisposesChannelOnSweepAsync: a stream that's merely
    // quiet for a moment, not idle past the window, must not be torn down by a sweep — evicting a
    // live stream forces the very next item to pay for a fresh channel + worker for no reason.
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

    fakeTime.Advance(TimeSpan.FromSeconds(2));   // still short of the 5s eviction window
    await sut.RunIdleSweepNowAsync();

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(1)
      .Because("a stream inside its eviction window is merely quiet, not idle — sweeping it away " +
               "would cost a channel rebuild on the very next item for no reason");
  }

  [Test]
  public async Task IdleSweep_OneStreamsWorkerFaults_OtherStreamsStillEvictAsync() {
    // The sweep awaits each evicted stream's worker so it can finish draining. A worker that
    // faults outside its per-item guard (e.g. a throwing sort comparer) must not abort the sweep
    // loop, or one bad stream would leave every other idle stream's channel dangling forever.
    var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var throwingStreamId = _idProvider.NewGuid();
    var healthyStreamId = _idProvider.NewGuid();
    var healthyProcessed = 0;
    // The sweep completes the healthy stream's channel and its worker drains on the thread pool,
    // so "the sweep returned" is not the same as "the item was processed". Wait on the processor
    // itself rather than on the sweep call.
    var healthyDrained = new TaskCompletionSource();

    var throwingComparer = Comparer<StreamItem>.Create((_, _) =>
      throw new InvalidOperationException("sort comparer boom"));

    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: (item, ct) => {
        if (item.Tag == "healthy") {
          Interlocked.Increment(ref healthyProcessed);
          healthyDrained.TrySetResult();
        }
        return Task.CompletedTask;
      },
      options: new PerStreamSerializerOptions {
        DrainBatchWindow = TimeSpan.FromSeconds(30),
        StreamChannelCapacity = 2,
        IdleEvictionWindow = TimeSpan.FromSeconds(5),
        IdleSweepInterval = TimeSpan.FromSeconds(1),
      },
      sortComparer: throwingComparer,
      timeProvider: fakeTime);

    // Two same-stream items so batch.Count > 1 and Sort actually runs (and throws).
    await sut.EnqueueAsync(new StreamItem(throwingStreamId, _idProvider.NewGuid()));
    await sut.EnqueueAsync(new StreamItem(throwingStreamId, _idProvider.NewGuid()));
    await sut.EnqueueAsync(new StreamItem(healthyStreamId, _idProvider.NewGuid(), "healthy"));

    fakeTime.Advance(TimeSpan.FromSeconds(10));   // both streams now past the eviction window
    await sut.RunIdleSweepNowAsync();
    await healthyDrained.Task.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(0)
      .Because("the faulting stream's worker throwing out of the sweep's await must not stop the " +
               "loop from reaching and evicting the still-idle healthy stream");
    await Assert.That(healthyProcessed).IsEqualTo(1)
      .Because("the healthy stream must still have drained its item despite the other stream's " +
               "worker faulting during eviction");
  }

  // ===== WaitForIdleAsync waits for in-flight work =====

  [Test]
  public async Task WaitForIdleAsync_ItemsStillQueuedBehindInFlightWork_WaitsUntilDrainedAsync() {
    // Every existing caller happens to find the channel already empty on the first check. If the
    // poll loop's "still busy, wait and recheck" tail ever stopped looping, WaitForIdleAsync would
    // return early while real work is still queued — callers would then read state that has not
    // been written yet.
    var streamId = _idProvider.NewGuid();
    var processorStarted = new TaskCompletionSource();
    var releaseProcessor = new TaskCompletionSource();
    var processedOrder = new List<Guid>();

    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: async (item, ct) => {
        if (processedOrder.Count == 0) {
          processorStarted.TrySetResult();
          await releaseProcessor.Task;
        }
        processedOrder.Add(item.MessageId);
      },
      options: new PerStreamSerializerOptions {
        DrainBatchWindow = TimeSpan.Zero,
      });

    var i1 = new StreamItem(streamId, _idProvider.NewGuid());
    var i2 = new StreamItem(streamId, _idProvider.NewGuid());
    await sut.EnqueueAsync(i1);
    await processorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await sut.EnqueueAsync(i2);   // sits in the channel — the worker is blocked processing i1

    var waitTask = sut.WaitForIdleAsync(TimeSpan.FromSeconds(5));
    releaseProcessor.TrySetResult();

    await waitTask;

    await Assert.That(processedOrder).IsEquivalentTo([i1.MessageId, i2.MessageId])
      .Because("WaitForIdleAsync must not return while an item is still queued behind in-flight " +
               "work — it has to loop and recheck, not just sample the queue once");
  }

  // ===== Drain batch window bounded by capacity =====

  [Test]
  public async Task DrainBatch_ReachesStreamCapacity_ProcessesWithoutWaitingOutTheWindowAsync() {
    // The drain-window loop keeps buffering same-stream arrivals until the window elapses.
    // Without the capacity break, a hot stream producing faster than the window closes could grow
    // an unbounded batch while the window is still open — this bounds it at StreamChannelCapacity.
    var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var streamId = _idProvider.NewGuid();
    var seen = new List<Guid>();
    var lockObj = new object();

    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: (item, ct) => {
        lock (lockObj) { seen.Add(item.MessageId); }
        return Task.CompletedTask;
      },
      options: new PerStreamSerializerOptions {
        DrainBatchWindow = TimeSpan.FromSeconds(30),   // the fake clock never advances this far
        StreamChannelCapacity = 2,
      },
      timeProvider: fakeTime);

    var i1 = new StreamItem(streamId, _idProvider.NewGuid());
    var i2 = new StreamItem(streamId, _idProvider.NewGuid());
    await sut.EnqueueAsync(i1);
    await sut.EnqueueAsync(i2);

    using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await sut.FlushAndStopAsync(stopCts.Token);

    await Assert.That(seen).IsEquivalentTo([i1.MessageId, i2.MessageId])
      .Because("hitting StreamChannelCapacity must end the drain window immediately — if it instead "
             + "waited out the full 30s window (which the fake clock never reaches), shutdown would "
             + "hang until this safety-net timeout aborted it");
  }

  // ===== Mid-drain cancellation observed by the processor =====

  [Test]
  public async Task FlushAndStopAsync_WithPreCanceledToken_StopsProcessorMidItemAsync() {
    // FlushAndStopAsync's WaitAsync(cancellationToken) throwing immediately on an already-canceled
    // token cancels _stopCts right away, without waiting on the still-running worker. A processor
    // that checks its own ct parameter must observe that cancellation and stop cleanly instead of
    // running to completion unchecked after shutdown was requested.
    var streamId = _idProvider.NewGuid();
    var processorStarted = new TaskCompletionSource();
    var releaseProcessor = new TaskCompletionSource();
    var processorObserved = new TaskCompletionSource();
    var observedCanceled = false;

    var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: async (item, ct) => {
        processorStarted.TrySetResult();
        await releaseProcessor.Task;
        try {
          ct.ThrowIfCancellationRequested();
        } catch (OperationCanceledException) {
          observedCanceled = true;
          throw;
        } finally {
          processorObserved.TrySetResult();
        }
      });

    await sut.EnqueueAsync(new StreamItem(streamId, _idProvider.NewGuid()));
    await processorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    using var preCanceled = new CancellationTokenSource();
    await preCanceled.CancelAsync();
    await sut.FlushAndStopAsync(preCanceled.Token);

    releaseProcessor.TrySetResult();
    await processorObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(observedCanceled).IsTrue()
      .Because("mid-item cancellation must reach the processor's own ct parameter, not just "
             + "abandon the drain silently at the FlushAndStopAsync boundary");
  }

  [Test]
  public async Task FlushAndStopAsync_WithPreCanceledToken_SkipsRemainingBatchedItemsAsync() {
    // Once shutdown observes a pre-canceled token, _stopCts is canceled immediately without
    // waiting for the running worker. The per-item loop must still check that before invoking the
    // next batched item — otherwise a "stopped" serializer would keep starting brand-new work
    // after the operator asked it to stop.
    var streamId = _idProvider.NewGuid();
    var processorStarted = new TaskCompletionSource();
    var releaseProcessor = new TaskCompletionSource();
    var secondItemRan = false;

    var i1 = new StreamItem(streamId, _idProvider.NewGuid());
    var i2 = new StreamItem(streamId, _idProvider.NewGuid());

    var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: x => x.StreamId,
      processor: async (item, ct) => {
        if (item.MessageId == i1.MessageId) {
          processorStarted.TrySetResult();
          await releaseProcessor.Task;
          return;
        }
        secondItemRan = true;
      },
      options: new PerStreamSerializerOptions {
        StreamChannelCapacity = 2,
      });

    await sut.EnqueueAsync(i1);
    await sut.EnqueueAsync(i2);   // batched with i1 — capacity 2 ends the drain window immediately
    await processorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    using var preCanceled = new CancellationTokenSource();
    await preCanceled.CancelAsync();
    await sut.FlushAndStopAsync(preCanceled.Token);

    releaseProcessor.TrySetResult();

    await Assert.That(secondItemRan).IsFalse()
      .Because("item 2 was already pulled into the same batch as item 1; once _stopCts is "
             + "canceled the loop must skip it rather than starting new work after shutdown");
  }
}
