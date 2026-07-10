using System.Linq.Expressions;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives.Hooks;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// Resolves the collective apply hooks that match a model and folds their recorded op lists into a single
/// driver-neutral <see cref="CollectiveApplyHookPlan{TModel}"/>. Runs once per apply (the registry memoizes the
/// per-model producer list), off the SQL-compile hot path.
/// </summary>
/// <remarks>
/// Fold rules:
/// <list type="bullet">
///   <item><description><c>SetColumn</c> is de-duplicated last-wins by column name (Postgres forbids assigning
///     a column twice in one UPDATE); first-seen order is preserved.</description></item>
///   <item><description><c>BumpVersion</c> wins over any explicit <c>version</c> column write — they would
///     otherwise collide on the same column.</description></item>
///   <item><description><c>AndWhere</c>/<c>ReplaceWhere</c> predicates (written over the model marker) are
///     lifted onto <c>PerspectiveRow&lt;TModel&gt;.Data</c> so the driver adapters can compile them. A later
///     <c>ReplaceWhere</c> wins.</description></item>
///   <item><description><c>RemoveSetter</c> names are carried through; the adapter applies them to the combined
///     spec + hook model-field setter list.</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/messaging/apply-hooks</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs:Hook_AndWhere_RefinesTheCohortAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs:Hook_ReplaceWhere_SwapsCohortButScopeStillBindsAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveApplierIntegrationTests.cs:Hook_SetColumn_OverridesDefaultUpdatedAt_AndSkipsVersionBumpAsync</tests>
public static class CollectiveApplyHookPlanner {
  /// <summary>Resolve and fold the matching collective hooks for <typeparamref name="TModel"/>.</summary>
  /// <param name="registry">The collective hook registry (already seeded with framework defaults).</param>
  /// <param name="context">The per-apply context (model type, event, scope, timestamp).</param>
  /// <typeparam name="TModel">The perspective model the collective event mutates.</typeparam>
  /// <returns>The folded plan the driver adapter renders into the set-based UPDATE.</returns>
  public static CollectiveApplyHookPlan<TModel> Resolve<TModel>(
      CollectiveApplyHookRegistry registry, ApplyHookContext context) where TModel : class {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(context);

    var modelSetters = new List<SetPropertyOp>();
    var removed = new HashSet<string>(StringComparer.Ordinal);
    var columnValues = new Dictionary<string, object?>(StringComparer.Ordinal);
    var columnOrder = new List<string>();
    var bump = false;
    var andWheres = new List<Expression<Func<PerspectiveRow<TModel>, bool>>>();
    Expression<Func<PerspectiveRow<TModel>, bool>>? replaceWhere = null;

    foreach (var produce in registry.ResolveFor(typeof(TModel))) {
      foreach (var op in produce(context)) {
        switch (op) {
          case SetPropertyOp setProperty:
            modelSetters.Add(setProperty);
            break;
          case RemoveSetterOp remove:
            removed.Add(remove.PropertyName);
            break;
          case SetColumnOp column:
            if (!columnValues.ContainsKey(column.Column)) {
              columnOrder.Add(column.Column);
            }
            columnValues[column.Column] = column.Value; // last-wins
            break;
          case BumpVersionOp:
            bump = true;
            break;
          case AndWhereOp andWhere:
            andWheres.Add(_lift<TModel>(andWhere.Predicate));
            break;
          case ReplaceWhereOp replace:
            replaceWhere = _lift<TModel>(replace.Predicate);
            break;
          default:
            break;
        }
      }
    }

    if (bump) {
      // A version bump and an explicit version column write would set the same column twice — the bump wins.
      columnValues.Remove(ApplyHookColumns.VERSION);
      columnOrder.Remove(ApplyHookColumns.VERSION);
    }

    var storeColumns = columnOrder
      .Select(c => new CollectiveStoreColumn(c, columnValues[c]))
      .ToArray();

    return new CollectiveApplyHookPlan<TModel>(modelSetters, removed, storeColumns, bump, andWheres, replaceWhere);
  }

  /// <summary>
  /// Lift a hook predicate written over the model marker (<c>m =&gt; …</c>) onto the perspective row
  /// (<c>r =&gt; …</c> over <c>r.Data</c>). Because the real model is assignable to the marker, replacing the
  /// marker parameter node with the <c>r.Data</c> access keeps every member access valid. The <c>r.Data</c>
  /// access is taken from a compile-time selector (<c>r =&gt; r.Data</c>) rather than a by-name
  /// <c>Expression.Property</c> lookup, so there is no runtime member reflection — trim/AOT-clean.
  /// </summary>
  private static Expression<Func<PerspectiveRow<TModel>, bool>> _lift<TModel>(LambdaExpression markerPredicate)
      where TModel : class {
    var markerParam = markerPredicate.Parameters[0];
    // r => r.Data — the compiler bakes the Data PropertyInfo into the tree; no Expression.Property(string) reflection.
    Expression<Func<PerspectiveRow<TModel>, TModel>> dataSelector = r => r.Data;
    var rowParam = dataSelector.Parameters[0];
    var body = new _parameterReplacer(markerParam, dataSelector.Body).Visit(markerPredicate.Body);
    return Expression.Lambda<Func<PerspectiveRow<TModel>, bool>>(body, rowParam);
  }

  private sealed class _parameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor {
    protected override Expression VisitParameter(ParameterExpression node) =>
      ReferenceEquals(node, from) ? to : base.VisitParameter(node);
  }
}
