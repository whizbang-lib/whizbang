namespace Whizbang.Core.Workers;

/// <summary>
/// Runs an inner dispatch under a <see cref="LeaseHandle"/>, racing it against the lease's
/// cancellation token so a misbehaving receptor that ignores its CT can't hold the dispatch
/// pump forever. Phase H step 9 slice 2.
/// </summary>
/// <remarks>
/// <para>
/// Without active abandonment, passing the lease token down to the inner dispatch isn't enough:
/// if the receptor (or any operation it calls) doesn't observe the token and just blocks on a
/// sync I/O / external call / unbounded TCS, the framework's <c>await</c> on the inner Task
/// stays parked forever. The executor solves this with <c>Task.WhenAny</c>: when the lease
/// token cancels first, it abandons the inner Task (attaches a fire-and-forget continuation
/// that observes any later exception so <c>UnobservedTaskException</c> doesn't escape) and
/// throws <see cref="OperationCanceledException"/> so the worker's failure path runs.
/// </para>
/// <para>
/// The abandoned task continues running in the background until it eventually completes or
/// the process restarts. That's an accepted resource cost for tolerating misbehaving receptors
/// — bounded by the abandoned-task count, in practice small. Receptors that honor CT properly
/// (idiomatic .NET) hit the clean path with no abandonment.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/lease-cancellation</docs>
public static class LeaseDispatchExecutor {
  /// <summary>
  /// Runs <paramref name="dispatch"/> with the lease's CT. Returns when the dispatch completes
  /// (success or thrown exception) or throws <see cref="OperationCanceledException"/> when the
  /// lease token cancels first — abandoning the still-running dispatch in the latter case.
  /// </summary>
  public static async Task RunWithLeaseAsync(
      LeaseHandle lease,
      Func<CancellationToken, Task> dispatch) {
    ArgumentNullException.ThrowIfNull(lease);
    ArgumentNullException.ThrowIfNull(dispatch);

    var ct = lease.Token;
    var dispatchTask = dispatch(ct);

    // Race signal: completes (with OCE) when the lease token cancels. We swallow the OCE in a
    // continuation so this signal-task itself doesn't faulted-bubble before WhenAny observes it.
    var cancellationSignal = Task.Delay(Timeout.InfiniteTimeSpan, ct)
      .ContinueWith(static _ => { }, TaskScheduler.Default);

    var winner = await Task.WhenAny(dispatchTask, cancellationSignal).ConfigureAwait(false);

    if (winner == dispatchTask) {
      // Dispatch finished first (success / exception / honored-CT-and-threw-OCE).
      // Surface its result/exception to the caller.
      await dispatchTask.ConfigureAwait(false);
      return;
    }

    // Cancellation won the race. The dispatch task is still running (or maybe just completed
    // moments after WhenAny picked the cancellation signal — but we treat it as abandoned for
    // determinism). Attach an observe-only continuation so a future exception on this task
    // doesn't escape as UnobservedTaskException.
    _ = dispatchTask.ContinueWith(
      static t => { _ = t.Exception; },  // accessing .Exception marks it observed
      CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default);

    ct.ThrowIfCancellationRequested();
  }
}
