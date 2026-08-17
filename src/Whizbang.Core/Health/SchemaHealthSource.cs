using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Health;

/// <summary>
/// The <c>"schema"</c> managed-resource health source. It judges its state against the current
/// <see cref="LifecyclePhase"/> (which it reads, not a central override): a failed migration drives the
/// lifecycle <see cref="LifecyclePhase.Faulted"/> → <see cref="LifecyclePhase.Halted"/>, surfaced as
/// <see cref="ComponentState.Degraded"/> during the record window (a <b>warning</b>: readiness Degraded)
/// and <see cref="ComponentState.Faulted"/> once <c>Halted</c> (a <b>failure</b>: readiness Unhealthy).
/// A ready gate is <see cref="ComponentState.Operational"/>; otherwise it is
/// <see cref="ComponentState.Connecting"/>/<see cref="ComponentState.Starting"/> while connecting and
/// <see cref="ComponentState.Migrating"/> while the migration runs. Under the Lenient default,
/// <c>Migrating</c> maps to <b>Degraded-but-serving</b> (HTTP 200) — so a long non-blocking startup
/// migration stays in rotation, a bounded-timeout rollout completes, and the state stays visible
/// instead of being rolled back, while a genuine failure escalates warning → failure.
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
    // A genuine init failure drives Faulted -> (record window) -> Halted. Surface the record window as
    // Degraded (a warning: readiness Degraded while the fault is recorded) and the terminal Halted as
    // Faulted (a failure: readiness Unhealthy — out of rotation). A ready gate is Operational; otherwise
    // Connecting/Starting while connecting and Migrating while the migration runs.
    var state = phase is LifecyclePhase.Halted ? ComponentState.Faulted
      : phase is LifecyclePhase.Faulted ? ComponentState.Degraded
      : _gate.IsReady ? ComponentState.Operational
      : phase is LifecyclePhase.Starting or LifecyclePhase.Connecting ? ComponentState.Connecting
      : ComponentState.Migrating;
    return new ValueTask<ComponentHealth>(new ComponentHealth(state));
  }
}
