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
using Whizbang.Core.Pooling;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Execution.Tests;

/// <summary>
/// A <see cref="SerialExecutor.ExecuteAsync{TResult}"/> whose enqueue fails: the caller is told why
/// and the pooled execution state goes back to its pool (#2012).
/// </summary>
/// <remarks>
/// <para>
/// <c>ExecuteAsync</c> checks that the executor is running and then writes to the channel. A stop can
/// close the channel between the two, and on a bounded channel a writer waiting for room is failed
/// when the channel closes. Either way the work was never queued. The caller gets
/// <see cref="OperationCanceledException"/>, the same as an item that was queued when the stop landed,
/// rather than the channel's own <c>ChannelClosedException</c>. A caller whose own token cancels the
/// wait for room gets that cancellation unchanged.
/// </para>
/// <para>
/// In both cases the execution state rented from <see cref="ExecutionStatePool{T}"/> is reset and
/// returned, because no worker will ever see it. Each test uses a result type of its own, so the
/// pool it observes is touched by nothing else: it seeds that pool with one known instance, which is
/// the instance the executor rents, and finds it there again afterward.
/// </para>
/// <para>
/// The interleaving is deterministic: with a capacity of one, the worker parked in a handler and a
/// second item in the channel, the third write waits for room until the test closes the channel or
/// cancels the write.
/// </para>
/// </remarks>
[Category("Execution")]
public class SerialExecutorEnqueueFailureTests {

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_StopClosesTheChannelWhileTheWriteWaits_CallerIsCanceledAndStateReturnedAsync(
      CancellationToken cancellationToken) {
    var seeded = new ExecutionState<ClosedByStop>();
    ExecutionStatePool<ClosedByStop>.Return(seeded);

    var executor = new SerialExecutor(channelCapacity: 1);
    await executor.StartAsync(cancellationToken);
    var (running, release, queued) = await _fillAsync(executor, cancellationToken);

    var waiting = executor.ExecuteAsync(
      _envelope(),
      (_, _) => ValueTask.FromResult(new ClosedByStop()),
      null!,   // execution strategies do not read the policy context
      cancellationToken);

    // Not awaited: its synchronous half closes the channel, which fails the waiting write.
    var stop = executor.StopAsync(CancellationToken.None);

    Exception? outcome = null;
    try {
      await waiting.AsTask().WaitAsync(cancellationToken);
    } catch (Exception ex) {
      outcome = ex;
    }
    release.SetResult(1);
    await running;
    await stop;
    await _settleAsync(queued);

    await Assert.That(cancellationToken.IsCancellationRequested).IsFalse()
      .Because("the caller must be finished by the executor, not by the test's bound");
    await Assert.That(outcome).IsTypeOf<OperationCanceledException>()
      .Because("work a stop kept from being queued is canceled, the same as work the stop found queued; "
        + "the channel's ChannelClosedException is an implementation detail the caller cannot act on");
    var rented = ExecutionStatePool<ClosedByStop>.Rent();
    await Assert.That(ReferenceEquals(rented, seeded)).IsTrue()
      .Because("the execution state rented for work that never reached the worker goes back to the pool");
    await Assert.That(rented.Handler is null).IsTrue()
      .Because("and it goes back reset, holding no handler, envelope or source");

    await executor.DisposeAsync();
  }

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_CallerCancelsWhileTheWriteWaits_CallerSeesItsCancellationAndStateReturnedAsync(
      CancellationToken cancellationToken) {
    var seeded = new ExecutionState<CanceledByCaller>();
    ExecutionStatePool<CanceledByCaller>.Return(seeded);

    var executor = new SerialExecutor(channelCapacity: 1);
    await executor.StartAsync(cancellationToken);
    var (running, release, queued) = await _fillAsync(executor, cancellationToken);

    using var callerCts = new CancellationTokenSource();
    var waiting = executor.ExecuteAsync(
      _envelope(),
      (_, _) => ValueTask.FromResult(new CanceledByCaller()),
      null!,
      callerCts.Token);
    await callerCts.CancelAsync();

    Exception? outcome = null;
    try {
      await waiting.AsTask().WaitAsync(cancellationToken);
    } catch (Exception ex) {
      outcome = ex;
    }
    release.SetResult(1);
    await running;
    await _settleAsync(queued);

    await Assert.That(cancellationToken.IsCancellationRequested).IsFalse()
      .Because("the caller must be finished by its own cancellation, not by the test's bound");
    await Assert.That(outcome).IsAssignableTo<OperationCanceledException>()
      .Because("a caller that cancels its own wait for room sees its cancellation");
    await Assert.That(((OperationCanceledException)outcome!).CancellationToken).IsEqualTo(callerCts.Token)
      .Because("it is the caller's own cancellation, passed through unchanged");
    var rented = ExecutionStatePool<CanceledByCaller>.Rent();
    await Assert.That(ReferenceEquals(rented, seeded)).IsTrue()
      .Because("the execution state rented for work that never reached the worker goes back to the pool");

    await executor.DisposeAsync();
  }

  /// <summary>
  /// Parks the worker in a handler and fills the one-slot channel behind it, so the next write
  /// waits for room.
  /// </summary>
  private static async Task<(Task<int> Running, TaskCompletionSource<int> Release, Task<int> Queued)> _fillAsync(
      SerialExecutor executor,
      CancellationToken cancellationToken) {
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var running = _runAsync(
      executor,
      async () => {
        entered.SetResult();
        return await release.Task;
      },
      cancellationToken);
    await entered.Task;
    var queued = _runAsync(executor, () => ValueTask.FromResult(2), cancellationToken);
    return (running, release, queued);
  }

  /// <summary>Awaits the filler item, which a stop may cancel; either outcome is fine here.</summary>
  private static async Task _settleAsync(Task<int> queued) {
    try {
      await queued;
    } catch (OperationCanceledException) {
      // Canceled by the stop in the first test; run in the second. Neither is under test here.
    }
  }

  private static async Task<int> _runAsync(
      SerialExecutor executor,
      Func<ValueTask<int>> handler,
      CancellationToken cancellationToken) =>
    await executor.ExecuteAsync(_envelope(), (_, _) => handler(), null!, cancellationToken);

  /// <summary>A locally dispatched envelope, which is all the executor reads of it.</summary>
  private static MessageEnvelope<EnqueueProbe> _envelope() =>
    new() {
      MessageId = MessageId.New(),
      Payload = new EnqueueProbe("enqueue"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  private sealed record EnqueueProbe(string Payload) : IMessage;

  /// <summary>Result type used only by the stop test, so its execution-state pool is its own.</summary>
  private sealed record ClosedByStop;

  /// <summary>Result type used only by the caller-cancel test, so its execution-state pool is its own.</summary>
  private sealed record CanceledByCaller;
}
