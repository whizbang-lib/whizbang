namespace Whizbang.Core.RunControl;

/// <summary>
/// Whether a managed resource is currently permitted to do work. The framework's run-controller sets
/// this from the lifecycle phase + config; the resource enforces it. A resource the controller sets
/// to <see cref="Paused"/> reports <c>ComponentState.PausedByDesign</c> to its health source — so
/// control (this) and observation (health) never disagree.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public enum RunState {
  /// <summary>Permitted to do work.</summary>
  Running,
  /// <summary>Intentionally held, resumable (e.g. workers held during a migration).</summary>
  Paused,
  /// <summary>Intentionally shut — drained for shutdown or an operator killswitch.</summary>
  Stopped
}

/// <summary>
/// The service's overall lifecycle phase — the shared signal both run-control (what may run) and
/// health (what a state means) read. Driven forward by startup/shutdown (e.g. the schema initializer
/// moves Starting → Migrating → Ready; graceful shutdown enters Draining).
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public enum LifecyclePhase {
  /// <summary>Process coming up, before/around dependency connect.</summary>
  Starting,
  /// <summary>A schema/data migration is in progress.</summary>
  Migrating,
  /// <summary>Fully up — everything permitted to run.</summary>
  Ready,
  /// <summary>Graceful shutdown — everything transitions to <see cref="RunState.Stopped"/>.</summary>
  Draining,
  /// <summary>Startup failed (e.g. migration failed/stalled) — held closed.</summary>
  Faulted
}
