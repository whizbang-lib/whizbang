using System.Collections.Generic;
using System.Linq.Expressions;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Security;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// First baked-in <see cref="ICollectiveScopeResolver"/>: handles
/// <see cref="TenantCollectiveScope"/> events. Routes every tenant-scoped
/// collective event onto the corresponding perspective's mutation spec,
/// gated by a <c>row.Scope.TenantId == scope.TenantId</c> filter.
/// </summary>
/// <remarks>
/// <para>
/// Register in DI as a singleton (the resolver is stateless), keyed by
/// <see cref="ScopeKind"/> = <c>"tenant"</c>. The collective-events
/// runtime looks up the resolver at apply time by matching the incoming
/// event's <see cref="ICollectiveScope.ScopeKind"/>.
/// </para>
/// <para>
/// Consumers extend the scope model by implementing their own
/// <see cref="ICollectiveScope"/> + <see cref="ICollectiveScopeResolver"/>
/// for a different <c>ScopeKind</c> (e.g. <c>"workspace"</c>,
/// <c>"org"</c>) and registering them alongside this one.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/TenantCollectiveScopeResolverTests.cs:Resolver_ScopeKind_MatchesScopeAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/TenantCollectiveScopeResolverTests.cs:Resolver_AcceptsPerspective_AllowsAnyModelAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/TenantCollectiveScopeResolverTests.cs:ScopeFilter_CompiledExpression_MatchesRowsByTenantIdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/TenantCollectiveScopeResolverTests.cs:EnterContext_SetsAmbientScopeContextWithTenantIdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/TenantCollectiveScopeResolverTests.cs:EnterContext_DisposeRestoresPriorContextAsync</tests>
public sealed class TenantCollectiveScopeResolver : ICollectiveScopeResolver {
  /// <inheritdoc />
  public string ScopeKind => "tenant";

  /// <inheritdoc />
  /// <remarks>
  /// All <see cref="PerspectiveRow{TModel}"/> rows carry a
  /// <see cref="PerspectiveScope"/> column, so tenant filtering applies
  /// regardless of <typeparamref name="TModel"/>. Per-model opt-out is a
  /// future concern; the first cut accepts every perspective.
  /// </remarks>
  public bool AcceptsPerspective<TModel>() where TModel : class => true;

  /// <inheritdoc />
  /// <remarks>
  /// The expression compiles to <c>row =&gt; row.Scope.TenantId == @tenantId</c>
  /// where <c>@tenantId</c> is closure-captured from the
  /// <see cref="TenantCollectiveScope"/> payload. The driver adapter
  /// (Slice 6 EF, Slice 9 Dapper) composes this as the OUTER WHERE
  /// around the perspective's mutation spec.
  /// </remarks>
  public Expression<Func<PerspectiveRow<TModel>, bool>> ScopeFilter<TModel>(ICollectiveScope scope)
    where TModel : class {
    var tenantScope = _requireTenantScope(scope);
    var tenantId = tenantScope.TenantId;
    return row => row.Scope.TenantId == tenantId;
  }

  /// <inheritdoc />
  /// <remarks>
  /// Sets <see cref="ScopeContextAccessor.CurrentContext"/> to a
  /// minimal <see cref="ScopeContext"/> exposing the tenant id at
  /// <c>Scope.TenantId</c> so perspectives in
  /// <c>[CollectiveApplyFor(ScopeHandling = Custom)]</c> mode can
  /// consult the ambient tenant when building their own WHERE clause.
  /// The returned <see cref="IDisposable"/> restores the prior context
  /// (re-entrance safety).
  /// </remarks>
  public IDisposable EnterContext(ICollectiveScope scope) {
    var tenantScope = _requireTenantScope(scope);
    var prior = ScopeContextAccessor.CurrentContext;
    ScopeContextAccessor.CurrentContext = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = tenantScope.TenantId },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
    };
    return new _scopeRestore(prior);
  }

  private static TenantCollectiveScope _requireTenantScope(ICollectiveScope scope) {
    if (scope is TenantCollectiveScope tenantScope) {
      return tenantScope;
    }
    throw new ArgumentException(
      $"TenantCollectiveScopeResolver only handles TenantCollectiveScope payloads (ScopeKind='tenant'); got {scope?.GetType().FullName ?? "null"} (ScopeKind='{scope?.ScopeKind ?? "null"}'). " +
      "This indicates a misconfigured resolver registration — the resolver registry should dispatch each scope kind to a matching resolver.",
      nameof(scope));
  }

  private sealed class _scopeRestore(IScopeContext? prior) : IDisposable {
    public void Dispose() => ScopeContextAccessor.CurrentContext = prior;
  }
}
