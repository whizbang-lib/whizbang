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
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Execution.Tests;

/// <summary>
/// A <see cref="SerialExecutor"/> drain bounded by its cancellation token (#2011).
/// </summary>
/// <remarks>
/// <para>
/// The token bounds the caller's wait and nothing else. A drain canceled while it waits stops
/// waiting and reports <see cref="OperationCanceledException"/>; the channel it already closed stays
/// closed and the worker keeps running the queue, so a later drain or stop finds the executor as the
/// first drain left it. A drain whose token is canceled before it starts changes nothing: the
/// executor keeps accepting work.
/// </para>
/// <para>
/// Before #2011 the token was never read, so a drain waited for the whole queue however its caller
/// asked it to stop, and a drain handed an already-canceled token closed the channel and returned as
/// if it had succeeded.
/// </para>
/// <para>
/// The interleaving is fixed by signals: the worker is parked inside a handler before the drain is
/// canceled, and released only after the cancellation has been observed.
/// </para>
/// </remarks>
[Category("Execution")]
public class SerialExecutorDrainCancellationTests {

  [Test]
  [Timeout(30000)]
  public async Task DrainAsync_CanceledWhileWaiting_StopsWaitingAndTheQueueStillRunsAsync(
      CancellationToken cancellationToken) {
    var executor = new SerialExecutor();
    await executor.StartAsync(cancellationToken);

    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var running = _runAsync(
      executor,
      async (_, _) => {
        entered.SetResult();
        return await release.Task;
      },
      cancellationToken);
    await entered.Task;
    var queued = _runAsync(executor, (_, _) => ValueTask.FromResult(7), cancellationToken);

    using var drainCts = new CancellationTokenSource();
    var drain = executor.DrainAsync(drainCts.Token);
    await drainCts.CancelAsync();

    // Bounded by the test's timeout token: a drain that ignores its own token waits on the parked
    // handler and fails here instead of hanging the run.
    Exception? drainOutcome = null;
    try {
      await drain.WaitAsync(cancellationToken);
    } catch (Exception ex) {
      drainOutcome = ex;
    }
    await Assert.That(cancellationToken.IsCancellationRequested).IsFalse()
      .Because("the drain must stop waiting when its own token is canceled; reaching the test's bound "
        + "means it ignored the token");
    await Assert.That(drainOutcome).IsAssignableTo<OperationCanceledException>()
      .Because("a drain canceled through its token reports the cancellation to its caller");

    release.SetResult(42);
    await Assert.That(await running).IsEqualTo(42)
      .Because("canceling a drain's wait does not touch the item that is running");
    await Assert.That(await queued).IsEqualTo(7)
      .Because("the token bounds only the caller's wait: the worker keeps running the queue the drain "
        + "had already closed, rather than the cancellation stopping the executor");

    var secondDrain = executor.DrainAsync(cancellationToken);
    await secondDrain;
    await Assert.That(secondDrain.IsCompletedSuccessfully).IsTrue()
      .Because("the executor is left consistent: a later drain completes once the queue is done");

    await executor.DisposeAsync();
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainAsync_TokenAlreadyCanceled_LeavesTheExecutorAcceptingWorkAsync(
      CancellationToken cancellationToken) {
    var executor = new SerialExecutor();
    await executor.StartAsync(cancellationToken);

    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();
    await Assert.That(async () => await executor.DrainAsync(canceled.Token)).Throws<OperationCanceledException>()
      .Because("a drain whose token is already canceled reports the cancellation instead of draining");

    var result = await _runAsync(executor, (_, _) => ValueTask.FromResult(5), cancellationToken);
    await Assert.That(result).IsEqualTo(5)
      .Because("a drain canceled before it started changes nothing: the channel is still open and the "
        + "executor still accepts work");

    await executor.DisposeAsync();
  }

  /// <summary>
  /// Awaits the executor's value task once, inside an async method, so the caller holds a task it
  /// can await later.
  /// </summary>
  private static async Task<int> _runAsync(
      SerialExecutor executor,
      Func<IMessageEnvelope, PolicyContext, ValueTask<int>> handler,
      CancellationToken cancellationToken) =>
    await executor.ExecuteAsync(
      _envelope(),
      handler,
      null!,   // execution strategies do not read the policy context
      cancellationToken);

  /// <summary>A locally dispatched envelope, which is all the executor reads of it.</summary>
  private static MessageEnvelope<DrainCancellationProbe> _envelope() =>
    new() {
      MessageId = MessageId.New(),
      Payload = new DrainCancellationProbe("drain"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  private sealed record DrainCancellationProbe(string Payload) : IMessage;
}
