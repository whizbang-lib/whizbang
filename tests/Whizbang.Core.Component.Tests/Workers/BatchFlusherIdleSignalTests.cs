// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A flusher says when every item it was given has landed (#1899). The drain workers report idle as soon as they have
/// queued their completions, and the completions are written later by a flusher, so a caller that waited for idle and
/// then touched the work tables raced the late write. The flusher now counts every item offered to it, through
/// <see cref="BatchFlusher{T}.EnqueueAsync"/> or its <see cref="BatchFlusher{T}.Writer"/>, until the flush that carries
/// it returns or the item is given up, and reports reaching zero as an event and an awaitable.
/// </summary>
/// <remarks>
/// Every assertion waits on a signal: the flush callback entering, the idle awaitable, or a stop returning. The flush
/// is held open by the test, so "not yet idle" is asserted while the write is known to be in progress.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/BatchFlusher.cs</code-under-test>
/// <docs>fundamentals/work-coordinator/batched-flushers#when-writes-have-landed</docs>
[Category("Workers")]
public sealed class BatchFlusherIdleSignalTests {
  private static readonly BatchFlusherOptions _immediate = new() { CoalesceWindowMs = 0, MaxBatchSize = 10, ImmediateFlushThreshold = 1 };

  /// <summary>A flush the test holds open, and says when a batch has entered it.</summary>
  private sealed class HeldFlush {
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public int ItemsSeen { get; private set; }

    public void Release() => _release.TrySetResult();

