using System.Linq.Expressions;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives.Hooks;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// One physical store-column assignment a collective apply hook contributed (e.g. <c>updated_at</c> or a
/// developer audit column). Rendered as <c>"Column" = @param</c> in the set-based UPDATE, on both drivers.
/// </summary>
/// <param name="Column">The physical column name.</param>
/// <param name="Value">The value to bind (may be <c>null</c>).</param>
public sealed record CollectiveStoreColumn(string Column, object? Value) {
  /// <summary>
  /// Defense-in-depth: an apply-hook store-column name is developer-authored (not user input), but it is
  /// interpolated into the raw UPDATE, so validate it as an unquoted Postgres identifier before quoting.
  /// </summary>
  public static bool IsValidIdentifier(string? column) {
    if (string.IsNullOrEmpty(column) || column.Length > 63) {
      return false;
    }
    if (!(char.IsAsciiLetter(column[0]) || column[0] == '_')) {
      return false;
    }
    for (var i = 1; i < column.Length; i++) {
      if (!(char.IsAsciiLetterOrDigit(column[i]) || column[i] == '_')) {
        return false;
      }
    }
    return true;
  }
}

/// <summary>
/// The driver-neutral fold of every collective apply hook that matched a model, resolved once per apply. It
/// separates the recorded <see cref="ApplyHookOp"/>s into the four things a set-based UPDATE needs: extra model
/// (jsonb) field setters, model-field setters to drop, physical store-column assignments (+ a version bump), and
/// cohort-<c>WHERE</c> modifiers already rebased onto <c>PerspectiveRow&lt;TModel&gt;</c>. Both the EF Core and
/// Dapper adapters consume this same plan.
/// </summary>
/// <typeparam name="TModel">The perspective model the collective event mutates.</typeparam>
/// <docs>fundamentals/messaging/apply-hooks</docs>
public sealed record CollectiveApplyHookPlan<TModel>(
  IReadOnlyList<SetPropertyOp> ModelFieldSetters,
  IReadOnlySet<string> RemovedModelFields,
  IReadOnlyList<CollectiveStoreColumn> StoreColumns,
  bool BumpVersion,
  IReadOnlyList<Expression<Func<PerspectiveRow<TModel>, bool>>> AndWheres,
  Expression<Func<PerspectiveRow<TModel>, bool>>? ReplaceWhere) where TModel : class {

  /// <summary>
  /// Fold the hook cohort-<c>WHERE</c> modifiers onto the handler's spec <paramref name="specWhere"/>: a
  /// <see cref="ReplaceWhere"/> swaps the cohort entirely, then every <see cref="AndWheres"/> refines it with
  /// <c>AND</c>. The framework still composes the mandatory scope envelope on top afterwards (D0). Returns the
  /// composed cohort predicate, or <c>null</c> when neither the spec nor any hook supplied one.
  /// </summary>
  /// <param name="specWhere">The handler spec's own cohort predicate (may be <c>null</c>).</param>
  public Expression<Func<PerspectiveRow<TModel>, bool>>? ComposeCohort(
      Expression<Func<PerspectiveRow<TModel>, bool>>? specWhere) {
    var cohort = ReplaceWhere ?? specWhere;
    foreach (var andWhere in AndWheres) {
      cohort = cohort is null ? andWhere : _andAlso(cohort, andWhere);
    }
    return cohort;
  }

  private static Expression<Func<PerspectiveRow<TModel>, bool>> _andAlso(
      Expression<Func<PerspectiveRow<TModel>, bool>> left,
      Expression<Func<PerspectiveRow<TModel>, bool>> right) {
    var parameter = left.Parameters[0];
    var reboundRight = new _parameterReplacer(right.Parameters[0], parameter).Visit(right.Body);
    return Expression.Lambda<Func<PerspectiveRow<TModel>, bool>>(
      Expression.AndAlso(left.Body, reboundRight), parameter);
  }

  private sealed class _parameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor {
    protected override Expression VisitParameter(ParameterExpression node) =>
      ReferenceEquals(node, from) ? to : base.VisitParameter(node);
  }
}
