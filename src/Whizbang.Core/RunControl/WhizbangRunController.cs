namespace Whizbang.Core.RunControl;

/// <summary>
/// Drives every <see cref="IWhizbangRunControl"/> from the lifecycle phase + <see cref="WhizbangRunControlOptions"/>.
/// On a phase transition it resolves the desired <see cref="RunState"/> per component and applies it;
/// an operator can also force a component's state (killswitch) at runtime. This is the central
/// enforcement point behind "serve reads, pause writes + processing during migration" and graceful
/// drain — expressed declaratively instead of by taking the whole process down.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public sealed class WhizbangRunController {
  private readonly IEnumerable<IWhizbangRunControl> _controls;
  private readonly WhizbangRunControlOptions _options;

  /// <summary>Creates a controller over the given run-controls and options.</summary>
  public WhizbangRunController(IEnumerable<IWhizbangRunControl> controls, WhizbangRunControlOptions options) {
    ArgumentNullException.ThrowIfNull(controls);
    ArgumentNullException.ThrowIfNull(options);
    _controls = controls;
    _options = options;
  }

  /// <summary>
  /// Applies the resolved run-state for <paramref name="phase"/> to every registered control.
  /// </summary>
  public async ValueTask TransitionAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
    foreach (var control in _controls) {
      await control.ApplyAsync(_options.Resolve(control.Component, phase), cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Operator killswitch: force <paramref name="component"/> to <paramref name="state"/> (or clear the
  /// override with <see langword="null"/>), then re-apply it under the given current phase.
  /// </summary>
  public async ValueTask SetOverrideAsync(
      string component, RunState? state, LifecyclePhase currentPhase, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(component);
    if (state is null) {
      _options.Overrides.Remove(component);
    } else {
      _options.Overrides[component] = state.Value;
    }
    foreach (var control in _controls) {
      if (string.Equals(control.Component, component, StringComparison.Ordinal)) {
        await control.ApplyAsync(_options.Resolve(component, currentPhase), cancellationToken).ConfigureAwait(false);
      }
    }
  }
}
