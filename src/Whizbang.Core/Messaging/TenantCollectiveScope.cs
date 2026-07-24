namespace Whizbang.Core.Messaging;

/// <summary>
/// Built-in <see cref="ICollectiveScope"/> payload for tenant-scoped
/// collective mutations. The dispatcher receives an event carrying
/// this scope, the resolver registry looks up
/// <see cref="Perspectives.TenantCollectiveScopeResolver"/> by
/// <see cref="ScopeKind"/> = <c>"tenant"</c>, and the resolver composes
/// the <c>row.Scope.TenantId == tenantId</c> filter around the
/// perspective's mutation spec.
/// </summary>
/// <param name="TenantId">
/// The tenant identifier the collective mutation is restricted to.
/// Matched against <see cref="Lenses.PerspectiveScope.TenantId"/> on
/// each candidate perspective row.
/// </param>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/TenantCollectiveScopeResolverTests.cs:TenantCollectiveScope_ScopeKind_IsTenantAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/TenantCollectiveScopeResolverTests.cs:TenantCollectiveScope_CarriesTenantIdAsync</tests>
public sealed record TenantCollectiveScope(string TenantId) : CollectiveScope {
  /// <summary>
  /// Routing key for the tenant scope resolver. Stable across versions —
  /// renaming this string is a breaking change.
  /// </summary>
  public override string ScopeKind => "tenant";
}
