using Whizbang.Core.Workers;

namespace Whizbang.Core.Health;

/// <summary>
/// The <c>"workers"</c> managed-resource health source. The worker pipeline waits on the schema-ready
/// gate before issuing SQL, so while the gate is closed the workers are <b>intentionally held</b> —
/// <see cref="ComponentState.PausedByDesign"/> (healthy by default), not "not running = broken" — and
/// <see cref="ComponentState.Operational"/> once the gate opens.
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
public sealed class WorkerHealthSource : IWhizbangHealthSource {
  private readonly ISchemaReadyGate _gate;

  /// <summary>Creates the worker health source over the schema-ready gate.</summary>
  public WorkerHealthSource(ISchemaReadyGate gate) {
    ArgumentNullException.ThrowIfNull(gate);
    _gate = gate;
  }

  /// <inheritdoc />
  public string Component => "workers";

  /// <inheritdoc />
  public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken)
    => new(new ComponentHealth(_gate.IsReady ? ComponentState.Operational : ComponentState.PausedByDesign));
}
