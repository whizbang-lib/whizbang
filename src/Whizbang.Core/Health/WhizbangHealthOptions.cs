namespace Whizbang.Core.Health;

/// <summary>
/// Configures how Whizbang maps managed-resource states to health, per component. The
/// <see cref="Default"/> policy applies to every component unless overridden in <see cref="Components"/>.
/// Default of defaults is <see cref="HealthPolicy.Lenient"/> — intentional states are healthy, so a
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
