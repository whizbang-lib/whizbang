// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers <see cref="WhizbangRunPermit"/>, the instance's one run permit (#2045): what every claiming worker and the
/// transport consumer obeys. Running is open; a closed permit holds every new hold until it reopens; a stopped permit
/// never reopens. A hold counts work in hand, so the run-control path can wait for it to be finished. Completion-signal
/// based: no timers, no delays.
/// </summary>
/// <code-under-test>src/Whizbang.Core/RunControl/WhizbangRunPermit.cs</code-under-test>
[Category("Component")]
[Category("RunControl")]
public class WhizbangRunPermitTests {

  [Test]
  public async Task Running_ReturnsImmediatelyAsync() {
    var permit = new WhizbangRunPermit();
    await permit.WaitAsync(CancellationToken.None);
    await Assert.That(permit.IsRunning).IsTrue();
    await Assert.That(permit.State).IsEqualTo(RunState.Running);
  }

  [Test]
  public async Task Paused_BlocksUntilResumedAsync() {
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);

    var waiter = permit.WaitAsync(CancellationToken.None);
    await Assert.That(waiter.IsCompleted).IsFalse();

    await permit.ChangeAsync(RunState.Running, CancellationToken.None);
    await waiter;
    await Assert.That(waiter.IsCompletedSuccessfully).IsTrue();
  }

  [Test]
  public async Task Stopped_CancelsAwaitersAsync() {
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    var waiting = permit.WaitAsync(CancellationToken.None);
    await permit.ChangeAsync(RunState.Stopped, CancellationToken.None);

    await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
    await Assert.That(async () => await permit.WaitAsync(CancellationToken.None)).Throws<OperationCanceledException>();
  }

  [Test]
  public async Task Stopped_NeverReopensAsync() {
    // Design choice (#2045): a stop is final. Pause and stand-down reopen; a stopped instance does not take work again.
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Stopped, CancellationToken.None);

    await permit.ChangeAsync(RunState.Running, CancellationToken.None);
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);

    await Assert.That(permit.State).IsEqualTo(RunState.Stopped);
    await Assert.That(permit.IsRunning).IsFalse();
  }

  [Test]
  public async Task Enter_WhileRunning_CountsTheHoldUntilItIsDisposedAsync() {
    var permit = new WhizbangRunPermit();

    var hold = await permit.EnterAsync(CancellationToken.None);
    await Assert.That(permit.Held).IsEqualTo(1);

    hold.Dispose();
    hold.Dispose();   // idempotent: a hold is released once
    await Assert.That(permit.Held).IsEqualTo(0);
  }

  [Test]
  public async Task Enter_WhilePaused_WaitsForTheReopeningAsync() {
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);

    var entering = permit.EnterAsync(CancellationToken.None).AsTask();
    await Assert.That(entering.IsCompleted).IsFalse().Because("no new work is taken while the permit is closed");
    await Assert.That(permit.Held).IsEqualTo(0);

    await permit.ChangeAsync(RunState.Running, CancellationToken.None);
    using var hold = await entering;
    await Assert.That(permit.Held).IsEqualTo(1);
  }

  [Test]
  public async Task Enter_WhenStopped_IsCanceledAsync() {
    var permit = new WhizbangRunPermit();
    var entering = permit.EnterAsync(CancellationToken.None).AsTask();
    await Assert.That(entering.IsCompleted).IsTrue();
    (await entering).Dispose();

    await permit.ChangeAsync(RunState.Stopped, CancellationToken.None);

    await Assert.That(async () => await permit.EnterAsync(CancellationToken.None)).Throws<OperationCanceledException>();
  }

  [Test]
  public async Task Enter_CanceledWhileWaiting_TakesNoHoldAsync() {
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    using var cts = new CancellationTokenSource();

    var entering = permit.EnterAsync(cts.Token).AsTask();
    await cts.CancelAsync();

    await Assert.That(async () => await entering).Throws<OperationCanceledException>();
    await Assert.That(permit.Held).IsEqualTo(0);
  }

  [Test]
  public async Task WhenNothingHeld_WithNoHold_IsAlreadyCompleteAsync() {
    var permit = new WhizbangRunPermit();
    var idle = permit.WhenNothingHeldAsync(CancellationToken.None);
    await Assert.That(idle.IsCompletedSuccessfully).IsTrue();
  }

  [Test]
  public async Task WhenNothingHeld_CompletesWhenTheLastHoldIsReleasedAsync() {
    var permit = new WhizbangRunPermit();
    var first = await permit.EnterAsync(CancellationToken.None);
    var second = await permit.EnterAsync(CancellationToken.None);
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);

    var idle = permit.WhenNothingHeldAsync(CancellationToken.None);
    first.Dispose();
    await Assert.That(idle.IsCompleted).IsFalse().Because("one hold is still in hand");

    second.Dispose();
    await idle;
    await Assert.That(idle.IsCompletedSuccessfully).IsTrue();

    // A later hold starts a new wait: the completed one is not reused.
    await permit.ChangeAsync(RunState.Running, CancellationToken.None);
    var third = await permit.EnterAsync(CancellationToken.None);
    var next = permit.WhenNothingHeldAsync(CancellationToken.None);
    await Assert.That(next.IsCompleted).IsFalse();
    third.Dispose();
    await next;
  }

  [Test]
  public async Task WhenNothingHeld_Canceled_EndsTheWaitAsync() {
    var permit = new WhizbangRunPermit();
    using var hold = await permit.EnterAsync(CancellationToken.None);
    using var cts = new CancellationTokenSource();

    var idle = permit.WhenNothingHeldAsync(cts.Token);
    await cts.CancelAsync();

    await Assert.That(async () => await idle).Throws<OperationCanceledException>();
  }

  [Test]
  public async Task Change_TellsEveryObeyerAndAwaitsItAsync() {
    var permit = new WhizbangRunPermit();
    var seen = new List<RunState>();
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var subscription = permit.Obey(async (state, _) => {
      seen.Add(state);
      await release.Task;
    });

    var closing = permit.ChangeAsync(RunState.Paused, CancellationToken.None).AsTask();
    await Assert.That(closing.IsCompleted).IsFalse().Because("the change is done only when every obeyer has acted on it");
    await Assert.That(permit.IsRunning).IsFalse().Because("the permit is closed before any obeyer is told");

    release.SetResult();
    await closing;
    await Assert.That(seen).IsEquivalentTo([RunState.Paused]);
  }

  [Test]
  public async Task Change_ToTheSameState_TellsNoObeyerAsync() {
    var permit = new WhizbangRunPermit();
    var calls = 0;
    using var subscription = permit.Obey((_, _) => {
      calls++;
      return Task.CompletedTask;
    });

    await permit.ChangeAsync(RunState.Running, CancellationToken.None);
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    await permit.ChangeAsync(RunState.Stopped, CancellationToken.None);
    await permit.ChangeAsync(RunState.Running, CancellationToken.None);

    await Assert.That(calls).IsEqualTo(2).Because("only Paused and Stopped changed the state");
  }

  [Test]
  public async Task Obey_Disposed_StopsTheNotificationsAsync() {
    var permit = new WhizbangRunPermit();
    var calls = 0;
    var subscription = permit.Obey((_, _) => {
      calls++;
      return Task.CompletedTask;
    });
    subscription.Dispose();
    subscription.Dispose();

    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);

    await Assert.That(calls).IsEqualTo(0);
  }

  [Test]
  public async Task Obey_Null_ThrowsAsync() {
    var permit = new WhizbangRunPermit();
    await Assert.That(() => permit.Obey(null!)).Throws<ArgumentNullException>();
  }
}
