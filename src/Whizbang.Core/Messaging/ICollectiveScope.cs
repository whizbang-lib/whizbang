// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

  /// <summary>
  /// Everything that distinguishes one instance of this scope from another, as a stable string.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Used where two applies must be told apart rather than merely routed: the advisory lock a
  /// collective apply takes is keyed by table and by this, so two scopes with the same identity
  /// serialize against each other and two with different identities do not.
  /// </para>
  /// <para>
  /// It must therefore include every field that makes the scope narrower than its kind -- a tenant
  /// id, a region, a customer. The kind alone is right only for a scope that has no instance data at
  /// all, such as a global one, which is why that is the default. Returning the kind from a scope
  /// that does carry data collapses every instance onto one lock: every tenant's collective applies
  /// then serialize against each other, one at a time, per table.
  /// </para>
  /// </remarks>
  string ScopeIdentity => ScopeKind;
}
