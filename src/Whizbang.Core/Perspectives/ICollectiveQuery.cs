using System.Diagnostics.CodeAnalysis;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The query context a collective-apply handler receives so its
/// <see cref="ICollectiveSpec{TModel}.Where"/> can reference <em>sibling</em> perspectives — rows in a
/// projection table other than the one being mutated. Each driver binds this to its own querying primitive;
/// the handler writes one driver-neutral expression and each driver projects it.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets a cohort span two read models. A consumer service's <c>OrderModel</c> carries no status
/// (it lives on the sibling <c>OrderStatusModel</c>, keyed by the same id), so "apply this template to
/// every Draft/Approved/Published order" is expressed against the status sibling:
/// </para>
/// <code>
/// [CollectiveApplyFor]
/// public ICollectiveSpec&lt;OrderModel&gt; ApplyTemplate(TemplateAppliedCollectiveEvent e, ICollectiveQuery q) =&gt;
///   new CollectiveSpec&lt;OrderModel&gt;(
///     Setters: s =&gt; s.SetProperty(o =&gt; o.TemplateId, e.TemplateId),
///     Where:   r =&gt; q.Of&lt;OrderStatusModel&gt;()
///                    .Any(st =&gt; st.Id == r.Id &amp;&amp; Eligible.Contains(st.Data.Status)));
/// </code>
/// <para>
/// <strong>Translation.</strong> The <c>Where</c> closes over the query context; each driver translates
/// the resulting <see cref="System.Linq.IQueryable{T}"/> sub-expression into a correlated
/// <c>EXISTS</c> subquery:
/// </para>
/// <list type="bullet">
///   <item><description><strong>EF Core</strong> — <see cref="Of{TOther}"/> returns the live
///     <c>DbContext.Set&lt;PerspectiveRow&lt;TOther&gt;&gt;()</c>; EF funcletizes the <c>q.Of()</c> call and
///     translates <c>.Any(correlated)</c> to <c>EXISTS</c>.</description></item>
///   <item><description><strong>Dapper</strong> — <see cref="Of{TOther}"/> returns a marker queryable
///     carrying <c>TOther</c>'s table name; the Dapper filter compiler emits the <c>EXISTS (SELECT 1 …)</c>
///     SQL directly.</description></item>
/// </list>
/// <para>
/// The handler never sees driver types — only this interface and <see cref="System.Linq"/> operators the
/// drivers know how to translate (currently <c>.Any(...)</c> with an equality correlation and
/// <c>Contains</c>/equality leaf predicates; richer shapes throw a clear <c>NotSupportedException</c>).
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
public interface ICollectiveQuery {
  /// <summary>
  /// A queryable over the sibling perspective's rows. Use inside a <c>Where</c> with a correlated
  /// <c>.Any(...)</c> to scope the mutated table by a condition on the sibling table (same id).
  /// </summary>
  /// <typeparam name="TOther">The sibling read model whose perspective table the cohort references.</typeparam>
  [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
    Justification = "Of<T>() is the intended fluent query surface (q.Of<StatusModel>().Any(...)); this is an internal framework collective-apply API not targeted at VB consumers, and the reserved-word collision is acceptable for the readability of the cohort expression.")]
  IQueryable<PerspectiveRow<TOther>> Of<TOther>() where TOther : class;
}
