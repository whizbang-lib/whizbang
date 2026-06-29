using System.Linq.Expressions;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// ORM-neutral description of the mutation a collective-events
/// perspective wants applied to its projection. The spec carries a
/// LINQ expression tree (<see cref="Setters"/>) that driver-specific
/// adapters translate to their native execution shape: EF Core's
/// <c>ExecuteUpdateAsync</c> with <c>SetPropertyCalls</c> on the EF
/// adapter, <c>jsonb_set</c> SQL on the Dapper adapter.
/// </summary>
/// <remarks>
/// <para>
/// The spec describes the MUTATION ONLY. The WHERE clause that gates
/// the SQL UPDATE is composed by the framework from three sources:
/// </para>
/// <list type="number">
///   <item><description><see cref="Whizbang.Core.Messaging.ICollectiveEvent.Scope"/> → resolver's <c>ScopeFilter</c> (outer scope envelope)</description></item>
///   <item><description><see cref="Whizbang.Core.Messaging.ICollectiveEvent.MatchedStreamIds"/> → id-membership clause (<c>id = ANY(...)</c>)</description></item>
///   <item><description>The perspective's spec → set clauses (this property)</description></item>
/// </list>
/// <para>
/// Perspectives opting into <c>ScopeHandling = Custom</c> via the
/// <see cref="CollectiveApplyForAttribute"/> take over WHERE composition
/// themselves — see the attribute's docs.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveSpecContractTests.cs:ICollectiveSpec_Setters_IsLinqExpressionTreeAsync</tests>
public interface ICollectiveSpec<TModel> where TModel : class {
  /// <summary>
  /// The LINQ expression tree describing the set of property assignments
  /// the SQL UPDATE should perform. Adapters walk this with
  /// <see cref="ExpressionVisitor"/> and emit driver-specific SQL — no
  /// runtime reflection, AOT-clean by construction.
  /// </summary>
  Expression<Action<ICollectiveSetters<TModel>>> Setters { get; }

  /// <summary>
  /// Optional per-model WHERE projection. When non-null, the handler — which knows its own model — shapes
  /// the cohort onto its own columns (e.g. <c>r =&gt; statuses.Contains(r.Data.Status)</c>), letting the
  /// SAME persisted collective event project differently across models and across services.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Composition is governed by the handler's <see cref="CollectiveScopeHandling"/> (see
  /// <see cref="CollectiveWhereComposer"/>):
  /// </para>
  /// <list type="bullet">
  ///   <item><description><see cref="CollectiveScopeHandling.Framework"/> (default): the resolver's scope
  ///     filter is AND-ed with this <c>Where</c> — the scope envelope still binds, the handler refines.</description></item>
  ///   <item><description><see cref="CollectiveScopeHandling.Custom"/>: this <c>Where</c> is the entire
  ///     predicate — the handler owns the WHERE, and a null here is a misconfiguration.</description></item>
  /// </list>
  /// <para>
  /// Defaults to <c>null</c> (a default interface member) so the common "set columns, framework scopes by
  /// tenant" handler stays a one-liner and existing Setters-only specs are unaffected.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/messaging/collective-events</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveWhereComposerTests.cs:Framework_WithHandlerWhere_AndsScopeAndHandlerAsync</tests>
  Expression<Func<PerspectiveRow<TModel>, bool>>? Where => null;
}
