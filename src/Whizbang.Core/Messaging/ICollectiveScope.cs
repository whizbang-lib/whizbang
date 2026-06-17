namespace Whizbang.Core.Messaging;

/// <summary>
/// Extensible scope payload carried by an <see cref="ICollectiveEvent"/>.
/// The <see cref="ScopeKind"/> string discriminator drives resolver
/// lookup at runtime — an <see cref="Perspectives.ICollectiveScopeResolver"/>
/// matching the kind owns the routing + filter composition for that
/// scope family.
/// </summary>
/// <remarks>
/// <para>
/// Whizbang ships built-in scopes (e.g. <c>"tenant"</c> via
/// <c>TenantCollectiveScopeResolver</c> in Slice 4) and consumers add
/// their own (e.g. <c>"workspace"</c>, <c>"org"</c>) by implementing
/// <see cref="ICollectiveScope"/> + a matching resolver and registering
/// the resolver in DI. The scope payload itself is just data; the
/// behavior lives on the resolver side.
/// </para>
/// <para>
/// The scope is consulted alongside Whizbang's existing
/// <c>ScopeContextAccessor</c> / <see cref="Security.PerspectiveScope"/>
/// security model — the resolver bridges between the collective scope
/// payload and the ambient <c>ScopeContext</c> at apply time.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CollectiveEventContractTests.cs:ICollectiveScope_ScopeKind_DiscriminatesResolverLookupAsync</tests>
public interface ICollectiveScope {
  /// <summary>
  /// Stable string discriminator that selects the
  /// <see cref="Perspectives.ICollectiveScopeResolver"/> handling this
  /// scope. Built-in kinds use lowercase singletons (e.g. <c>"tenant"</c>,
  /// <c>"global"</c>); consumer-defined kinds should follow the same
  /// shape to avoid collisions.
  /// </summary>
  string ScopeKind { get; }
}
