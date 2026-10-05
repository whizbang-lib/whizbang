// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Async;

/// <summary>
/// A coalescing, level-triggered wake signal for poll loops that never leaves an abandoned waiter
/// behind and never loses a signal.
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
/// on the next iteration without queuing a second waiter. Cancellation clears the waiter.
/// </para>
/// <para>
/// The signal is a level, not an edge. <see cref="Set"/> raises it and completes the parked waiter,
/// and it stays raised until the loop calls <see cref="Consume"/>; until then every
/// <see cref="WaitAsync"/> returns a completed task. A signal that lands while the loop is busy
/// therefore wakes the next iteration even when the waiter it completed was one the loop had already
/// abandoned for another wake source. Completing that waiter and forgetting the signal, as an
/// edge-triggered design does, let the next iteration park a fresh waiter and sleep through it.
/// </para>
/// <para>
/// A loop calls <see cref="Consume"/> once it is awake and before it looks for work, so a signal
/// raised after that point wakes the following iteration. Repeated signals before the consume
/// coalesce into one wake.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Async/WakeSignalTests.cs</tests>
internal sealed class WakeSignal {
  private readonly Lock _gate = new();
  private TaskCompletionSource<bool>? _waiter;
  private CancellationTokenRegistration _waiterCancellation;
  private bool _signaled;

  /// <summary>Number of waiters currently parked. Always 0 or 1, never more.</summary>
  public int PendingWaiters {
    get {
      lock (_gate) {
        return _waiter is null ? 0 : 1;
      }
    }
  }

  /// <summary>True when a signal was raised and not yet consumed; every wait completes at once until then.</summary>
  public bool HasPendingSignal {
    get {
      lock (_gate) {
        return _signaled;
      }
    }
  }

  /// <summary>
  /// Raises the signal and wakes the pending waiter, if any. The signal stays raised until
  /// <see cref="Consume"/>; further calls before then coalesce into it.
  /// </summary>
  public void Set() {
    TaskCompletionSource<bool>? toComplete;
    CancellationTokenRegistration registration;
    lock (_gate) {
      _signaled = true;
      toComplete = _waiter;
      registration = _waiterCancellation;
      _waiter = null;
      _waiterCancellation = default;
    }
    registration.Dispose();
    toComplete?.TrySetResult(true);
  }

  /// <summary>
  /// Lowers the signal once the loop is awake and about to look for work. Called before the work is
  /// examined, so a signal raised after this point wakes the following iteration. A parked waiter is
  /// left in place.
  /// </summary>
  public void Consume() {
    lock (_gate) {
      _signaled = false;
    }
  }

  /// <summary>
  /// Returns a task that completes on the next <see cref="Set"/>, or a completed task while the
  /// signal is raised. While a wait is pending, further calls return that same task instead of
  /// queuing another waiter. The token of the call that created the waiter governs its cancellation.
  /// </summary>
  public Task WaitAsync(CancellationToken cancellationToken) {
    if (cancellationToken.IsCancellationRequested) {
      return Task.FromCanceled(cancellationToken);
    }
    lock (_gate) {
      if (_signaled) {
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
