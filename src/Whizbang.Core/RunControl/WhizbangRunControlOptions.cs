namespace Whizbang.Core.RunControl;

/// <summary>
/// Resolves the desired <see cref="RunState"/> for a component in a given <see cref="LifecyclePhase"/>.
/// Resolution order: an operator <see cref="Overrides"/> wins; then <see cref="LifecyclePhase.Draining"/>
/// stops everything; then a per-<see cref="Phases"/> entry; otherwise <see cref="RunState.Running"/>.
/// The <see cref="Default"/> factory pauses processing + writes during a migration.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public sealed class WhizbangRunControlOptions {
  /// <summary>Desired run-state per <c>(component, phase)</c>. Missing entries fall through to Running.</summary>
  public IDictionary<(string Component, LifecyclePhase Phase), RunState> Phases { get; } =
    new Dictionary<(string, LifecyclePhase), RunState>();

  /// <summary>Operator killswitch: force a component's run-state regardless of phase.</summary>
  public IDictionary<string, RunState> Overrides { get; } =
    new Dictionary<string, RunState>(StringComparer.Ordinal);

  /// <summary>Resolves the desired run-state for a component in a phase (override → drain → phase-table → Running).</summary>
  public RunState Resolve(string component, LifecyclePhase phase) {
    ArgumentNullException.ThrowIfNull(component);
    if (Overrides.TryGetValue(component, out var overridden)) {
      return overridden;
    }
    if (phase == LifecyclePhase.Draining) {
      return RunState.Stopped;
    }
    return Phases.TryGetValue((component, phase), out var desired) ? desired : RunState.Running;
  }

  /// <summary>
  /// The default policy: during <see cref="LifecyclePhase.Migrating"/> the processing + write
  /// components are Paused (reads/other components keep running); Draining stops everything (handled
  /// in <see cref="Resolve"/>). Standard component ids: <c>workers</c>, <c>transport-consume</c>,
  /// <c>writes</c>.
  /// </summary>
  public static WhizbangRunControlOptions Default() {
    var options = new WhizbangRunControlOptions();
    foreach (var component in new[] { "workers", "transport-consume", "writes" }) {
      options.Phases[(component, LifecyclePhase.Migrating)] = RunState.Paused;
      options.Phases[(component, LifecyclePhase.Starting)] = RunState.Paused;
      options.Phases[(component, LifecyclePhase.Faulted)] = RunState.Paused;
    }
    return options;
  }
}
