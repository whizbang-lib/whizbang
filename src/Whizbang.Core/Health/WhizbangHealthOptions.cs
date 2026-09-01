namespace Whizbang.Core.Health;

/// <summary>
/// Configures how Whizbang maps managed-resource states to health, per component. The
/// <see cref="Default"/> policy applies to every component unless overridden in <see cref="Components"/>.
/// Default of defaults is <see cref="HealthPolicy.Lenient"/> — intentional states are Degraded-but-serving, so a
/// service stays ready and serving during a long startup migration.
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
/// <tests>tests/Whizbang.Core.Tests/Health/WhizbangHealthAggregatorTests.cs:StrictOverride_HoldsThatComponentOutOfRotationAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Health/WhizbangHealthAggregatorTests.cs:StrictOverride_DoesNotAffectOtherComponentsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Health/WhizbangHealthAggregatorTests.cs:LenientDefault_Migrating_IsReadyAsync</tests>
public sealed class WhizbangHealthOptions {
  /// <summary>Policy applied to any component without an explicit override. Defaults to <see cref="HealthPolicy.Lenient"/>.</summary>
  public HealthPolicy Default { get; set; } = HealthPolicy.Lenient;

  /// <summary>
  /// How long a single health source may take before it is reported as faulted. Default 2 seconds.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A probe must always answer. Sources report on dependencies, and a dependency probe can block
  /// indefinitely: a network call inside a library that does not observe cancellation will not
  /// return and will not throw. Without a bound, one such source takes the entire health response
  /// with it, and the policy that says liveness is always healthy never gets to run.
  /// </para>
  /// <para>
  /// The consequence is worse for liveness than readiness. A readiness probe that hangs holds a pod
  /// out of rotation; a liveness probe that hangs makes kubelet kill a process that is running
  /// perfectly well, then do it again after every restart.
  /// </para>
  /// <para>
  /// Keep this comfortably under the probe's own timeout. A source that cannot answer in two
  /// seconds is not healthy, and saying so is more useful than waiting longer to find out.
  /// </para>
  /// </remarks>
  public TimeSpan SourceTimeout { get; set; } = TimeSpan.FromSeconds(2);

  /// <summary>
  /// Per-component policy overrides keyed by <see cref="IWhizbangHealthSource.Component"/> — e.g. keep
  /// <c>"offload"</c> Strict while everything else is Lenient.
  /// </summary>
  public IDictionary<string, HealthPolicy> Components { get; } =
    new Dictionary<string, HealthPolicy>(StringComparer.Ordinal);

  /// <summary>The effective policy for a component: its override if present, otherwise <see cref="Default"/>.</summary>
  public HealthPolicy PolicyFor(string component) {
    ArgumentNullException.ThrowIfNull(component);
    return Components.TryGetValue(component, out var policy) ? policy : Default;
  }
}
