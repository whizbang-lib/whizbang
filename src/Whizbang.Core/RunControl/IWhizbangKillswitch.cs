namespace Whizbang.Core.RunControl;

/// <summary>
/// The operator killswitch over Whizbang's managed resources. Coarse global controls drive the whole
/// system through the lifecycle machine (pause/resume/stop everything); the per-component controls pin a
/// single resource (e.g. drain one transport for maintenance) independent of the system phase. All go
/// through the coordinated state machine, so every resource acknowledges each change.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public interface IWhizbangKillswitch {
  /// <summary>Pause everything: advances <see cref="LifecyclePhase.Pausing"/> → <see cref="LifecyclePhase.Paused"/>.</summary>
  ValueTask PauseAsync(CancellationToken cancellationToken = default);

  /// <summary>Resume everything: advances <see cref="LifecyclePhase.Resuming"/> → <see cref="LifecyclePhase.Running"/>.</summary>
  ValueTask ResumeAsync(CancellationToken cancellationToken = default);

  /// <summary>Stop everything (graceful): advances <see cref="LifecyclePhase.Stopping"/> → <see cref="LifecyclePhase.Stopped"/>.</summary>
  ValueTask StopAsync(CancellationToken cancellationToken = default);

  /// <summary>Pin one component to <paramref name="phase"/> regardless of the system phase (e.g. drain a transport).</summary>
  ValueTask OverrideComponentAsync(string component, LifecyclePhase phase, CancellationToken cancellationToken = default);

  /// <summary>Clear a component's override, returning it to the current system phase.</summary>
  ValueTask ClearComponentOverrideAsync(string component, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IWhizbangKillswitch"/>: coarse controls drive the shared
/// <see cref="IWhizbangLifecycleState"/> through the transitional→settled pairs; per-component controls
/// go through the <see cref="WhizbangLifecycleCoordinator"/>'s override map at the current system phase.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public sealed class WhizbangKillswitch : IWhizbangKillswitch {
  private readonly IWhizbangLifecycleState _lifecycle;
  private readonly WhizbangLifecycleCoordinator _coordinator;

  /// <summary>Creates the killswitch over the lifecycle state and coordinator.</summary>
  public WhizbangKillswitch(IWhizbangLifecycleState lifecycle, WhizbangLifecycleCoordinator coordinator) {
    ArgumentNullException.ThrowIfNull(lifecycle);
    ArgumentNullException.ThrowIfNull(coordinator);
    _lifecycle = lifecycle;
    _coordinator = coordinator;
  }

  /// <inheritdoc />
  public async ValueTask PauseAsync(CancellationToken cancellationToken = default) {
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Pausing, cancellationToken).ConfigureAwait(false);
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Paused, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask ResumeAsync(CancellationToken cancellationToken = default) {
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Resuming, cancellationToken).ConfigureAwait(false);
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Running, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask StopAsync(CancellationToken cancellationToken = default) {
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Stopping, cancellationToken).ConfigureAwait(false);
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Stopped, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public ValueTask OverrideComponentAsync(string component, LifecyclePhase phase, CancellationToken cancellationToken = default)
    => _coordinator.SetComponentOverrideAsync(component, phase, _lifecycle.Phase, cancellationToken);

  /// <inheritdoc />
  public ValueTask ClearComponentOverrideAsync(string component, CancellationToken cancellationToken = default)
    => _coordinator.SetComponentOverrideAsync(component, null, _lifecycle.Phase, cancellationToken);
}
