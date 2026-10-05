// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Async;

namespace Whizbang.Core.Tests.Async;

/// <summary>
/// Locks the contract that makes <see cref="WakeSignal"/> safe for poll loops: at most one waiter
/// ever exists, a signal is never swallowed by a stale or abandoned waiter, and a raised signal
/// stays raised (coalesced into one) until the loop consumes it.
/// </summary>
[Category("Unit")]
public class WakeSignalTests {

  [Test]
  public async Task WaitAsync_RepeatedWhilePending_ReturnsTheSameTaskAndKeepsOneWaiterAsync() {
    var signal = new WakeSignal();

    var first = signal.WaitAsync(CancellationToken.None);
    var second = signal.WaitAsync(CancellationToken.None);
    var third = signal.WaitAsync(CancellationToken.None);

    await Assert.That(ReferenceEquals(first, second)).IsTrue()
      .Because("a loop that abandons its wait for another wake source must get the same waiter back, not a second one");
    await Assert.That(ReferenceEquals(second, third)).IsTrue();
    await Assert.That(signal.PendingWaiters).IsEqualTo(1)
      .Because("three iterations must never queue three waiters; that is the leak this type exists to prevent");
    await Assert.That(first.IsCompleted).IsFalse();
  }

  [Test]
  public async Task Set_WithPendingWaiter_CompletesItAndClearsTheSlotAsync() {
    var signal = new WakeSignal();
    var wait = signal.WaitAsync(CancellationToken.None);

    signal.Set();

    await wait;
    await Assert.That(signal.PendingWaiters).IsEqualTo(0);
    await Assert.That(signal.HasPendingSignal).IsTrue()
      .Because("the signal is a level: it stays raised until the loop consumes it");
  }

  [Test]
  public async Task Set_WithoutWaiter_IsKeptOnceForTheNextWaitAsync() {
    var signal = new WakeSignal();

    signal.Set();
    signal.Set();
    signal.Set();

    await Assert.That(signal.HasPendingSignal).IsTrue();
    var first = signal.WaitAsync(CancellationToken.None);
    await Assert.That(first.IsCompletedSuccessfully).IsTrue()
      .Because("a signal raised while the loop was busy must wake the very next iteration");
    signal.Consume();
    var second = signal.WaitAsync(CancellationToken.None);
    await Assert.That(second.IsCompleted).IsFalse()
      .Because("three signals coalesce into one wake; the second wait parks again");
    await Assert.That(signal.HasPendingSignal).IsFalse();
  }

  [Test]
  public async Task Set_AfterManyAbandonedIterations_WakesTheLiveWaiterNotAStaleOneAsync() {
    // The SemaphoreSlim idiom failed exactly here: N abandoned WaitAsync calls queued N waiters and
    // Release completed the oldest, which nothing awaited. Here every iteration shares one waiter.
    var signal = new WakeSignal();
    Task live = Task.CompletedTask;
    for (var iteration = 0; iteration < 50; iteration++) {
      live = signal.WaitAsync(CancellationToken.None);
    }

    signal.Set();

    await live;
    await Assert.That(live.IsCompletedSuccessfully).IsTrue()
      .Because("the one signal must reach the waiter the loop is actually awaiting");
    await Assert.That(signal.PendingWaiters).IsEqualTo(0);
  }

  [Test]
  public async Task WaitAsync_Canceled_CancelsTheWaiterAndClearsTheSlotAsync() {
    var signal = new WakeSignal();
    using var cts = new CancellationTokenSource();
    var wait = signal.WaitAsync(cts.Token);

    await cts.CancelAsync();

    await Assert.That(async () => await wait).Throws<OperationCanceledException>();
    await Assert.That(signal.PendingWaiters).IsEqualTo(0)
      .Because("a canceled waiter must not linger; the next wait after a restart-style cancel starts clean");
  }

  [Test]
  public async Task WaitAsync_AlreadyCanceledToken_ReturnsCanceledWithoutTouchingStateAsync() {
    var signal = new WakeSignal();
    signal.Set();
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    var wait = signal.WaitAsync(cts.Token);

    await Assert.That(wait.IsCanceled).IsTrue();
    await Assert.That(signal.HasPendingSignal).IsTrue()
      .Because("a canceled caller did not consume the pending signal; the next live caller gets it");
    await Assert.That(signal.PendingWaiters).IsEqualTo(0);
  }

  [Test]
  public async Task Set_AfterCancellation_BecomesAPendingSignalForTheNextWaitAsync() {
    var signal = new WakeSignal();
    using var cts = new CancellationTokenSource();
    var canceledWait = signal.WaitAsync(cts.Token);
    await cts.CancelAsync();
    await Assert.That(async () => await canceledWait).Throws<OperationCanceledException>();

    signal.Set();

    var next = signal.WaitAsync(CancellationToken.None);
    await Assert.That(next.IsCompletedSuccessfully).IsTrue()
      .Because("a signal after cancellation must not be lost; it satisfies the next wait");
  }

  [Test]
  public async Task Set_DeliveredWaiterIsNotCanceledByALaterTokenCancellationAsync() {
    // The registration is released when the signal delivers, so canceling the token afterwards
    // must not turn a completed wake into a canceled one or disturb the slot.
    var signal = new WakeSignal();
    using var cts = new CancellationTokenSource();
    var wait = signal.WaitAsync(cts.Token);
    signal.Set();
    await wait;

    await cts.CancelAsync();

    await Assert.That(wait.IsCompletedSuccessfully).IsTrue();
    await Assert.That(signal.PendingWaiters).IsEqualTo(0);
    await Assert.That(signal.HasPendingSignal).IsTrue()
      .Because("canceling the token of a delivered wait must not lower the signal the loop has not consumed");
  }

  [Test]
  public async Task Set_OnAWaiterTheLoopAbandoned_WakesTheNextWaitAsync() {
    // A loop iteration that ended on another wake source left its wait parked and moved on. A
    // signal landing while the loop is busy completes that wait; the next iteration must still see
    // it rather than park a fresh waiter and sleep through the signal.
    var signal = new WakeSignal();
    _ = signal.WaitAsync(CancellationToken.None);   // abandoned: the iteration woke on work instead

    signal.Set();

    var next = signal.WaitAsync(CancellationToken.None);
    await Assert.That(next.IsCompletedSuccessfully).IsTrue()
      .Because("a signal raised after the last wait began must never be lost");
    await Assert.That(signal.PendingWaiters).IsEqualTo(0);
  }

  [Test]
  public async Task Consume_AfterTheWake_ParksTheNextWaitAgainAsync() {
    var signal = new WakeSignal();
    var wait = signal.WaitAsync(CancellationToken.None);
    signal.Set();
    await wait;

    signal.Consume();

    await Assert.That(signal.HasPendingSignal).IsFalse()
      .Because("the loop acted on the signal, so nothing is left pending");
    var next = signal.WaitAsync(CancellationToken.None);
    await Assert.That(next.IsCompleted).IsFalse()
      .Because("one signal is one wake; after it is consumed the next wait parks");
    await Assert.That(signal.PendingWaiters).IsEqualTo(1);
  }

  [Test]
  public async Task Consume_WithAParkedWaiter_LeavesItParkedAsync() {
    var signal = new WakeSignal();
    var wait = signal.WaitAsync(CancellationToken.None);

    signal.Consume();

    await Assert.That(signal.PendingWaiters).IsEqualTo(1)
      .Because("consuming with no signal delivered must not drop the one parked waiter");
    await Assert.That(ReferenceEquals(signal.WaitAsync(CancellationToken.None), wait)).IsTrue();
  }
}
