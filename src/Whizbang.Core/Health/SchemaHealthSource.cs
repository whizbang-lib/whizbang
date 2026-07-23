using Whizbang.Core.Workers;

namespace Whizbang.Core.Health;

/// <summary>
/// The <c>"schema"</c> managed-resource health source. Reports <see cref="ComponentState.Migrating"/>
/// while the schema-ready gate is closed and <see cref="ComponentState.Operational"/> once it opens.
/// Under the default <see cref="HealthPolicy.Lenient"/> policy, Migrating maps to <b>ready</b> — so a
/// host doing a long non-blocking startup migration stays in rotation instead of being rolled back,
/// which is exactly what the older <c>SchemaReadyHealthCheck</c>'s always-Unhealthy-while-gated
/// behavior got wrong. (A failed/stalled migration surfaces as <see cref="ComponentState.Faulted"/>
/// once the stall guard is wired; the gate alone only distinguishes migrating vs ready.)
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
public sealed class SchemaHealthSource : IWhizbangHealthSource {
  private readonly ISchemaReadyGate _gate;

  /// <summary>Creates the schema health source over the schema-ready gate.</summary>
  public SchemaHealthSource(ISchemaReadyGate gate) {
    ArgumentNullException.ThrowIfNull(gate);
    _gate = gate;
  }

  /// <inheritdoc />
  public string Component => "schema";

  /// <inheritdoc />
  public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken)
    => new(new ComponentHealth(_gate.IsReady ? ComponentState.Operational : ComponentState.Migrating));
}