    public async Task FlushAsync(IReadOnlyList<int> items, CancellationToken ct) {
      ItemsSeen += items.Count;
      _entered.TrySetResult();
      await _release.Task.WaitAsync(ct);
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task AFlusherWithNothingOffered_IsIdle_AndItsWaitIsAlreadyCompleteAsync(CancellationToken cancellationToken) {
    await using var flusher = new BatchFlusher<int>((_, _) => Task.CompletedTask, _immediate, NullLogger.Instance);

    await Assert.That(flusher.IsIdle).IsTrue();
    await Assert.That(flusher.Activity).IsEqualTo(new QueuedWriteActivity(Version: 0, Pending: 0));
    await Assert.That(flusher.WaitForIdleAsync(cancellationToken).IsCompleted).IsTrue()
      .Because("a caller waiting on a flusher that holds nothing must not wait for a write that will never come");
  }

  [Test]
  [Timeout(30000)]
  public async Task AnEnqueuedItem_IsNotIdle_UntilTheFlushCarryingItReturnsAsync(CancellationToken cancellationToken) {
    var held = new HeldFlush();
    await using var flusher = new BatchFlusher<int>(held.FlushAsync, _immediate, NullLogger.Instance);
    var idleRaised = 0;
    // The event is its own signal: it is raised after the awaitable completes, so the test waits on the event too.
    var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    flusher.OnWorkProcessingIdle += () => {
      Interlocked.Increment(ref idleRaised);
      raised.TrySetResult();
    };

    await flusher.EnqueueAsync(1, cancellationToken);
    var wait = flusher.WaitForIdleAsync(cancellationToken);
    await held.Entered.WaitAsync(cancellationToken);

    // The loop has taken the item up and the write is in progress: the item is out of the channel but has not landed.
    await Assert.That(flusher.IsIdle).IsFalse();
    await Assert.That(flusher.Pending).IsEqualTo(1);
    await Assert.That(wait.IsCompleted).IsFalse()
      .Because("the idle wait completes when the write has landed, not when the item left the channel (#1899)");
    await Assert.That(Volatile.Read(ref idleRaised)).IsEqualTo(0);

    held.Release();
    await wait;
    await raised.Task.WaitAsync(cancellationToken);

    await Assert.That(flusher.IsIdle).IsTrue();
    await Assert.That(flusher.Activity).IsEqualTo(new QueuedWriteActivity(Version: 1, Pending: 0));
    await Assert.That(Volatile.Read(ref idleRaised)).IsEqualTo(1)
      .Because("reaching zero raises the idle event once");
  }

  [Test]
  [Timeout(30000)]
  public async Task ItemsWrittenThroughTheWriter_AreCountedLikeEnqueuedOnesAsync(CancellationToken cancellationToken) {
    // Four of the five flush workers wrote through Writer, which Pending did not count, so their queued writes were
    // invisible. Every way in is counted now, so no producer can bypass the signal.
    await using var flusher = new BatchFlusher<int>((_, _) => Task.CompletedTask, _immediate, NullLogger.Instance,
      timeProvider: null, started: false);

    await flusher.Writer.WriteAsync(1, cancellationToken);
    var accepted = flusher.Writer.TryWrite(2);

    await Assert.That(accepted).IsTrue();
    await Assert.That(flusher.Pending).IsEqualTo(2);
    await Assert.That(flusher.IsIdle).IsFalse();
    await Assert.That(await flusher.Writer.WaitToWriteAsync(cancellationToken)).IsTrue();

    flusher.Start();
    await flusher.WaitForIdleAsync(cancellationToken);

    await Assert.That(flusher.Activity).IsEqualTo(new QueuedWriteActivity(Version: 2, Pending: 0));
  }

  [Test]
  [Timeout(30000)]
  public async Task ABatchDroppedAfterItsLastAttempt_NoLongerCountsAsync(CancellationToken cancellationToken) {
    await using var flusher = new BatchFlusher<int>(
      (_, _) => throw new InvalidOperationException("the database refused the write"),
      new BatchFlusherOptions { CoalesceWindowMs = 0, MaxBatchSize = 10, ImmediateFlushThreshold = 1, MaxFlushAttempts = 1 },
      NullLogger.Instance);

    await flusher.EnqueueAsync(1, cancellationToken);
    await flusher.WaitForIdleAsync(cancellationToken);

    await Assert.That(flusher.ItemsDropped).IsEqualTo(1);
    await Assert.That(flusher.IsIdle).IsTrue()
      .Because("a dropped item will never be written, so a caller waiting for it would wait forever");
  }

  [Test]
  [Timeout(30000)]
  public async Task AnUnstartedFlusherStopped_GivesUpWhatWasQueued_AndIsIdleAsync(CancellationToken cancellationToken) {
    var flusher = new BatchFlusher<int>((_, _) => Task.CompletedTask, _immediate, NullLogger.Instance,
      timeProvider: null, started: false);
    await flusher.EnqueueAsync(1, cancellationToken);
    await flusher.EnqueueAsync(2, cancellationToken);
    var wait = flusher.WaitForIdleAsync(cancellationToken);

    await flusher.DisposeAsync();
    await wait;

    await Assert.That(flusher.Activity).IsEqualTo(new QueuedWriteActivity(Version: 2, Pending: 0));
  }

  [Test]
  [Timeout(30000)]
  public async Task AWriteRefusedByAStoppedFlusher_IsNotCountedAsync(CancellationToken cancellationToken) {
    var flusher = new BatchFlusher<int>((_, _) => Task.CompletedTask, _immediate, NullLogger.Instance);
    await flusher.DisposeAsync();
    var idleRaised = 0;
    flusher.OnWorkProcessingIdle += () => Interlocked.Increment(ref idleRaised);

    var tried = flusher.Writer.TryWrite(1);
    async Task enqueueAsync() => await flusher.EnqueueAsync(2, cancellationToken);

    await Assert.That(tried).IsFalse();
    await Assert.That(enqueueAsync).Throws<ChannelClosedException>();
    await Assert.That(flusher.IsIdle).IsTrue()
      .Because("an item the channel refused was never queued, so nothing waits on it");
    await Assert.That(flusher.Activity.Pending).IsEqualTo(0);
    await Assert.That(flusher.Writer.TryComplete()).IsFalse();
    await Assert.That(Volatile.Read(ref idleRaised)).IsEqualTo(2)
      .Because("each refused item was counted for a moment and released, and releasing the last one is reaching zero");
  }

  [Test]
  [Timeout(30000)]
  public async Task AWriteCanceledWhileTheChannelIsFull_IsNotCountedAsync(CancellationToken cancellationToken) {
    await using var flusher = new BatchFlusher<int>((_, _) => Task.CompletedTask,
      new BatchFlusherOptions { ChannelCapacity = 1, CoalesceWindowMs = 0, MaxBatchSize = 10, ImmediateFlushThreshold = 1 },
      NullLogger.Instance, timeProvider: null, started: false);
    await flusher.EnqueueAsync(1, cancellationToken);
    using var canceled = new CancellationTokenSource();
    var blocked = flusher.EnqueueAsync(2, canceled.Token).AsTask();

    await canceled.CancelAsync();

    await Assert.That(async () => await blocked).Throws<OperationCanceledException>();
    await Assert.That(flusher.Pending).IsEqualTo(1)
      .Because("the item that never made it into the full channel is not pending; the one that did still is");
  }

  [Test]
  [Timeout(30000)]
  public async Task AWriteThatWaitedForRoomInAFullChannel_IsCountedOnceItIsQueuedAsync(CancellationToken cancellationToken) {
    await using var flusher = new BatchFlusher<int>((_, _) => Task.CompletedTask,
      new BatchFlusherOptions { ChannelCapacity = 1, CoalesceWindowMs = 0, MaxBatchSize = 10, ImmediateFlushThreshold = 1 },
      NullLogger.Instance, timeProvider: null, started: false);
    await flusher.EnqueueAsync(1, cancellationToken);
    var waitingForRoom = flusher.EnqueueAsync(2, cancellationToken).AsTask();
    var queuedBeforeStart = waitingForRoom.IsCompleted;

    // Starting the loop reads the first item, which makes room for the second.
    flusher.Start();
    await waitingForRoom;
    await flusher.WaitForIdleAsync(cancellationToken);

    await Assert.That(queuedBeforeStart).IsFalse();
    await Assert.That(flusher.Activity).IsEqualTo(new QueuedWriteActivity(Version: 2, Pending: 0))
      .Because("the write that waited for room was counted once and landed like any other");
  }

  [Test]
  [Timeout(30000)]
  public async Task AFlushAbandonedPastTheDrainBound_AndWhatWasStillQueued_NoLongerCountAsync(CancellationToken cancellationToken) {
    var held = new HeldFlush();
    var clock = new ArmingClock();
    var drainBound = TimeSpan.FromMilliseconds(200);
    var flusher = new BatchFlusher<int>(held.FlushAsync,
      new BatchFlusherOptions { CoalesceWindowMs = 0, MaxBatchSize = 1, ImmediateFlushThreshold = 1, DrainTimeoutMs = 200 },
      NullLogger.Instance, clock);
    await flusher.EnqueueAsync(1, cancellationToken);
    await held.Entered.WaitAsync(cancellationToken);
    // Behind the held flush: still in the channel when shutdown gives up.
    await flusher.EnqueueAsync(2, cancellationToken);

    var dispose = flusher.DisposeAsync().AsTask();
    await clock.WaitForArmedAsync(drainBound, cancellationToken);
    clock.Advance(drainBound);
    await dispose.WaitAsync(cancellationToken);

    await Assert.That(flusher.Activity).IsEqualTo(new QueuedWriteActivity(Version: 2, Pending: 0))
      .Because("shutdown gave up on both items, so neither will land, and a caller waiting for them must not hang");
    await Assert.That(flusher.WaitForIdleAsync(cancellationToken).IsCompleted).IsTrue();
  }

  [Test]
  [Timeout(30000)]
  public async Task AWait_IsEndedByItsTokenAsync(CancellationToken cancellationToken) {
    var held = new HeldFlush();
    await using var flusher = new BatchFlusher<int>(held.FlushAsync, _immediate, NullLogger.Instance);
    await flusher.EnqueueAsync(1, cancellationToken);
    using var canceled = new CancellationTokenSource();
    var wait = flusher.WaitForIdleAsync(canceled.Token);

    await canceled.CancelAsync();

    await Assert.That(async () => await wait).Throws<OperationCanceledException>();
    held.Release();
  }

  [Test]
  [Timeout(30000)]
  public async Task AReconcileThatFindsNothingChanged_RaisesNothingAsync(CancellationToken cancellationToken) {
    // Two producers crossing zero at once both reconcile; the second finds the first already did the work. That
    // interleaving is not one a test can order, so the reconcile is called directly with nothing changed.
    await using var flusher = new BatchFlusher<int>((_, _) => Task.CompletedTask, _immediate, NullLogger.Instance);
    var idleRaised = 0;
    flusher.OnWorkProcessingIdle += () => Interlocked.Increment(ref idleRaised);

    flusher.ReconcileIdle();

    await Assert.That(Volatile.Read(ref idleRaised)).IsEqualTo(0);
    await Assert.That(flusher.WaitForIdleAsync(cancellationToken).IsCompleted).IsTrue();
  }
}
