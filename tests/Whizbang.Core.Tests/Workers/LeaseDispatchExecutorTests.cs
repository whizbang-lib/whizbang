using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Phase H step 9 slice 2 — RED-first locks for <see cref="LeaseDispatchExecutor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Without active abandonment, the lease cancellation token can't actually unstick a hung
/// dispatch — we'd still be <c>await</c>ing the inner Task that's not honoring its CT. The
/// executor races the dispatch against the lease token via <c>Task.WhenAny</c>; when the
/// cancellation wins, it abandons the dispatch (fire-and-forget continuation that observes
/// any later exception) and throws <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// <strong>Locked invariants:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>Receptor honors CT → executor surfaces the receptor's OCE cleanly; no abandonment needed.</description></item>
/// <item><description>Receptor ignores CT after deadline → executor throws OCE within bounded time even though the inner Task is still running.</description></item>
/// <item><description>Receptor completes before deadline → executor returns normally; no OCE.</description></item>
/// <item><description>Abandoned task exceptions are observed (no <c>UnobservedTaskException</c> escapes) — the abandoned task's eventual exception is swallowed by an internal continuation.</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/work-coordinator/lease-cancellation</docs>
public class LeaseDispatchExecutorTests {

  private static FakeTimeProvider _provider() =>
    new(new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero));

  private static LeaseHandle _newLease(FakeTimeProvider time, TimeSpan? leaseFromNow = null) =>
    new(
      workId: (Guid)TrackedGuid.NewMedo(),
      category: WorkCategory.Inbox,
      deadline: time.GetUtcNow() + (leaseFromNow ?? TimeSpan.FromSeconds(60)),
      maxRenewals: 6,
      timeProvider: time,
      linkedTokens: []);

  [Test]
  public async Task Dispatch_CompletesBeforeDeadline_ReturnsNormallyAsync() {
    var time = _provider();
    using var lease = _newLease(time);

    await LeaseDispatchExecutor.RunWithLeaseAsync(lease, _ => Task.CompletedTask);

    await Assert.That(lease.Token.IsCancellationRequested).IsFalse()
      .Because("lease shouldn't have cancelled — dispatch completed cleanly");
  }

  [Test]
  public async Task Dispatch_ReturnsValueViaTcs_CompletesNormallyAsync() {
    var time = _provider();
    using var lease = _newLease(time);
    var dispatchTcs = new TaskCompletionSource();

    var helper = LeaseDispatchExecutor.RunWithLeaseAsync(lease, _ => dispatchTcs.Task);
    dispatchTcs.SetResult();
    await helper;

    // No assertion on cancellation — just verify it didn't throw.
    await Assert.That(helper.IsCompletedSuccessfully).IsTrue();
  }

  [Test]
  public async Task ReceptorHonorsCT_ThrowsOnCancellation_ExecutorSurfacesOceCleanlyAsync() {
    var time = _provider();
    using var lease = _newLease(time);

    // Receptor that honors CT: awaits the ct, throws OCE when cancelled.
    var helper = LeaseDispatchExecutor.RunWithLeaseAsync(lease, async ct => {
      var honorTcs = new TaskCompletionSource();
      using var reg = ct.Register(() => honorTcs.TrySetResult());
      await honorTcs.Task.ConfigureAwait(false);
      ct.ThrowIfCancellationRequested();
    });

    time.Advance(TimeSpan.FromSeconds(61));

    await Assert.That(async () => await helper).Throws<OperationCanceledException>();
  }

  [Test]
  public async Task ReceptorIgnoresCT_AfterDeadline_ExecutorAbandonsAndThrowsOceAsync() {
    var time = _provider();
    using var lease = _newLease(time);
    var dispatchTcs = new TaskCompletionSource();

    // Receptor that IGNORES CT — awaits a TCS that never completes via cancellation.
    var helper = LeaseDispatchExecutor.RunWithLeaseAsync(lease, _ => dispatchTcs.Task);

    time.Advance(TimeSpan.FromSeconds(61));

    await Assert.That(async () => await helper).Throws<OperationCanceledException>();
    await Assert.That(dispatchTcs.Task.IsCompleted).IsFalse()
      .Because("the dispatch task is abandoned, not awaited — it's still running in the background");
  }

  [Test]
  public async Task AbandonedTask_LaterThrows_NoUnobservedTaskExceptionAsync() {
    // Critical regression lock: abandoned task that later faults must NOT raise
    // UnobservedTaskException. The executor attaches a continuation that observes .Exception.
    var time = _provider();
    using var lease = _newLease(time);
    var dispatchTcs = new TaskCompletionSource();
    var unobserved = new List<UnobservedTaskExceptionEventArgs>();
    void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) => unobserved.Add(e);
    TaskScheduler.UnobservedTaskException += OnUnobserved;
    try {
      var helper = LeaseDispatchExecutor.RunWithLeaseAsync(lease, _ => dispatchTcs.Task);
      time.Advance(TimeSpan.FromSeconds(61));
      try { await helper; } catch (OperationCanceledException) { /* expected */ }

      // Now the abandoned task faults. Without our continuation, this would raise UTE.
      dispatchTcs.SetException(new InvalidOperationException("post-abandon"));

      // Force the abandoned-task to be GC'd so finalizer runs (where UTE would fire).
      var weak = new WeakReference(dispatchTcs);
      dispatchTcs = null!;
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
    } finally {
      TaskScheduler.UnobservedTaskException -= OnUnobserved;
    }

    await Assert.That(unobserved).IsEmpty()
      .Because("the executor's abandonment continuation must observe the abandoned task's exception");
  }

  [Test]
  public async Task SuccessfulDispatch_DoesNotThrowFirstChanceOceOnLeaseDisposeAsync() {
    // Phase H step 9 regression lock (2026-05-03): the original implementation used
    // Task.Delay(Timeout.InfiniteTimeSpan, ct) as the cancellation race signal. When the lease
    // disposed (which cancels the CT), Task.Delay's internal cancellation registration fired
    // and threw TaskCanceledException — caught downstream but FIRST-CHANCE thrown per dispatch.
    // a consumer BFF observed thousands of first-chance OCEs per second under load. The fix: use a
    // TaskCompletionSource + ct.UnsafeRegister, and dispose the registration BEFORE the lease
    // disposes (via `await using` inside the executor). Registration disposal stops the
    // callback-fire on subsequent CT cancellation.
    var fcOces = new System.Collections.Concurrent.ConcurrentBag<Exception>();
    void handler(object? _, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e) {
      // The bug surfaces as TaskCanceledException thrown from Task.Delay's internal
      // cancellation handling. That throw originates in BCL code (Task.Delay / DelayPromise),
      // not in user code, so we can't filter by the executor's frame. Count all
      // TaskCanceledException first-chances during the test — there's no other legitimate
      // source in this isolated dispatch loop.
      if (e.Exception is TaskCanceledException) {
        fcOces.Add(e.Exception);
      }
    }
    AppDomain.CurrentDomain.FirstChanceException += handler;
    try {
      // Run 50 successful dispatches in sequence. Each disposes its lease at end-of-scope.
      // Old impl: 50 OCEs (one Task.Delay throw per dispatch). New impl: 0 OCEs.
      for (var i = 0; i < 50; i++) {
        var time = _provider();
        using var lease = _newLease(time);
        await LeaseDispatchExecutor.RunWithLeaseAsync(lease, _ => Task.CompletedTask);
      }
    } finally {
      AppDomain.CurrentDomain.FirstChanceException -= handler;
    }

    await Assert.That(fcOces).IsEmpty()
      .Because("successful dispatches must not throw first-chance OperationCanceledException — see commit message for the original Task.Delay-based pattern that did");
  }

  [Test]
  public async Task NullArgs_ThrowAsync() {
    var time = _provider();
    using var lease = _newLease(time);

    Task nullLease() => LeaseDispatchExecutor.RunWithLeaseAsync(null!, _ => Task.CompletedTask);
    Task nullDispatch() => LeaseDispatchExecutor.RunWithLeaseAsync(lease, null!);

    await Assert.That(nullLease).Throws<ArgumentNullException>();
    await Assert.That(nullDispatch).Throws<ArgumentNullException>();
  }
}
