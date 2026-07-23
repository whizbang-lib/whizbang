using Microsoft.Extensions.Hosting;
using Whizbang.Core.Workers;

namespace Whizbang.Core.RunControl;

/// <summary>
/// Drives the lifecycle phase from the schema-ready gate: advances to <see cref="LifecyclePhase.Migrating"/>
/// at startup (pausing the run-control adapters the controller manages) and to
/// <see cref="LifecyclePhase.Ready"/> once the gate opens (resuming them). If initialization fails the
/// gate never opens, so the phase stays Migrating and the adapters stay paused — the fail-closed
/// behavior. This is the driver that makes run-control move without touching the schema initializer.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
internal sealed class LifecyclePhaseWorker : BackgroundService {
  private readonly IWhizbangLifecycleState _lifecycle;
  private readonly ISchemaReadyGate _schemaReadyGate;

  public LifecyclePhaseWorker(IWhizbangLifecycleState lifecycle, ISchemaReadyGate schemaReadyGate) {
    ArgumentNullException.ThrowIfNull(lifecycle);
    ArgumentNullException.ThrowIfNull(schemaReadyGate);
    _lifecycle = lifecycle;
    _schemaReadyGate = schemaReadyGate;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Migrating, stoppingToken).ConfigureAwait(false);
    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      return; // host stopping before the schema became ready — leave adapters paused
    }
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Ready, stoppingToken).ConfigureAwait(false);
  }
}
