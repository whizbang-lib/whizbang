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
  private readonly IReadModelsReadyGate? _readModelsReadyGate;

  public LifecyclePhaseWorker(
      IWhizbangLifecycleState lifecycle, ISchemaReadyGate schemaReadyGate,
      IReadModelsReadyGate? readModelsReadyGate = null) {
    ArgumentNullException.ThrowIfNull(lifecycle);
    ArgumentNullException.ThrowIfNull(schemaReadyGate);
    _lifecycle = lifecycle;
    _schemaReadyGate = schemaReadyGate;
    _readModelsReadyGate = readModelsReadyGate;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Connecting, stoppingToken).ConfigureAwait(false);
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Migrating, stoppingToken).ConfigureAwait(false);
    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      return; // host stopping before the schema became ready — leave participants paused
    }

    // CQRS made observable: the schema gate releases the WRITE side (dispatch has its event
    // store and outbox), so commands become safe before queries do. The read-model barrier —
    // Migrate plus the perspective startup repair — releases the read side, and only then is
    // the instance fully Running. Without a read-model gate (partial hosts, old fixtures) the
    // two moments coincide.
    await _lifecycle.AdvanceToAsync(LifecyclePhase.AcceptingCommands, stoppingToken).ConfigureAwait(false);
    if (_readModelsReadyGate is not null) {
      try {
        await _readModelsReadyGate.WaitForReadyAsync(stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        return; // host stopping while reads were still held — fail-closed, phase stays put
      }
    }
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Running, stoppingToken).ConfigureAwait(false);
  }
}
