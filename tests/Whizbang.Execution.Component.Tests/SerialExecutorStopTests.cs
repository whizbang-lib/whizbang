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
/// What a stop does to work that is still waiting in a <see cref="SerialExecutor"/>'s channel.
/// </summary>
/// <remarks>
/// <para>
/// A stop lets the item that is already running finish and finishes every item still queued behind
/// it as canceled: the handler is not run and the caller's await ends in
/// <see cref="OperationCanceledException"/>. Running the queue is <c>DrainAsync</c>'s job; the
/// documented lifecycle says a stop does not wait for work, and a host's stop runs under the host's
/// shutdown timeout, which a deep queue would overrun.
/// </para>
/// <para>
/// Before #2004 a stop canceled the worker's read token and nothing else, so an item still in the
/// channel was finished only by chance: run when the reader happened to have it in hand, and
/// otherwise never run and never canceled, its caller waiting forever.
/// </para>
/// <para>
/// The interleaving is fixed by signals. The worker is parked inside the first handler before the
/// second item is written, and the stop's synchronous half has marked the executor stopping before
/// that handler is released, so nothing here depends on timing.
/// </para>
/// </remarks>
[Category("Execution")]
public class SerialExecutorStopTests {

  [Test]
  [Timeout(30000)]
  public async Task StopAsync_ItemQueuedBehindRunningWork_IsCanceledWithoutRunningAsync(
      CancellationToken cancellationToken) {
    var executor = new SerialExecutor();
    await executor.StartAsync(cancellationToken);

    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var running = _runAsync(
      executor,
      _envelope("running"),
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

    // Not awaited: its synchronous half marks the executor stopping while the worker is still
    // parked in the first handler, so the second item is behind the stop when the worker reaches it.
    var stop = executor.StopAsync(CancellationToken.None);
    release.SetResult(42);

    var result = await running;
    await stop;

    await Assert.That(result).IsEqualTo(42)
      .Because("a stop lets the item that is already running finish and hand its result to its caller");
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
      .Because("a stop finishes queued work as canceled rather than running it; running the queue is "
        + "what DrainAsync is for");
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
      MessageEnvelope<StopProbeMessage> envelope,
      Func<IMessageEnvelope, PolicyContext, ValueTask<int>> handler,
      CancellationToken cancellationToken) =>
    await executor.ExecuteAsync(
      envelope,
      handler,
      null!,   // execution strategies do not read the policy context
      cancellationToken);

  /// <summary>A locally dispatched envelope, which is all the executor reads of it.</summary>
  private static MessageEnvelope<StopProbeMessage> _envelope(string payload) =>
    new() {
      MessageId = MessageId.New(),
      Payload = new StopProbeMessage(payload),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  private sealed record StopProbeMessage(string Payload) : IMessage;
}
