using System.Linq.Expressions;

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
}
