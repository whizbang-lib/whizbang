namespace Whizbang.Core.RunControl;

/// <summary>
/// A ready-made <see cref="IWhizbangRunControl"/> that interprets the lifecycle phase into a
/// <see cref="WhizbangRunPermit"/> gate state. A subsystem awaits the permit in its loop; on each
/// transition this adapter maps the phase to <see cref="RunState"/> (via its interpreter) and flips the
/// permit — pausing, resuming, or draining the subsystem. Because interpretation is a delegate, each
/// subsystem supplies what a phase means <em>for it</em>; <see cref="ForWorkers"/> is the common
/// "run when Running, drain on Stopping, otherwise pause" default.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunPermitTests.cs:Adapter_InterpretsPhaseIntoPermitAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunPermitTests.cs:Adapter_DrivenByCoordinator_PausesThenRunsThenDrainsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunPermitTests.cs:ForWorkers_InterpretationAsync</tests>
public sealed class RunPermitControl : IWhizbangRunControl {
  private readonly WhizbangRunPermit _permit;
  private readonly Func<LifecyclePhase, RunState> _interpret;

  /// <summary>Creates an adapter for <paramref name="component"/> that drives <paramref name="permit"/> via <paramref name="interpret"/>.</summary>
  public RunPermitControl(string component, WhizbangRunPermit permit, Func<LifecyclePhase, RunState> interpret) {
    ArgumentNullException.ThrowIfNull(component);
    ArgumentNullException.ThrowIfNull(permit);
    ArgumentNullException.ThrowIfNull(interpret);
    Component = component;
    _permit = permit;
    _interpret = interpret;
  }

  /// <inheritdoc />
  public string Component { get; }

  /// <summary>The permit's current gate state (for diagnostics/tests).</summary>
  public RunState Current => _permit.State;

  /// <inheritdoc />
  public ValueTask OnPhaseAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
    _permit.Set(_interpret(phase));
    return default;
  }

  /// <summary>
  /// The common worker/consumer interpretation: <see cref="RunState.Running"/> only while
  /// <see cref="LifecyclePhase.Running"/>; <see cref="RunState.Stopped"/> (drain — finish in-flight,
  /// take no new) on <see cref="LifecyclePhase.Stopping"/>/<see cref="LifecyclePhase.Stopped"/> and the
  /// fault path; otherwise <see cref="RunState.Paused"/> (held during startup, migration, and pause).
  /// </summary>
  public static RunPermitControl ForWorkers(string component, WhizbangRunPermit permit)
    => new(component, permit, InterpretForWorkers);

  /// <summary>The <see cref="ForWorkers"/> phase→gate mapping, exposed for reuse/testing.</summary>
  public static RunState InterpretForWorkers(LifecyclePhase phase) => phase switch {
    LifecyclePhase.Running => RunState.Running,
    LifecyclePhase.Stopping or LifecyclePhase.Stopped or LifecyclePhase.Faulted or LifecyclePhase.Halted => RunState.Stopped,
    _ => RunState.Paused
  };
}
