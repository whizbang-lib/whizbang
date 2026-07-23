using Microsoft.Extensions.Hosting;
using Whizbang.Core.Workers;

namespace Whizbang.Core.RunControl;

/// <summary>
/// Drives the lifecycle phase from the schema-ready gate: advances
/// <see cref="LifecyclePhase.Connecting"/> → <see cref="LifecyclePhase.Migrating"/> at startup (so every
/// participant pauses/stays-up per its own interpretation) and <see cref="LifecyclePhase.Running"/> once
/// the gate opens (so they resume). If initialization never completes the gate never opens, so the
/// phase stays <see cref="LifecyclePhase.Migrating"/> — fail-closed. This is the default driver that
/// moves the machine without touching the schema initializer; drivers that warm connections can hold
/// <see cref="LifecyclePhase.Connecting"/> longer before the migration.
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
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Connecting, stoppingToken).ConfigureAwait(false);
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Migrating, stoppingToken).ConfigureAwait(false);
    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      return; // host stopping before the schema became ready — leave participants paused
    }
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Running, stoppingToken).ConfigureAwait(false);
  }
}
