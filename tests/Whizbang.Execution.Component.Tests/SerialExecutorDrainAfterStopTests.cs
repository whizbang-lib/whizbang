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
/// A stop that lands while a drain is already waiting on the worker: both complete, the running
/// item finishes, and the item still queued is finished as canceled.
/// </summary>
/// <remarks>
/// <para>
/// <c>DrainAsync</c> completes the channel writer and then awaits the worker. Everything up to that
/// await is synchronous, so a caller that does not await the returned task has the drain suspended
/// on the worker with the channel already completed, which is the interleaving a host produces when
/// a shutdown overlaps a drain.
/// </para>
/// <para>
/// The stop wins for work that has not started: the drain asked for the queue to run, the stop asked
/// for it not to, and a stop is the stronger request because it runs under the host's shutdown
/// timeout. The drain still completes, rather than faulting with the stop's cancellation, because
/// the worker ends normally once it has finished every item.
/// </para>
/// <para>
/// Before #2004 the stop canceled the worker's read token, so the worker task ended canceled and the
/// drain had to swallow that, and an item that was still queued could be left with neither a result
/// nor a cancellation.
/// </para>
/// <para>
/// The interleaving is deterministic, not timed. Each step waits on a signal or on the synchronous
/// part of a call; nothing sleeps and nothing polls.
/// </para>
/// </remarks>
[Category("Execution")]
public class SerialExecutorDrainAfterStopTests {

  [Test]
  [Timeout(30000)]
  public async Task DrainAsync_StopLandsWhileTheDrainAwaitsTheWorker_BothCompleteAndQueuedWorkIsCanceledAsync(
      CancellationToken cancellationToken) {
    var executor = new SerialExecutor();
    await executor.StartAsync(cancellationToken);

    // Park the worker inside a handler so there is work running and work queued when the drain and
    // the stop land.
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var inflight = _runAsync(
      executor,
      _envelope("drain-after-stop"),
      async (_, _) => {
        entered.SetResult();
        return await release.Task;
      },
      cancellationToken);
    await entered.Task;

    var queuedHandlerRuns = 0;
    var queued = executor.ExecuteAsync(
      _envelope("queued"),
      (_, _) => {
        Interlocked.Increment(ref queuedHandlerRuns);
        return ValueTask.FromResult(7);
      },
      null!,   // execution strategies do not read the policy context
      cancellationToken);

    // Not awaited: the synchronous half runs now, so the state check passes and the writer is
    // completed, and the task is left suspended on the worker.
    var drain = executor.DrainAsync(cancellationToken);
    // Also not awaited: its synchronous half flips the state and marks the executor stopping.
    var stop = executor.StopAsync(CancellationToken.None);

    release.SetResult(42);
    var result = await inflight;

    await drain;
    await stop;

    await Assert.That(result).IsEqualTo(42)
      .Because("work that was already running has to finish and reach its caller; a stop overlapping a "
        + "drain must not lose a result that was in flight");
    await Assert.That(drain.IsCompletedSuccessfully).IsTrue()
      .Because("a caller who asked only to drain must not be handed the stop's cancellation");
    // The caller resumes on the pool after the executor finishes it, so this waits, bounded by the
    // test's timeout token: a stranded caller fails here instead of hanging the run.
    Exception? finished = null;
    try {
      await queued.AsTask().WaitAsync(cancellationToken);
    } catch (Exception ex) {
      finished = ex;
    }
    await Assert.That(cancellationToken.IsCancellationRequested).IsFalse()
      .Because("every item accepted before the stop is finished by the executor; reaching the test's "
        + "bound means the caller was stranded");
    await Assert.That(finished).IsTypeOf<OperationCanceledException>()
      .Because("the stop wins over the drain for work that has not started: it is finished as canceled");
    await Assert.That(queuedHandlerRuns).IsEqualTo(0)
      .Because("a handler the stop canceled must not run");

    await executor.DisposeAsync();
  }

  /// <summary>
  /// Awaits the executor's value task once, inside an async method, so the caller holds a task it
  /// can inspect and await more than once.
  /// </summary>
  private static async Task<int> _runAsync(
      SerialExecutor executor,
      MessageEnvelope<DrainProbeMessage> envelope,
      Func<IMessageEnvelope, PolicyContext, ValueTask<int>> handler,
      CancellationToken cancellationToken) =>
    await executor.ExecuteAsync(
      envelope,
      handler,
      null!,   // execution strategies do not read the policy context
      cancellationToken);

  /// <summary>A locally dispatched envelope, which is all the executor reads of it.</summary>
  private static MessageEnvelope<DrainProbeMessage> _envelope(string payload) =>
    new() {
      MessageId = MessageId.New(),
      Payload = new DrainProbeMessage(payload),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  private sealed record DrainProbeMessage(string Payload) : IMessage;
}
