namespace Whizbang.Core.Health;

/// <summary>
/// The state a Whizbang-managed resource reports about itself. Only the resource can say whether it
/// is <see cref="Faulted"/>; the framework decides what each state <em>means</em> for a probe via a
/// <see cref="HealthPolicy"/>. Intentional states (<see cref="Starting"/>, <see cref="Migrating"/>,
/// <see cref="PausedByDesign"/>) are "operating correctly for the state it is in", not failures.
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
/// <tests>tests/Whizbang.Core.Tests/Health/WhizbangHealthAggregatorTests.cs:Aggregate_WorstStatusWinsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Health/WhizbangHealthAggregatorTests.cs:Liveness_NeverFailsForIntentionalStates_UnderStrictAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Health/ConnectivityHealthSourceTests.cs:AssumedHealthy_IsPhaseAware_NeverFaultsFromProbeAsync</tests>
public enum ComponentState {
  /// <summary>Running normally.</summary>
  Operational,
  /// <summary>Coming up (process boot). Intentional and transient.</summary>
  Starting,
  /// <summary>Warming a network connection (DB/transport/offload). Intentional and transient.</summary>
  Connecting,
  /// <summary>A schema/data migration is in progress. Intentional.</summary>
  Migrating,
  /// <summary>
  /// The blocking startup steps have drained and every readiness contributor answered — fully up.
  /// Sits between <see cref="Migrating"/> and <see cref="Operational"/> in the lifecycle: distinct
  /// from Operational so a probe can tell "the schema is ready" from "the pipeline drained".
  /// </summary>
  Ready,
  /// <summary>Gated/drained on purpose (e.g. workers held during a migration or pause). Intentional.</summary>
  PausedByDesign,
  /// <summary>Finishing in-flight work for a graceful stop, taking no new. Intentional.</summary>
  Draining,
  /// <summary>Working but impaired (slow, partial). Still serves.</summary>
  Degraded,
  /// <summary>Genuinely broken — a real fault (dependency dead, migration stalled/failed).</summary>
  Faulted
}

/// <summary>
/// A single managed resource's self-reported health: its <see cref="ComponentState"/> plus optional
/// human-readable detail (e.g. "migrating: step 7/12, 420k/1.35M rows").
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
public readonly record struct ComponentHealth(ComponentState State, string? Detail = null);

/// <summary>
/// Which Kubernetes-style probe a health evaluation is answering. The mapping from
/// <see cref="ComponentState"/> to a status can differ per probe — e.g. a <see cref="ComponentState.Migrating"/>
/// resource is alive (<see cref="Liveness"/>) but, under a strict policy, not yet in rotation
/// (<see cref="Readiness"/>).
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
/// <tests>tests/Whizbang.Core.Tests/Health/WhizbangHealthAggregatorTests.cs:Faulted_FailsReadinessAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Health/WhizbangHealthAggregatorTests.cs:Faulted_StillAlive_NeverRestartsOnDependencyFaultAsync</tests>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangManagedHealthCheckTests.cs:Liveness_Faulted_StaysHealthyAsync</tests>
public enum HealthProbe {
  /// <summary>"Is the process alive?" — should almost never fail on an intentional state or a dependency fault.</summary>
  Liveness,
  /// <summary>"Can the process serve?" — reflects serve-ability; a real fault takes the pod out of rotation.</summary>
  Readiness
}
