namespace Whizbang.Core.RunControl;

/// <summary>
/// The run-control (killswitch) hook for one Whizbang-managed resource — the enforcement counterpart
/// to <c>IWhizbangHealthSource</c>. The framework's <see cref="WhizbangRunController"/> asks the
/// resource to enter a desired <see cref="RunState"/> as the lifecycle phase changes (or on an
/// operator override); the resource is responsible for actually pausing/resuming/stopping its work.
/// This generalizes <c>ISchemaReadyGate</c> (which today only gates workers) to every managed
/// resource — transport-consume, the write/append path, offload, and so on.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public interface IWhizbangRunControl {
  /// <summary>Stable component id, shared with the health source's <c>Component</c> id space.</summary>
  string Component { get; }

  /// <summary>The resource's current run-state.</summary>
  RunState Current { get; }

  /// <summary>
  /// Asks the resource to enter <paramref name="desired"/>. Idempotent — applying the current state
  /// is a no-op. Resuming (<see cref="RunState.Running"/>) must not require a restart.
  /// </summary>
  ValueTask ApplyAsync(RunState desired, CancellationToken cancellationToken);
}
