using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

/// <summary>
/// Tests for <see cref="BackgroundStageDispatch"/>.
/// </summary>
/// <remarks>
/// The single contract this helper must uphold is that scheduled work runs on a dedicated
/// (non-ThreadPool) thread. The rest of the pipeline relies on that — if the scheduler ever
/// slips back to <see cref="Task.Run(System.Action, System.Threading.CancellationToken)"/>,
/// CI starvation regressions like the one that produced the
/// <c>InboxStages_FireInCorrectOrder_AllStagesInvokedAsync</c> 120s-timeout failures return.
///
/// RED/GREEN discipline: reverting <see cref="BackgroundStageDispatch.StartLongRunning"/>
/// to <c>Task.Run(...)</c> makes <see cref="StartLongRunning_RunsBodyOnDedicatedThreadAsync"/>
/// fail (<c>IsThreadPoolThread</c> flips to <c>true</c>). That's the test's proof-of-purpose.
/// </remarks>
public class BackgroundStageDispatchTests {

  [Test]
  public async Task StartLongRunning_RunsBodyOnDedicatedThreadAsync() {
    // Arrange — the helper should schedule on a dedicated thread, not the shared ThreadPool
    var observed = new TaskCompletionSource<ThreadObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

    // Act — capture the thread the helper runs on BEFORE any await (the await can resume on
    // any thread — we only care about the thread the task started on, which is what the
    // caller pays for when scheduling background work)
    var task = BackgroundStageDispatch.StartLongRunning(() => {
      observed.TrySetResult(new ThreadObservation(
        IsThreadPoolThread: Thread.CurrentThread.IsThreadPoolThread,
        IsBackground: Thread.CurrentThread.IsBackground,
        ManagedThreadId: Environment.CurrentManagedThreadId));
      return Task.CompletedTask;
    }, cts.Token);

    await task;
    var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Assert — dedicated thread (not ThreadPool) is the whole point of this helper
    await Assert.That(result.IsThreadPoolThread).IsFalse()
      .Because("StartLongRunning must schedule on a dedicated thread, not the ThreadPool — otherwise it can be starved under load");

    // Sanity — LongRunning threads are still background threads (they die with the process)
    await Assert.That(result.IsBackground).IsTrue()
      .Because("Scheduled background work should not prevent process exit");
  }

  [Test]
  public async Task StartLongRunning_PropagatesAsyncCompletionAsync() {
    // Arrange — the async body's Task must unwrap so callers can await real completion,
    // not just the scheduling Task (which would complete before the async body runs)
    var bodyCompleted = false;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

    // Act
    var task = BackgroundStageDispatch.StartLongRunning(async () => {
      await Task.Yield();
      bodyCompleted = true;
    }, cts.Token);

    await task;

    // Assert — awaiting the returned task observes the async body's completion
    await Assert.That(bodyCompleted).IsTrue()
      .Because("The returned Task must complete only when the async body completes (Unwrap contract)");
  }

  [Test]
  public async Task StartLongRunning_PropagatesBodyExceptionAsync() {
    // Arrange — body exceptions must surface when the returned task is awaited,
    // not silently disappear via Task.Unwrap ignoring the inner exception
    var expectedEx = new InvalidOperationException("intentional");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

    // Act
    var task = BackgroundStageDispatch.StartLongRunning(() => Task.FromException(expectedEx), cts.Token);

    // Assert
    var caught = await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).IsEqualTo("intentional");
  }

  [Test]
  public async Task StartLongRunning_NullBody_ThrowsArgumentNullExceptionAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(() => {
      _ = BackgroundStageDispatch.StartLongRunning(null!, CancellationToken.None);
      return Task.CompletedTask;
    });
  }

  [Test]
  public async Task StartLongRunning_ContinuationsStayOnDedicatedThread_AcrossAwaitsAsync() {
    // The pump contract — and the fix for the InboxStages 120s CI timeouts: LongRunning alone only
    // pins the SYNCHRONOUS PREFIX of the body; the first await's continuation returns to the shared
    // ThreadPool, where it can starve under load. The single-threaded SynchronizationContext pump
    // keeps EVERY continuation on the dedicated thread, making detached-stage progress independent
    // of ThreadPool availability. Reverting to the prefix-only Task.Factory.StartNew(LongRunning)
    // implementation makes this test fail (post-await thread flips to a pool thread).
    var threadIds = new List<int>();
    var poolFlags = new List<bool>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

    await BackgroundStageDispatch.StartLongRunning(async () => {
      threadIds.Add(Environment.CurrentManagedThreadId);
      poolFlags.Add(Thread.CurrentThread.IsThreadPoolThread);
      await Task.Yield();
      threadIds.Add(Environment.CurrentManagedThreadId);
      poolFlags.Add(Thread.CurrentThread.IsThreadPoolThread);
      await Task.Delay(10, cts.Token);
      threadIds.Add(Environment.CurrentManagedThreadId);
      poolFlags.Add(Thread.CurrentThread.IsThreadPoolThread);
    }, cts.Token);

    await Assert.That(threadIds.Distinct().Count()).IsEqualTo(1)
      .Because("every continuation of the detached-stage body must resume on the SAME dedicated thread — pool-immune progress is the whole point of the pump");
    await Assert.That(poolFlags.Any(f => f)).IsFalse()
      .Because("no segment of the body may run on a ThreadPool thread, or starvation regressions (InboxStages 120s timeouts) return");
  }

  [Test]
  public async Task StartLongRunning_PreCancelledToken_ReturnsCanceledTaskAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    var task = BackgroundStageDispatch.StartLongRunning(() => Task.CompletedTask, cts.Token);

    await Assert.ThrowsAsync<TaskCanceledException>(async () => await task);
  }

  private readonly record struct ThreadObservation(bool IsThreadPoolThread, bool IsBackground, int ManagedThreadId);
}
