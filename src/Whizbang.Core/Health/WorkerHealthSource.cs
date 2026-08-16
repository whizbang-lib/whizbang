using Whizbang.Core.RunControl;

namespace Whizbang.Core.Health;

/// <summary>
/// The <c>"workers"</c> managed-resource health source. It mirrors the worker pipeline's phase-driven
/// run-state: <see cref="ComponentState.Operational"/> while <see cref="LifecyclePhase.Running"/>,
/// <see cref="ComponentState.Draining"/> while <see cref="LifecyclePhase.Stopping"/>,
/// <see cref="ComponentState.Faulted"/> on the fault path, and <see cref="ComponentState.PausedByDesign"/>
/// otherwise (workers intentionally held during startup, connecting, migrating, or pause) — healthy by
/// default, not "not running = broken".
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
public sealed class WorkerHealthSource : IWhizbangHealthSource {
  private readonly IWhizbangLifecycleState _lifecycle;

  /// <summary>Creates the worker health source over the lifecycle phase.</summary>
  public WorkerHealthSource(IWhizbangLifecycleState lifecycle) {
    ArgumentNullException.ThrowIfNull(lifecycle);
    _lifecycle = lifecycle;
  }

  /// <inheritdoc />
  public string Component => "workers";

  /// <inheritdoc />
  public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
    var state = _lifecycle.Phase switch {
      // AcceptingCommands: the worker pumps are schema-gate-driven and already run while the
      // read side is still held — the workers component is operational, only lenses refuse.
      LifecyclePhase.Running or LifecyclePhase.AcceptingCommands => ComponentState.Operational,
      LifecyclePhase.Stopping => ComponentState.Draining,
      LifecyclePhase.Faulted or LifecyclePhase.Halted => ComponentState.Faulted,
      _ => ComponentState.PausedByDesign
    };
    return new ValueTask<ComponentHealth>(new ComponentHealth(state));
  }
}
