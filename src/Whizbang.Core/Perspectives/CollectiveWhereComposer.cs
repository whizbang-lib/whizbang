using System.Linq.Expressions;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Composes the final <c>WHERE</c> predicate for a collective-event apply from the resolver's scope filter
/// and the handler's own per-model <see cref="ICollectiveSpec{TModel}.Where"/> projection, according to the
/// handler's <see cref="CollectiveScopeHandling"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that lets the SAME persisted collective event be projected differently by each
/// perspective that consumes it — across models and across services. The resolver supplies a model-agnostic
/// scope envelope (e.g. the tenant clause); the handler — which knows its own model — supplies a
/// <c>Where</c> that refines or replaces it onto its own columns.
/// </para>
/// <list type="bullet">
///   <item><description><see cref="CollectiveScopeHandling.Framework"/> with no handler <c>Where</c>:
///     the resolver scope filter alone (verbatim — same reference).</description></item>
///   <item><description><see cref="CollectiveScopeHandling.Framework"/> with a handler <c>Where</c>:
///     <c>scopeFilter AND handlerWhere</c>. The scope envelope always binds, so a handler can refine but
///     never over-mutate beyond its scope.</description></item>
///   <item><description><see cref="CollectiveScopeHandling.Custom"/> with a handler <c>Where</c>:
///     the handler <c>Where</c> alone — the handler owns the full predicate (including any scoping it
///     wants).</description></item>
///   <item><description><see cref="CollectiveScopeHandling.Custom"/> with no handler <c>Where</c>:
///     a misconfiguration — throws. Custom means the handler owns the WHERE; supplying none would update
///     every row.</description></item>
/// </list>
/// <para>
/// Pure expression construction — no reflection, AOT-clean. The two predicates are combined with
/// <see cref="Expression.AndAlso(Expression, Expression)"/> after rebinding the second lambda's parameter
/// onto the first's, so the result is a single inspectable tree the EF/Dapper adapters translate.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveWhereComposerTests.cs:Framework_WithHandlerWhere_AndsScopeAndHandlerAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveWhereComposerTests.cs:Custom_WithHandlerWhere_UsesHandlerWhereAloneAsync</tests>
public static class CollectiveWhereComposer {
  /// <summary>
  /// Build the effective collective-update WHERE for a handler. See the type remarks for the composition
  /// matrix per <see cref="CollectiveScopeHandling"/>.
  /// </summary>
  /// <param name="scopeHandling">How the handler opted to compose its WHERE.</param>
  /// <param name="scopeFilter">The resolver's scope predicate for <typeparamref name="TModel"/>.</param>
  /// <param name="handlerWhere">The handler's per-model projection, or <c>null</c> when it supplied none.</param>
  /// <typeparam name="TModel">The perspective model the collective event mutates.</typeparam>
  /// <returns>The single predicate the driver adapter composes into the UPDATE's WHERE.</returns>
  public static Expression<Func<PerspectiveRow<TModel>, bool>> Compose<TModel>(
      CollectiveScopeHandling scopeHandling,
      Expression<Func<PerspectiveRow<TModel>, bool>>? scopeFilter,
      Expression<Func<PerspectiveRow<TModel>, bool>>? handlerWhere)
      where TModel : class {

    switch (scopeHandling) {
      case CollectiveScopeHandling.Framework:
        // The scope envelope is mandatory on the framework path — it's what prevents over-mutation.
        ArgumentNullException.ThrowIfNull(scopeFilter);
        return handlerWhere is null ? scopeFilter : _and(scopeFilter, handlerWhere);

      case CollectiveScopeHandling.Custom:
        // Custom ignores the resolver scope filter entirely — the applier need not even compute one.
        return handlerWhere ?? throw new InvalidOperationException(
          "CollectiveScopeHandling.Custom requires the handler's spec to supply a Where predicate — the " +
          "handler owns the full WHERE. Spec.Where was null, which would update every row in the table. " +
          "Either return a Where from the spec or use ScopeHandling.Framework.");

      default:
        throw new ArgumentOutOfRangeException(
          nameof(scopeHandling), scopeHandling, "Unknown collective scope handling.");
    }
  }

  private static Expression<Func<PerspectiveRow<TModel>, bool>> _and<TModel>(
      Expression<Func<PerspectiveRow<TModel>, bool>> left,
      Expression<Func<PerspectiveRow<TModel>, bool>> right)
      where TModel : class {
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
