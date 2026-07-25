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

  /// <summary>
  /// Drives the system into the fault path — <see cref="LifecyclePhase.Faulted"/>, held for the record
  /// window, then terminal <see cref="LifecyclePhase.Halted"/> — for a failure that happens OUTSIDE a
  /// phase transition (e.g. a background schema initialization that throws). Without this, such a
  /// failure would leave the phase stuck at <see cref="LifecyclePhase.Migrating"/> forever (the ready
  /// gate never opens) and health would keep reporting "still initializing" instead of the truth.
  /// Idempotent: once Faulted/Halted, further calls are no-ops.
  /// </summary>
  ValueTask FaultAsync(CancellationToken cancellationToken);
}
