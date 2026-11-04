// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.RunControl;

/// <summary>
/// The instance's one run permit (#2045): what every claiming worker, the transport consumer and the role elector obey.
/// <see cref="RunState.Running"/> is open; <see cref="RunState.Paused"/> holds every new hold until it reopens;
/// <see cref="RunState.Stopped"/> cancels every waiter and never reopens. A worker takes a hold
/// (<see cref="EnterAsync"/>) around each unit of work it takes, so the run-control path can wait for the work in hand
/// to be finished (<see cref="WhenNothingHeldAsync"/>) before it releases what is left.
/// </summary>
/// <remarks>
/// Only the run-control path (<see cref="IWhizbangRunController"/>) changes the permit, through
/// <see cref="ChangeAsync"/>, which is internal for that reason: nothing else can close or reopen it. An obeyer that
/// cannot take holds around its work (a transport consumer, which is called back rather than looping) registers with
/// <see cref="Obey"/> and acts on each change; the change completes only when every obeyer has acted on it.
/// </remarks>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunPermitTests.cs</tests>
public sealed class WhizbangRunPermit {
  private readonly Lock _lock = new();
  private readonly List<Func<RunState, CancellationToken, Task>> _obeyers = [];
  private TaskCompletionSource _gate;
  private TaskCompletionSource? _idle;
  private int _held;

  /// <summary>Creates a permit that starts <see cref="RunState.Running"/> (open).</summary>
  public WhizbangRunPermit() {
    _gate = _newGate();
    _gate.SetResult();
    State = RunState.Running;
  }

  /// <summary>The permit's current run-state.</summary>
  public RunState State { get; private set; }

  /// <summary>True while the permit is open (Running).</summary>
  public bool IsRunning {
    get {
      lock (_lock) {
        return State == RunState.Running;
      }
    }
  }

  /// <summary>How many holds are in hand: units of work taken and not yet finished.</summary>
  public int Held {
    get {
      lock (_lock) {
        return _held;
      }
    }
  }

  /// <summary>
  /// Moves the permit to <paramref name="desired"/> and returns once every obeyer has acted on the change. A stopped
  /// permit stays stopped, and a change to the state the permit is already in tells no obeyer.
  /// </summary>
  /// <param name="desired">The state to move to.</param>
  /// <param name="cancellationToken">Passed to each obeyer.</param>
  internal async ValueTask ChangeAsync(RunState desired, CancellationToken cancellationToken) {
    Func<RunState, CancellationToken, Task>[] obeyers;
    lock (_lock) {
      if (State == RunState.Stopped || State == desired) {
        return;
      }
      State = desired;
      if (desired == RunState.Running) {
        _gate.TrySetResult();
      } else {
        if (_gate.Task.IsCompleted) {
          _gate = _newGate();
        }
        if (desired == RunState.Stopped) {
          _gate.TrySetCanceled(new CancellationToken(canceled: true));
        }
      }
      obeyers = [.. _obeyers];
    }
    await Task.WhenAll(obeyers.Select(obey => obey(desired, cancellationToken))).ConfigureAwait(false);
  }

  /// <summary>Completes when the permit is open; canceled when it is stopped.</summary>
  /// <param name="cancellationToken">Ends the wait.</param>
  /// <returns>A task that completes when the permit is open.</returns>
  public Task WaitAsync(CancellationToken cancellationToken) {
    Task gate;
    lock (_lock) {
      gate = _gate.Task;
    }
    return gate.WaitAsync(cancellationToken);
  }

  /// <summary>
  /// Takes a hold once the permit is open: a unit of work in hand, counted until the hold is disposed. Waits while the
  /// permit is paused.
  /// </summary>
  /// <param name="cancellationToken">Ends the wait; no hold is taken.</param>
  /// <returns>The hold. Dispose it when the unit of work is finished.</returns>
  /// <exception cref="OperationCanceledException">The permit is stopped, or the wait was canceled.</exception>
  public async ValueTask<RunPermitHold> EnterAsync(CancellationToken cancellationToken) {
    while (true) {
      Task gate;
      lock (_lock) {
        if (State == RunState.Running) {
          _held++;
          return new RunPermitHold(this);
        }
        gate = _gate.Task;
      }
      await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>Completes when no hold is in hand.</summary>
  /// <param name="cancellationToken">Ends the wait.</param>
  /// <returns>A task that completes when the last hold is released.</returns>
  public Task WhenNothingHeldAsync(CancellationToken cancellationToken) {
    Task idle;
    lock (_lock) {
      if (_held == 0) {
        return Task.CompletedTask;
      }
      _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      idle = _idle.Task;
    }
    return idle.WaitAsync(cancellationToken);
  }

  /// <summary>
  /// Registers <paramref name="onChange"/> to act on every change of the permit, before the change completes: a
  /// transport consumer pauses its subscriptions on a close and resumes them on a reopening.
  /// </summary>
  /// <param name="onChange">Called with the new state; the change awaits the task it returns.</param>
  /// <returns>Disposing it stops the notifications.</returns>
  public IDisposable Obey(Func<RunState, CancellationToken, Task> onChange) {
    ArgumentNullException.ThrowIfNull(onChange);
    lock (_lock) {
      _obeyers.Add(onChange);
    }
    return new Obedience(this, onChange);
  }

  internal void Release() {
    TaskCompletionSource? idle = null;
    lock (_lock) {
      _held--;
      if (_held == 0) {
        idle = _idle;
        _idle = null;
      }
    }
    idle?.TrySetResult();
  }

  private static TaskCompletionSource _newGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

  private sealed class Obedience(WhizbangRunPermit permit, Func<RunState, CancellationToken, Task> onChange) : IDisposable {
    private int _disposed;

    public void Dispose() {
      if (Interlocked.Exchange(ref _disposed, 1) == 0) {
        lock (permit._lock) {
          _ = permit._obeyers.Remove(onChange);
        }
      }
    }
  }
}
