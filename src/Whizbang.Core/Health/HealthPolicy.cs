using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Whizbang.Core.Health;

/// <summary>
/// Maps a resource's <see cref="ComponentState"/> to an effective <see cref="HealthStatus"/> per
/// <see cref="HealthProbe"/>. This is where "what does healthy mean in this state" is decided.
/// Two built-ins: <see cref="Lenient"/> (the default — intentional states are healthy, so a pod
/// stays ready and serving during a migration) and <see cref="Strict"/> (intentional startup states
/// take the pod out of rotation for readiness).
/// <para>
/// Invariant baked into both built-ins: <see cref="HealthProbe.Liveness"/> is <see cref="HealthStatus.Healthy"/>
/// for every state — an intentional state (or a downed dependency) must never restart the pod.
/// Genuine wedge detection (e.g. a stalled migration) is surfaced as <see cref="ComponentState.Faulted"/>
/// and handled by readiness, not by killing a progressing process.
/// </para>
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
public sealed class HealthPolicy {
  private readonly IReadOnlyDictionary<(ComponentState, HealthProbe), HealthStatus> _map;

  /// <summary>Creates a policy from an explicit (state, probe) → status map. Unmapped pairs default to Healthy.</summary>
  public HealthPolicy(IReadOnlyDictionary<(ComponentState, HealthProbe), HealthStatus> map) {
    ArgumentNullException.ThrowIfNull(map);
    _map = map;
  }

  /// <summary>The effective status for a state on a given probe (Healthy for any unmapped pair).</summary>
  public HealthStatus Map(ComponentState state, HealthProbe probe)
    => _map.TryGetValue((state, probe), out var status) ? status : HealthStatus.Healthy;

  /// <summary>
  /// Default. Intentional states (Starting/Migrating/PausedByDesign) are Healthy on both probes —
  /// the pod is ready and serving during migration; Degraded still serves (readiness Degraded);
  /// only a real Faulted takes it out of rotation.
  /// </summary>
  public static HealthPolicy Lenient { get; } = new(_build(readinessForStartupStates: HealthStatus.Healthy));

  /// <summary>
  /// Stricter. Starting/Migrating/PausedByDesign are <see cref="HealthStatus.Unhealthy"/> on
  /// readiness (pod held out of rotation until fully up), but still Healthy on liveness.
  /// </summary>
  public static HealthPolicy Strict { get; } = new(_build(readinessForStartupStates: HealthStatus.Unhealthy));

  private static Dictionary<(ComponentState, HealthProbe), HealthStatus> _build(HealthStatus readinessForStartupStates) {
    var map = new Dictionary<(ComponentState, HealthProbe), HealthStatus>();
    // Liveness: always Healthy for every state — never restart on an intentional state or a dependency fault.
    foreach (var state in Enum.GetValues<ComponentState>()) {
      map[(state, HealthProbe.Liveness)] = HealthStatus.Healthy;
    }
    // Readiness: serve-ability.
    map[(ComponentState.Operational, HealthProbe.Readiness)] = HealthStatus.Healthy;
    map[(ComponentState.Starting, HealthProbe.Readiness)] = readinessForStartupStates;
    map[(ComponentState.Migrating, HealthProbe.Readiness)] = readinessForStartupStates;
    map[(ComponentState.PausedByDesign, HealthProbe.Readiness)] = readinessForStartupStates;
    map[(ComponentState.Degraded, HealthProbe.Readiness)] = HealthStatus.Degraded;
    map[(ComponentState.Faulted, HealthProbe.Readiness)] = HealthStatus.Unhealthy;
    return map;
  }
}
