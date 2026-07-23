namespace Whizbang.Core.RunControl;

/// <summary>
/// Default <see cref="IWhizbangLifecycleState"/>: holds the current phase and, on advance, drives the
/// <see cref="WhizbangRunController"/> so every run-control adapter is applied for the new phase.
/// Single instance shared by the initializer (which advances it) and the controller (which enforces).
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public sealed class WhizbangLifecycleState : IWhizbangLifecycleState {
  private readonly WhizbangRunController _controller;
  private LifecyclePhase _phase = LifecyclePhase.Starting;

  /// <summary>Creates the lifecycle state over the run-controller it drives.</summary>
  public WhizbangLifecycleState(WhizbangRunController controller) {
    ArgumentNullException.ThrowIfNull(controller);
    _controller = controller;
  }

  /// <inheritdoc />
  public LifecyclePhase Phase => _phase;

  /// <inheritdoc />
  public async ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
    _phase = phase;
    await _controller.TransitionAsync(phase, cancellationToken).ConfigureAwait(false);
  }
}
