using System.Linq.Expressions;

namespace Whizbang.Transports.HotChocolate.QueryTranslation;

/// <summary>
/// Expression visitor that removes OrderBy, OrderByDescending, ThenBy, and ThenByDescending
/// method calls from an expression tree.
/// </summary>
/// <remarks>
/// <para>
/// This visitor is used to strip pre-existing ordering from an IQueryable before
/// HotChocolate's sorting middleware applies GraphQL-requested sorting. This ensures
/// that GraphQL sorting always takes precedence over application-level default ordering.
/// </para>
/// <para>
/// Before transformation:
/// <code>
/// source.Where(x => x.Active).OrderBy(x => x.Name).ThenByDescending(x => x.Date)
/// </code>
/// </para>
/// <para>
/// After transformation:
/// <code>
/// source.Where(x => x.Active)
/// </code>
/// </para>
/// </remarks>
/// <docs>graphql/sorting</docs>
/// <tests>Whizbang.Transports.HotChocolate.Tests/Unit/OrderByStrippingExpressionVisitorTests.cs</tests>
public class OrderByStrippingExpressionVisitor : ExpressionVisitor {
  /// <summary>
  /// Visits a method call expression and strips ordering methods.
  /// </summary>
  /// <param name="node">The method call expression to visit.</param>
  /// <returns>
  /// The source expression (with ordering stripped) if the method is an ordering method;
  /// otherwise, the visited expression with any nested ordering stripped.
  /// </returns>
  protected override Expression VisitMethodCall(MethodCallExpression node) {
    // Check if this is an ordering method on System.Linq.Queryable
    if (node.Method.DeclaringType == typeof(Queryable) && _isOrderingMethod(node.Method.Name)) {
      // The first argument is the source IQueryable - visit it to strip any nested ordering
      return Visit(node.Arguments[0]);
    }

    return base.VisitMethodCall(node);
  }

  /// <summary>
  /// Checks if a method name is an ordering method.
  /// Uses string literals for AOT compatibility (no reflection).
  /// </summary>
  private static bool _isOrderingMethod(string methodName) {
    return methodName is "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending";
  }
}
