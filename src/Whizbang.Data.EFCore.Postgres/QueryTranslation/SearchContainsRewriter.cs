using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Functions;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Rewrites <c>Contains</c> on a field declared <c>[Indexed(IndexKinds.Search)]</c> to the folded search its
/// index is built over.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#search</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/SearchQueryShapeTests.cs</tests>
/// <remarks>
/// <para>
/// Runs before the promoted-field redirect, while the property is still a plain member access whose owner
/// says which model it belongs to. That holds both for <c>r.Data.Title</c> and for a query that selected the
/// model first (<c>rows.Select(r =&gt; r.Data).Where(m =&gt; m.Title.Contains(t))</c>), since the registry is
/// keyed by the model type either way.
/// </para>
/// <para>
/// Only the one-argument <c>Contains</c>, and only on a declared field: folding changes what <c>Contains</c>
/// means (it ignores case and quote style), so it is applied where the field asked for it and nowhere else.
/// </para>
/// </remarks>
public sealed class SearchContainsRewriter : ExpressionVisitor {
  private static readonly MethodInfo _contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

  private static readonly MethodInfo _foldedContains = typeof(WhizbangSearchDbFunctions).GetMethod(
    nameof(WhizbangSearchDbFunctions.FoldedContains), [typeof(DbFunctions), typeof(string), typeof(string)])!;

  // A constant, because that is the form EF.Functions has in a query by the time interceptors see it: the
  // funcletizer has already evaluated it. The raw static-property access is something no translator reads.
  private static readonly Expression _functions = Expression.Constant(EF.Functions, typeof(DbFunctions));

  /// <inheritdoc />
  protected override Expression VisitMethodCall(MethodCallExpression node) {
    ArgumentNullException.ThrowIfNull(node);
    if (node.Method == _contains
        && node.Object is MemberExpression { Member: PropertyInfo property, Expression: { } owner } member
        && (JsonIndexRegistry.Kinds(owner.Type, property.Name) & IndexKinds.Search) != 0) {
      return Expression.Call(_foldedContains, _functions, Visit(member), Visit(node.Arguments[0]));
    }
    return base.VisitMethodCall(node);
  }
}
