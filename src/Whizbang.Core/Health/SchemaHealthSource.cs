using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Health;

/// <summary>
/// The <c>"schema"</c> managed-resource health source. It judges its state against the current
/// <see cref="LifecyclePhase"/> (which it reads, not a central override): a failed/wedged migration
/// (phase <see cref="LifecyclePhase.Faulted"/>/<see cref="LifecyclePhase.Halted"/>) is
/// <see cref="ComponentState.Faulted"/>; a ready gate is <see cref="ComponentState.Operational"/>;
/// otherwise it is <see cref="ComponentState.Connecting"/>/<see cref="ComponentState.Starting"/> while
/// connecting and <see cref="ComponentState.Migrating"/> while the migration runs. Under the Lenient
/// default, <c>Migrating</c> maps to <b>ready</b> — so a long non-blocking startup migration stays in
/// rotation instead of being rolled back, and a genuine failure surfaces as <c>Faulted</c>.
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
public sealed class SchemaHealthSource : IWhizbangHealthSource {
  private readonly ISchemaReadyGate _gate;
  private readonly IWhizbangLifecycleState _lifecycle;

  /// <summary>Creates the schema health source over the schema-ready gate and the lifecycle phase.</summary>
  public SchemaHealthSource(ISchemaReadyGate gate, IWhizbangLifecycleState lifecycle) {
    ArgumentNullException.ThrowIfNull(gate);
    ArgumentNullException.ThrowIfNull(lifecycle);
    _gate = gate;
    _lifecycle = lifecycle;
  }

  /// <inheritdoc />
  public string Component => "schema";

  /// <inheritdoc />
  public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
    var phase = _lifecycle.Phase;
    var state = phase is LifecyclePhase.Faulted or LifecyclePhase.Halted ? ComponentState.Faulted
      : _gate.IsReady ? ComponentState.Operational
      : phase is LifecyclePhase.Starting or LifecyclePhase.Connecting ? ComponentState.Connecting
      : ComponentState.Migrating;
    return new ValueTask<ComponentHealth>(new ComponentHealth(state));
  }
}
