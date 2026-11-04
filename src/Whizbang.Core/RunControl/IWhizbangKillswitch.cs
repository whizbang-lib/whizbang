// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.RunControl;

/// <summary>
/// The operator killswitch over Whizbang's managed resources. Pause, resume and stop take the one run-control path
/// (<see cref="IWhizbangRunController"/>, #2045), the same path as a stand-down and host shutdown: the run permit closes,
/// held work and roles are released, and the phase is recorded. The per-component controls pin a single run-control
/// participant (e.g. one an application registered for its own resource) independent of the system phase.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangKillswitchTests.cs:PauseAsync_DrivesPausingThenPausedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangKillswitchTests.cs:StopAsync_DrivesStoppingThenStoppedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangKillswitchTests.cs:OverrideComponent_PinsOneResource_IndependentOfSystemPhaseAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangKillswitchTests.cs:StopAsync_ClosesThePermitForGood_AndAResumeIsRefusedAsync</tests>
public interface IWhizbangKillswitch {
  /// <summary>Pause everything through the run-control path: Pausing, then Paused once held work and roles are released.</summary>
  ValueTask PauseAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Lift the operator's pause: Resuming, then Running once the permit is open. A stand-down that still holds keeps the
  /// instance standing by (only its revival lifts it). Refused after a stop.
  /// </summary>
  ValueTask ResumeAsync(CancellationToken cancellationToken = default);

  /// <summary>Stop everything for good through the run-control path: Stopping, then Stopped once held work and roles are released.</summary>
  ValueTask StopAsync(CancellationToken cancellationToken = default);

  /// <summary>Pin one component to <paramref name="phase"/> regardless of the system phase (e.g. drain a transport).</summary>
  ValueTask OverrideComponentAsync(string component, LifecyclePhase phase, CancellationToken cancellationToken = default);

  /// <summary>Clear a component's override, returning it to the current system phase.</summary>
  ValueTask ClearComponentOverrideAsync(string component, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IWhizbangKillswitch"/>: coarse controls take the run-control path; per-component controls go
/// through the <see cref="WhizbangLifecycleCoordinator"/>'s override map at the current system phase.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public sealed class WhizbangKillswitch : IWhizbangKillswitch {
  private readonly IWhizbangRunController _runControl;
  private readonly IWhizbangLifecycleState _lifecycle;
  private readonly WhizbangLifecycleCoordinator _coordinator;

  /// <summary>Creates the killswitch over the run-control path, the lifecycle state and the coordinator.</summary>
  public WhizbangKillswitch(
      IWhizbangRunController runControl, IWhizbangLifecycleState lifecycle, WhizbangLifecycleCoordinator coordinator) {
    ArgumentNullException.ThrowIfNull(runControl);
    ArgumentNullException.ThrowIfNull(lifecycle);
    ArgumentNullException.ThrowIfNull(coordinator);
    _runControl = runControl;
    _lifecycle = lifecycle;
    _coordinator = coordinator;
  }

  /// <inheritdoc />
  public async ValueTask PauseAsync(CancellationToken cancellationToken = default) =>
    await _runControl.HaltAsync(RunHaltReason.Pause, cancellationToken).ConfigureAwait(false);

  /// <inheritdoc />
  public async ValueTask ResumeAsync(CancellationToken cancellationToken = default) =>
    await _runControl.ResumeAsync(RunHaltReason.Pause, cancellationToken).ConfigureAwait(false);

  /// <inheritdoc />
  public async ValueTask StopAsync(CancellationToken cancellationToken = default) =>
    await _runControl.HaltAsync(RunHaltReason.Stop, cancellationToken).ConfigureAwait(false);

  /// <inheritdoc />
  public ValueTask OverrideComponentAsync(string component, LifecyclePhase phase, CancellationToken cancellationToken = default)
    => _coordinator.SetComponentOverrideAsync(component, phase, _lifecycle.Phase, cancellationToken);

  /// <inheritdoc />
  public ValueTask ClearComponentOverrideAsync(string component, CancellationToken cancellationToken = default)
    => _coordinator.SetComponentOverrideAsync(component, null, _lifecycle.Phase, cancellationToken);
}
