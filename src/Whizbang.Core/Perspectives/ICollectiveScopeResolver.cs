using System.Linq.Expressions;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// DI-registered handler for one scope kind. Owns the routing decision
/// ("which perspectives accept events with this scope?"), the filter
/// composition ("what WHERE clause gates the SQL UPDATE?"), and the
/// ambient-context handoff ("set <c>ScopeContextAccessor</c> for the
/// duration of the perspective's <c>Apply</c>").
/// </summary>
/// <remarks>
/// <para>
/// Whizbang ships built-in resolvers — e.g. <c>TenantCollectiveScopeResolver</c>
/// for <see cref="ICollectiveScope.ScopeKind"/> = <c>"tenant"</c> (Slice 4).
/// Consumers add new scope kinds (workspace, org, custom) by implementing
/// this interface, exposing a stable <see cref="ScopeKind"/>, and
/// registering the resolver in DI. The framework looks up the resolver
/// at apply time by matching the incoming
/// <see cref="ICollectiveEvent.Scope"/>'s <c>ScopeKind</c>.
/// </para>
/// <para>
/// The interface is intentionally narrow — these are the four hooks the
/// projection runner needs at apply time. Resolvers can hold richer
/// state internally (caches, derived materializations) as long as it's
/// invisible to the contract.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveSpecContractTests.cs:ICollectiveScopeResolver_HasScopeKindDiscriminatorAsync</tests>
public interface ICollectiveScopeResolver {
  /// <summary>
  /// The scope discriminator this resolver handles. Must match the
  /// <see cref="ICollectiveScope.ScopeKind"/> of events that should be
  /// routed here. Stable across runs — used as the DI dispatch key.
  /// </summary>
  string ScopeKind { get; }

  /// <summary>
  /// Does this resolver consider perspectives over <typeparamref name="TModel"/>
  /// in-scope for events it handles? For the built-in tenant resolver
  /// this returns <c>true</c> for any model with a tenant-aware
  /// <c>PerspectiveScope</c>; for custom scopes the resolver decides.
  /// </summary>
  bool AcceptsPerspective<TModel>() where TModel : class;

  /// <summary>
  /// Produce the WHERE clause (over <see cref="PerspectiveRow{TModel}"/>)
  /// that gates the perspective's collective SQL UPDATE. The framework
  /// composes this filter as an outer WHERE around the perspective's
  /// mutation spec (unless the handler opts out via
  /// <c>ScopeHandling = Custom</c>).
  /// </summary>
  Expression<Func<PerspectiveRow<TModel>, bool>> ScopeFilter<TModel>(ICollectiveScope scope)
    where TModel : class;

  /// <summary>
  /// Set the ambient <c>ScopeContextAccessor.CurrentContext</c> for the
  /// duration of the perspective's <c>Apply</c> call. Returns an
  /// <see cref="IDisposable"/> the caller disposes to restore the prior
  /// context. Matches Whizbang's existing AsyncLocal scope-handoff
  /// idiom.
  /// </summary>
  IDisposable EnterContext(ICollectiveScope scope);
}
