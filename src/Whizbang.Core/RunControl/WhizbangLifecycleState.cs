// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// <remarks>
/// Advances are serialized, and two moves are refused (#2045): nothing leaves a terminal phase (a stop is final), and
/// while the run-control path holds the instance halted (Pausing, Paused, StandingBy, Stopping) a startup-ladder phase
/// is ignored, so a startup step that finishes late cannot silently resume a paused or stood-down instance. Only the
/// path moves a halted instance on: Resuming, a further halt, or the settled phase of its own halt.
/// </remarks>
/// <docs>resilience/managed-resource-run-control</docs>
public sealed class WhizbangLifecycleState : IWhizbangLifecycleState, IDisposable {
  private readonly WhizbangLifecycleCoordinator _coordinator;
  private readonly WhizbangLifecycleOptions _options;
  private readonly TimeProvider _timeProvider;
  private volatile LifecyclePhase _phase = LifecyclePhase.Starting;
  // #1638: the phase being entered, set before the broadcast so a writer of the instance's row never reads the phase
  // the transition is leaving while the transition is being recorded.
  private volatile LifecyclePhase _announced = LifecyclePhase.Starting;
  private readonly SemaphoreSlim _advance = new(1, 1);

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
  public LifecyclePhase AnnouncedPhase => _announced;

  /// <inheritdoc />
  public async ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
    await _advance.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      if (!_accepts(_announced, phase)) {
        return;
      }
      _announced = phase;
      await _coordinator.TransitionAsync(phase, cancellationToken).ConfigureAwait(false);
      _phase = phase;
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception) {
      await _faultAndHaltAsync(cancellationToken).ConfigureAwait(false);
    } finally {
      _advance.Release();
    }
  }

  /// <inheritdoc />
  public void Dispose() => _advance.Dispose();

  /// <summary>
  /// Whether the machine may move from <paramref name="from"/> to <paramref name="to"/> (#2045): never out of a terminal
  /// phase, and never onto the startup ladder while the run-control path holds the instance halted.
  /// </summary>
  private static bool _accepts(LifecyclePhase from, LifecyclePhase to) {
    if (from.IsTerminal()) {
      return false;
    }
    var halted = from is LifecyclePhase.Pausing or LifecyclePhase.Paused or LifecyclePhase.StandingBy or LifecyclePhase.Stopping;
    var startupLadder = to is LifecyclePhase.Starting or LifecyclePhase.Connecting or LifecyclePhase.Migrating
      or LifecyclePhase.AcceptingCommands or LifecyclePhase.Running;
    return !(halted && startupLadder);
  }

  /// <inheritdoc />
  public ValueTask FaultAsync(CancellationToken cancellationToken) {
    // Idempotent: once the fault path has started, further calls are no-ops.
    if (_phase is LifecyclePhase.Faulted or LifecyclePhase.Halted) {
      return ValueTask.CompletedTask;
    }

    return _faultAndHaltAsync(cancellationToken);
  }

  private async ValueTask _faultAndHaltAsync(CancellationToken cancellationToken) {
    _phase = LifecyclePhase.Faulted;
    _announced = LifecyclePhase.Faulted;
    await _broadcastBestEffortAsync(LifecyclePhase.Faulted, cancellationToken).ConfigureAwait(false);

    try {
      await Task.Delay(_options.FaultRecordWindow, _timeProvider, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      // Shutting down during the record window — proceed straight to Halted.
    }

    _phase = LifecyclePhase.Halted;
    _announced = LifecyclePhase.Halted;
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
