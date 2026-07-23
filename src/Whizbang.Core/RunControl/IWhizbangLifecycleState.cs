namespace Whizbang.Core.RunControl;

/// <summary>
/// The service's current <see cref="LifecyclePhase"/> and the one place it is advanced. Advancing the
/// phase drives the <see cref="WhizbangRunController"/> — so moving to <see cref="LifecyclePhase.Migrating"/>
/// pauses the configured components, and moving to <see cref="LifecyclePhase.Ready"/> resumes them. The
/// schema initializer advances it (Starting → Migrating → Ready/Faulted); graceful shutdown advances it
/// to <see cref="LifecyclePhase.Draining"/>.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public interface IWhizbangLifecycleState {
  /// <summary>The current phase (starts at <see cref="LifecyclePhase.Starting"/>).</summary>
  LifecyclePhase Phase { get; }

  /// <summary>Sets the phase and applies the resulting run-states to every registered control.</summary>
  ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken);
}
