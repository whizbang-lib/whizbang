namespace Whizbang.Core.RunControl;

/// <summary>
/// The gate vocabulary of a <see cref="WhizbangRunPermit"/> — a subsystem-internal mechanism, NOT the
/// run-control interface currency. A resource interprets the lifecycle <see cref="LifecyclePhase"/>
/// (in its <c>IWhizbangRunControl.OnPhaseAsync</c>) and drives its permit to one of these: open
/// (<see cref="Running"/>), closed-but-resumable (<see cref="Paused"/>), or draining/cancelling
/// (<see cref="Stopped"/>). The lifecycle phase is what crosses the interface; this is how a permit
/// expresses the resulting gate state.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunPermitTests.cs:Paused_BlocksUntilResumedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunPermitTests.cs:Stopped_CancelsAwaitersAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunPermitTests.cs:ForWorkers_InterpretationAsync</tests>
public enum RunState {
  /// <summary>Permit open — awaiters proceed.</summary>
  Running,
  /// <summary>Permit closed but resumable — awaiters block until re-opened.</summary>
  Paused,
  /// <summary>Permit draining — awaiters are cancelled (finish in-flight, take no new).</summary>
  Stopped
}
