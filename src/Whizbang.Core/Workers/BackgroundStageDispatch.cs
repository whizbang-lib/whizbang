using System.Collections.Concurrent;

namespace Whizbang.Core.Workers;

/// <summary>
/// Scheduler for fire-and-forget background stage dispatch (PostLifecycle in
/// <see cref="PerspectiveWorker"/>, inbox Pre/PostDetached in <see cref="WorkCoordinatorPublisherWorker"/>).
/// </summary>
/// <remarks>
/// <para>
/// Runs the async body to COMPLETION on a dedicated OS thread by installing a single-threaded
/// <see cref="SynchronizationContext"/> pump (the classic AsyncPump pattern): every continuation of the
/// body's awaits posts back to the dedicated thread instead of the shared ThreadPool.
/// </para>
/// <para>
/// <strong>Why not just <c>TaskCreationOptions.LongRunning</c>:</strong> that only pins the SYNCHRONOUS
/// PREFIX of the body — the first <c>await</c> returns its continuation to the ThreadPool, so under pool
/// saturation (perspective drain cycles, EF async continuations, transport callbacks) the detached stage
/// still starves. Observed symptom of that earlier approach: CI-bound integration tests saw
/// Inbox*Detached stages sit queued past a 120s deadline
/// (<c>InboxStages_FireInCorrectOrder_AllStagesInvokedAsync</c>) while the synchronous prefix had long
/// since run. The pump closes that gap: progress of the detached stage no longer depends on ThreadPool
/// availability (interior awaits that use <c>ConfigureAwait(false)</c> may still hop through the pool,
/// but the top-level stage body and receptor invocation chain stay on the dedicated thread).
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Workers/BackgroundStageDispatchTests.cs</tests>
public static class BackgroundStageDispatch {
  /// <summary>
  /// Schedules <paramref name="body"/> on a dedicated OS thread and pumps its continuations on that same
  /// thread until the body completes. The returned task completes (or faults/cancels) with the body.
  /// </summary>
  public static Task StartLongRunning(Func<Task> body, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(body);
    if (cancellationToken.IsCancellationRequested) {
      return Task.FromCanceled(cancellationToken);
    }

    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() => {
      try {
        _runOnCurrentThread(body);
        completion.TrySetResult();
      } catch (OperationCanceledException oce) {
        completion.TrySetCanceled(oce.CancellationToken);
      } catch (Exception ex) {
        completion.TrySetException(ex);
      }
    }) {
      IsBackground = true,
      Name = "whizbang-detached-stage",
    };
    thread.Start();
    return completion.Task;
  }

  /// <summary>
  /// AsyncPump core: installs a single-threaded SynchronizationContext, starts the body, and pumps posted
  /// continuations on the current thread until the body's task completes; then rethrows its outcome.
  /// </summary>
  private static void _runOnCurrentThread(Func<Task> body) {
    var previous = SynchronizationContext.Current;
    var context = new SingleThreadSynchronizationContext();
    try {
      SynchronizationContext.SetSynchronizationContext(context);
      var task = body();
      task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
      context.RunOnCurrentThread();
      task.GetAwaiter().GetResult();
    } finally {
      SynchronizationContext.SetSynchronizationContext(previous);
    }
  }

  /// <summary>
  /// Minimal single-threaded SynchronizationContext: Post enqueues; the owning thread drains until
  /// <see cref="Complete"/>. Posts arriving after completion fall back to the ThreadPool rather than
  /// being dropped (a late fire-and-forget continuation must still run).
  /// </summary>
  private sealed class SingleThreadSynchronizationContext : SynchronizationContext {
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

    public override void Post(SendOrPostCallback d, object? state) {
      ArgumentNullException.ThrowIfNull(d);
      try {
        _queue.Add((d, state));
      } catch (InvalidOperationException) {
        // Queue already completed — a straggler continuation posted after the body finished.
        ThreadPool.QueueUserWorkItem(static s => {
          var (callback, callbackState) = ((SendOrPostCallback, object?))s!;
          callback(callbackState);
        }, (d, state));
      }
    }

    public override void Send(SendOrPostCallback d, object? state) =>
      throw new NotSupportedException("Synchronous Send is not supported on the detached-stage pump.");

    public void RunOnCurrentThread() {
      foreach (var (callback, state) in _queue.GetConsumingEnumerable()) {
        callback(state);
      }
    }

    public void Complete() => _queue.CompleteAdding();
  }
}
