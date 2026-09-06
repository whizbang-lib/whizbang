using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for <see cref="BackgroundStageDispatch"/> paths the primary suite
/// (<see cref="BackgroundStageDispatchTests"/>) doesn't reach: the dedicated thread's own
/// cancellation catch, the single-threaded pump's straggler-continuation fallback once its queue
/// has already been marked complete, and the pump's unsupported synchronous Send.
/// </summary>
public class BackgroundStageDispatchCoverageTests {

  /// <summary>
  /// If this regressed to synthesizing a fresh token (or losing the body's own), a caller
  /// inspecting why a detached stage was canceled would see the wrong reason, or none at all.
  /// </summary>
  [Test]
  public async Task StartLongRunning_BodyReturnsATaskCanceledWithItsOwnToken_ForwardsThatExactTokenAsync() {
    using var bodyCts = new CancellationTokenSource();
    await bodyCts.CancelAsync();
    var bodyToken = bodyCts.Token;

    var task = BackgroundStageDispatch.StartLongRunning(() => Task.FromCanceled(bodyToken), CancellationToken.None);

    var caught = await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
    await Assert.That(caught!.CancellationToken).IsEqualTo(bodyToken)
      .Because("the dedicated thread's cancellation catch must forward the body's OWN token so a "
             + "caller inspecting a canceled detached stage sees the real reason, not a synthesized "
             + "or absent one");
  }

  /// <summary>
  /// If this regressed to dropping the straggler instead of falling back to the ThreadPool, a
  /// late fire-and-forget continuation posted just after the pump wound down would silently never
  /// run — losing whatever cleanup or completion signal it carried.
  /// </summary>
  [Test]
  public async Task Post_AfterThePumpHasAlreadyCompleted_StillRunsTheContinuationViaThreadPoolAsync() {
    SynchronizationContext? capturedContext = null;

    var task = BackgroundStageDispatch.StartLongRunning(() => {
      capturedContext = SynchronizationContext.Current;
      return Task.CompletedTask;
    }, CancellationToken.None);

    // By the time this completes, the pump's queue is guaranteed already marked complete: the
    // dedicated thread only finishes (and lets `completion.TrySetResult()` run) after
    // RunOnCurrentThread's consuming enumerable has drained, which itself requires Complete() to
    // have already been called. So the Post below is guaranteed to race the queue AFTER
    // completion, never before.
    await task;

    var stragglerRan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    capturedContext!.Post(_ => {
      stragglerRan.TrySetResult(Thread.CurrentThread.IsThreadPoolThread);
    }, null);

    var ranOnThreadPool = await stragglerRan.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(ranOnThreadPool).IsTrue()
      .Because("a continuation posted after the pump's queue has completed must still run, via the "
             + "ThreadPool fallback, rather than being silently dropped");
  }

  /// <summary>
  /// If this regressed to actually running the callback (or to a silent no-op) instead of
  /// throwing, a caller mistakenly relying on synchronous continuation on the detached-stage pump
  /// would deadlock the dedicated thread against itself instead of failing fast.
  /// </summary>
  [Test]
  public async Task Send_OnTheDetachedStagePump_ThrowsNotSupportedAsync() {
    SynchronizationContext? capturedContext = null;

    var task = BackgroundStageDispatch.StartLongRunning(() => {
      capturedContext = SynchronizationContext.Current;
      return Task.CompletedTask;
    }, CancellationToken.None);
    await task;

    var caught = await Assert.ThrowsAsync<NotSupportedException>(() => {
      capturedContext!.Send(_ => { }, null);
      return Task.CompletedTask;
    });

    await Assert.That(caught!.Message).Contains("Send")
      .Because("the exception must identify what's unsupported (synchronous Send) so a caller "
             + "hitting this in production knows what to fix, not just that something threw");
  }
}
