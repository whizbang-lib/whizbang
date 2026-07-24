namespace Whizbang.Core.RunControl;

/// <summary>
/// The service's current <see cref="LifecyclePhase"/> and the one place it is advanced. Advancing
/// broadcasts the phase to every managed resource through the <see cref="WhizbangLifecycleCoordinator"/>
/// (await-all-ack), so moving to <see cref="LifecyclePhase.Migrating"/> is applied everywhere before it
/// settles, and moving to <see cref="LifecyclePhase.Running"/> resumes what was paused. The
/// schema-driven <c>LifecyclePhaseWorker</c> advances it (Starting → Connecting → Migrating → Running);
/// graceful shutdown advances it to <see cref="LifecyclePhase.Stopping"/>; a failed transition drives
/// it to <see cref="LifecyclePhase.Faulted"/> → <see cref="LifecyclePhase.Halted"/>.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public interface IWhizbangLifecycleState {
  /// <summary>The current phase (starts at <see cref="LifecyclePhase.Starting"/>).</summary>
  LifecyclePhase Phase { get; }

  /// <summary>Advances to <paramref name="phase"/>, broadcasting it to every resource and awaiting all acks.</summary>
  ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken);
}
