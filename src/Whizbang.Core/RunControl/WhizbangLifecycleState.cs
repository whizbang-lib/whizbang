namespace Whizbang.Core.RunControl;

/// <summary>
/// Default <see cref="IWhizbangLifecycleState"/>: holds the current phase and advances it through the
/// <see cref="WhizbangLifecycleCoordinator"/> (broadcast + await-all-ack + queue). It also owns the
/// <b>fault path</b>: if a transition fails (a resource throws or times out), it drives the system to
/// <see cref="LifecyclePhase.Faulted"/>, holds there for <see cref="WhizbangLifecycleOptions.FaultRecordWindow"/>
/// so resources can record/report, then reaches the terminal <see cref="LifecyclePhase.Halted"/>. Single
/// instance shared by whoever advances it (the schema-driven <c>LifecyclePhaseWorker</c>) and every
/// resource that reads the phase.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public sealed class WhizbangLifecycleState : IWhizbangLifecycleState {
  private readonly WhizbangLifecycleCoordinator _coordinator;
  private readonly WhizbangLifecycleOptions _options;
  private readonly TimeProvider _timeProvider;
  private volatile LifecyclePhase _phase = LifecyclePhase.Starting;

  /// <summary>Creates the lifecycle state over the coordinator it drives.</summary>
  public WhizbangLifecycleState(
      WhizbangLifecycleCoordinator coordinator,
      WhizbangLifecycleOptions options,
      TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(coordinator);
    ArgumentNullException.ThrowIfNull(options);
    _coordinator = coordinator;
    _options = options;
    _timeProvider = timeProvider ?? TimeProvider.System;
  }

  /// <inheritdoc />
  public LifecyclePhase Phase => _phase;

  /// <inheritdoc />
  public async ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
    try {
      await _coordinator.TransitionAsync(phase, cancellationToken).ConfigureAwait(false);
      _phase = phase;
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception) {
      await _faultAndHaltAsync(cancellationToken).ConfigureAwait(false);
    }
  }

  private async ValueTask _faultAndHaltAsync(CancellationToken cancellationToken) {
    _phase = LifecyclePhase.Faulted;
    await _broadcastBestEffortAsync(LifecyclePhase.Faulted, cancellationToken).ConfigureAwait(false);

    try {
      await Task.Delay(_options.FaultRecordWindow, _timeProvider, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      // Shutting down during the record window — proceed straight to Halted.
    }

    _phase = LifecyclePhase.Halted;
    await _broadcastBestEffortAsync(LifecyclePhase.Halted, CancellationToken.None).ConfigureAwait(false);
  }

  private async ValueTask _broadcastBestEffortAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
    try {
      await _coordinator.TransitionAsync(phase, cancellationToken).ConfigureAwait(false);
    } catch {
      // The system is already dying; a resource that also fails to record must not mask the fault path.
    }
  }
}
