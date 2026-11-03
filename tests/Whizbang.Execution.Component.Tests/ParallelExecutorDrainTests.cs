// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Execution;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Execution.Tests;

/// <summary>
/// Drains of a <see cref="ParallelExecutor"/> that overlap each other or are canceled part way.
/// </summary>
/// <remarks>
/// <para>
/// A drain proves nothing is running by holding every concurrency slot at once. The semaphore has no
/// way to take several slots in one step, so a drain takes them one at a time; two drains doing that
/// at the same time could each end up holding some of the slots and wait forever for the rest
/// (#2005). Drains are therefore serialized: a drain takes the drain gate first, and only the holder
/// of the gate takes slots.
/// </para>
/// <para>
/// A drain whose token is canceled part way gives back the slots it had taken. Kept, they would
/// lower the executor's concurrency for the rest of its life and make every later drain wait for
/// slots nobody will release.
/// </para>
/// <para>
/// The interleavings are fixed by signals: each handler signals that it is running and is released
/// by the test, and the semaphore hands slots to waiting drains in the order they asked.
/// </para>
/// </remarks>
[Category("Execution")]
public class ParallelExecutorDrainTests {

  [Test]
  [Timeout(30000)]
  public async Task DrainAsync_TwoDrainsOverlap_BothCompleteAsync(CancellationToken cancellationToken) {
    var executor = new ParallelExecutor(maxConcurrency: 2);
    await executor.StartAsync(cancellationToken);

    var (first, releaseFirst) = await _startBlockingAsync(executor, cancellationToken);
    var (second, releaseSecond) = await _startBlockingAsync(executor, cancellationToken);

    // Both slots are busy, so each drain waits; the first asked first.
    var drainA = executor.DrainAsync(cancellationToken);
    var drainB = executor.DrainAsync(cancellationToken);

    // Each finished handler frees one slot, which goes to whichever drain is waiting at the head of
    // the semaphore's queue. Taking slots one at a time, drain A gets the first and drain B the
    // second, and each then waits for the slot the other holds.
    releaseFirst.SetResult(1);
    await first;
    releaseSecond.SetResult(2);
    await second;

    // The test's timeout token is the bound: a deadlock fails here instead of hanging the run.
    await drainA;
    await drainB;

    await Assert.That(drainA.IsCompletedSuccessfully).IsTrue()
      .Because("the first drain must finish once nothing is running");
    await Assert.That(drainB.IsCompletedSuccessfully).IsTrue()
      .Because("a second drain overlapping the first must also finish, not deadlock against it (#2005)");

    await executor.DisposeAsync();
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainAsync_CanceledAfterTakingSlots_GivesThemBackAsync(CancellationToken cancellationToken) {
    var executor = new ParallelExecutor(maxConcurrency: 2);
    await executor.StartAsync(cancellationToken);

    var (running, release) = await _startBlockingAsync(executor, cancellationToken);

    // The free slot is taken at once; the drain then waits for the slot the handler holds.
    using var drainCts = new CancellationTokenSource();
    var canceledDrain = executor.DrainAsync(drainCts.Token);
    await drainCts.CancelAsync();
    await Assert.That(async () => await canceledDrain).Throws<OperationCanceledException>()
      .Because("a drain's token bounds its wait");

    release.SetResult(1);
    await running;

    // Every slot is free now, so a drain takes them all without waiting and completes synchronously.
    var drain = executor.DrainAsync(cancellationToken);
    await Assert.That(drain.IsCompletedSuccessfully).IsTrue()
      .Because("a canceled drain has to give back the slots it took; kept, they lower the executor's "
        + "concurrency for good and every later drain waits for them forever");

    await executor.DisposeAsync();
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainAsync_CanceledBeforeTakingAnySlot_LeavesTheExecutorWholeAsync(
      CancellationToken cancellationToken) {
    var executor = new ParallelExecutor(maxConcurrency: 1);
    await executor.StartAsync(cancellationToken);

    var (running, release) = await _startBlockingAsync(executor, cancellationToken);

    // The only slot is busy, so the drain is canceled holding nothing.
    using var drainCts = new CancellationTokenSource();
    var canceledDrain = executor.DrainAsync(drainCts.Token);
    await drainCts.CancelAsync();
    await Assert.That(async () => await canceledDrain).Throws<OperationCanceledException>()
      .Because("a drain's token bounds its wait");

    release.SetResult(1);
    await running;

    var drain = executor.DrainAsync(cancellationToken);
    await Assert.That(drain.IsCompletedSuccessfully).IsTrue()
      .Because("a drain canceled before it took a slot gives back nothing and releases the gate, so "
        + "the next drain of an idle executor completes at once");

    await executor.DisposeAsync();
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainAsync_CanceledWaitingForTheGate_LeavesTheRunningDrainWholeAsync(
      CancellationToken cancellationToken) {
    var executor = new ParallelExecutor(maxConcurrency: 1);
    await executor.StartAsync(cancellationToken);

    var (running, release) = await _startBlockingAsync(executor, cancellationToken);

    // The first drain holds the gate and waits for the busy slot; the second waits for the gate.
    var holder = executor.DrainAsync(cancellationToken);
    using var waiterCts = new CancellationTokenSource();
    var waiter = executor.DrainAsync(waiterCts.Token);
    await waiterCts.CancelAsync();
    await Assert.That(async () => await waiter).Throws<OperationCanceledException>()
      .Because("a drain's token bounds its wait for the gate as well");

    release.SetResult(1);
    await running;
    await holder;

    await Assert.That(holder.IsCompletedSuccessfully).IsTrue()
      .Because("a drain canceled while waiting for the gate never held it, so it must not release it; "
        + "a release there would overfill the gate and fault the drain that does hold it");
    var drain = executor.DrainAsync(cancellationToken);
    await Assert.That(drain.IsCompletedSuccessfully).IsTrue()
      .Because("the gate and every slot are free again once the holder finishes");

    await executor.DisposeAsync();
  }

  /// <summary>
  /// Starts a handler that signals it is running and then waits for the returned source.
  /// </summary>
  private static async Task<(Task<int> Running, TaskCompletionSource<int> Release)> _startBlockingAsync(
      ParallelExecutor executor,
      CancellationToken cancellationToken) {
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var running = executor.ExecuteAsync<int>(
      _envelope(),
      async (_, _) => {
        entered.SetResult();
        return await release.Task;
      },
      null!,   // execution strategies do not read the policy context
      cancellationToken).AsTask();
    await entered.Task;
    return (running, release);
  }

  /// <summary>A locally dispatched envelope, which is all the executor reads of it.</summary>
  private static MessageEnvelope<DrainProbeMessage> _envelope() =>
    new() {
      MessageId = MessageId.New(),
      Payload = new DrainProbeMessage("drain"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  private sealed record DrainProbeMessage(string Payload) : IMessage;
}
