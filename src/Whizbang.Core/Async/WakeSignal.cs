namespace Whizbang.Core.Async;

/// <summary>
/// A coalescing, single-consumer wake signal that never leaves an abandoned waiter behind.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the <c>SemaphoreSlim(0, 1)</c> wake idiom in the poll loops. That idiom created a fresh
/// <c>WaitAsync</c> on every iteration and raced it against a delay with <c>Task.WhenAny</c>. Every
/// iteration the delay (or a work channel) won left one more queued waiter that nothing awaited, and
/// the next <c>Release</c> completed the oldest of those stale waiters instead of the live one, so a
/// real signal was swallowed. A heap dump of one long-running instance held 108,992 such waiters.
/// </para>
/// <para>
/// Here at most one waiter exists. Repeated <see cref="WaitAsync"/> calls while a wait is pending
/// return the same task, so a loop that abandons the task for another wake source simply asks again
/// on the next iteration without queuing a second waiter. <see cref="Set"/> completes that one task,
/// or records a single pending signal when nobody is waiting, so a signal raised while the loop is
/// busy still wakes the next iteration. Cancellation clears the waiter.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Async/WakeSignalTests.cs</tests>
internal sealed class WakeSignal {
  private readonly object _gate = new();
  private TaskCompletionSource<bool>? _waiter;
  private CancellationTokenRegistration _waiterCancellation;
  private bool _pendingSignal;

  /// <summary>Number of waiters currently parked. Always 0 or 1, never more.</summary>
  public int PendingWaiters {
    get {
      lock (_gate) {
        return _waiter is null ? 0 : 1;
      }
    }
  }

  /// <summary>True when a signal arrived while nobody was waiting; the next wait completes at once.</summary>
  public bool HasPendingSignal {
    get {
      lock (_gate) {
        return _pendingSignal;
      }
    }
  }

  /// <summary>
  /// Wakes the pending waiter. When nobody is waiting, records one pending signal; further calls
  /// before the next wait coalesce into that one.
  /// </summary>
  public void Set() {
    TaskCompletionSource<bool> toComplete;
    CancellationTokenRegistration registration;
    lock (_gate) {
      if (_waiter is null) {
        _pendingSignal = true;
        return;
      }
      toComplete = _waiter;
      registration = _waiterCancellation;
      _waiter = null;
      _waiterCancellation = default;
    }
    registration.Dispose();
    toComplete.TrySetResult(true);
  }

  /// <summary>
  /// Returns a task that completes on the next <see cref="Set"/>, or immediately when a signal is
  /// already pending. While a wait is pending, further calls return that same task instead of
  /// queuing another waiter. The token of the call that created the waiter governs its cancellation.
  /// </summary>
  public Task WaitAsync(CancellationToken cancellationToken) {
    if (cancellationToken.IsCancellationRequested) {
      return Task.FromCanceled(cancellationToken);
    }
    lock (_gate) {
      if (_pendingSignal) {
        _pendingSignal = false;
        return Task.CompletedTask;
      }
      if (_waiter is not null) {
        return _waiter.Task;
      }
      var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      _waiter = waiter;
      if (cancellationToken.CanBeCanceled) {
        _waiterCancellation = cancellationToken.UnsafeRegister(
          static (state, token) => ((WakeSignal)state!)._cancelWaiter(token), this);
      }
      return waiter.Task;
    }
  }

  private void _cancelWaiter(CancellationToken token) {
    TaskCompletionSource<bool>? waiter;
    lock (_gate) {
      waiter = _waiter;
      _waiter = null;
      _waiterCancellation = default;
    }
    waiter?.TrySetCanceled(token);
  }
}
