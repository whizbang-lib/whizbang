namespace Whizbang.Core.RunControl;

/// <summary>
/// The run-control face of one Whizbang-managed resource — the enforcement counterpart to
/// <c>IWhizbangHealthSource</c>. Whizbang broadcasts the current <see cref="LifecyclePhase"/>; the
/// resource <b>interprets</b> it for itself (stay up, pause, drain, stop, disconnect) and
/// <b>acknowledges</b> by completing the returned task. There is no central "what does this phase mean
/// for this component" table — only the resource knows (the DB stays up during
/// <see cref="LifecyclePhase.Migrating"/>; a worker drains on <see cref="LifecyclePhase.Stopping"/>),
/// so the resource owns the decision, coded to the documented per-phase contract.
/// <para>
/// The response is <b>mandatory</b>: every registered resource is invoked on every transition, even
/// when it has nothing to do (return a completed task) — so the coordinator knows the change reached
/// all of them. It may be async; completion is the acknowledgement, and the coordinator bounds it with
/// a configurable timeout.
/// </para>
/// This generalizes <c>ISchemaReadyGate</c> (which today only gates workers) to every managed
/// resource — transport-consume, the write/append path, offload, temporal, the signal bus.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangLifecycleCoordinatorTests.cs:Transition_BroadcastsToEveryParticipantAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangLifecycleCoordinatorTests.cs:Transition_Barrier_WaitsForAllAcksBeforeReturningAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunControlDiTests.cs:AddWhizbangRunControl_BroadcastsToRegisteredAdaptersAsync</tests>
public interface IWhizbangRunControl {
  /// <summary>Stable component id, shared with the health source's <c>Component</c> id space.</summary>
  string Component { get; }

  /// <summary>
  /// Interpret <paramref name="phase"/> for this resource, do the work, and acknowledge by completing.
  /// Mandatory and idempotent — a resource with nothing to do for a phase returns a completed task.
  /// Throwing or exceeding the coordinator's ack timeout faults the system.
  /// </summary>
  ValueTask OnPhaseAsync(LifecyclePhase phase, CancellationToken cancellationToken);
}
