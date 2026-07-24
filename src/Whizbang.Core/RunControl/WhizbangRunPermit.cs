namespace Whizbang.Core.RunControl;

/// <summary>
/// A re-closable async gate a subsystem awaits before doing a unit of work — the enforcement mechanism
/// behind a run-control adapter. <see cref="RunState.Running"/> is open (await returns immediately),
/// <see cref="RunState.Paused"/> is closed (await blocks until resumed), <see cref="RunState.Stopped"/>
/// cancels awaiters (drain). Generalizes the one-way <c>ISchemaReadyGate</c> to a resumable pause a
/// subsystem checks in its loop, so the run-controller can pause/resume it at runtime.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public sealed class WhizbangRunPermit {
  private readonly object _lock = new();
  private TaskCompletionSource _gate;
  private bool _stopped;

  /// <summary>Creates a permit that starts <see cref="RunState.Running"/> (open).</summary>
  public WhizbangRunPermit() {
    _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _gate.SetResult();
    State = RunState.Running;
  }

  /// <summary>The permit's current run-state.</summary>
  public RunState State { get; private set; }

  /// <summary>True while the permit is open (Running).</summary>
  public bool IsRunning {
    get { lock (_lock) { return State == RunState.Running; } }
  }

  /// <summary>Sets the run-state: Running opens the gate, Paused/Stopped close it (Stopped also cancels awaiters).</summary>
  public void Set(RunState desired) {
    lock (_lock) {
      State = desired;
      switch (desired) {
        case RunState.Running:
          _stopped = false;
          if (!_gate.Task.IsCompleted) {
            _gate.SetResult();
          }
          break;
        case RunState.Paused:
          _stopped = false;
          if (_gate.Task.IsCompleted) {
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          }
          break;
        default: // Stopped
          _stopped = true;
          if (_gate.Task.IsCompleted) {
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          }
          break;
      }
    }
  }

  /// <summary>
  /// Awaits an open permit. Returns immediately when Running; blocks while Paused until resumed;
  /// throws <see cref="OperationCanceledException"/> when Stopped or on <paramref name="cancellationToken"/>.
  /// </summary>
  public Task WaitAsync(CancellationToken cancellationToken) {
    Task gate;
    lock (_lock) {
      if (_stopped) {
        return Task.FromCanceled(new CancellationToken(canceled: true));
      }
      gate = _gate.Task;
    }
    return gate.WaitAsync(cancellationToken);
  }
}
