using HotChocolate.Resolvers;
using Whizbang.Transports.HotChocolate.QueryTranslation;

namespace Whizbang.Transports.HotChocolate.Middleware;

/// <summary>
/// Middleware that strips pre-existing OrderBy from IQueryable expressions before
/// HotChocolate's sorting middleware applies GraphQL-requested sorting.
/// </summary>
/// <remarks>
/// <para>
/// This middleware ensures GraphQL sorting takes precedence over application-level
/// default ordering. When a GraphQL query includes sorting arguments, any pre-existing
/// OrderBy/OrderByDescending in the IQueryable expression tree is removed.
/// </para>
/// <para>
/// This middleware should be applied AFTER [UseSorting] in attribute order (closer to resolver)
/// to ensure the ordering is stripped before HotChocolate's sorting middleware processes it.
/// </para>
/// </remarks>
/// <docs>graphql/sorting</docs>
/// <tests>Whizbang.Transports.HotChocolate.Tests/Integration/QueryExecutionTests.cs</tests>
public static class OrderByStrippingMiddleware {
  private static readonly OrderByStrippingExpressionVisitor _visitor = new();

  /// <summary>
  /// Creates a middleware delegate that strips pre-existing ordering.
  /// </summary>
  public static FieldMiddleware Create() {
    return next => async context => {
      await next(context).ConfigureAwait(false);

      // Only process if sorting arguments are present and result is IQueryable
      if (!_hasSortingArguments(context)) {
        return;
      }

      var result = context.Result;
      if (result is null) {
        return;
      }

      // Strip ordering from IQueryable
      var strippedQueryable = _stripOrderingFromQueryable(result);
      if (strippedQueryable is not null) {
        context.Result = strippedQueryable;
      }
    };
  }

  /// <summary>
  /// Checks if the field has sorting arguments in the request.
  /// </summary>
  private static bool _hasSortingArguments(IMiddlewareContext context) {
    // Check for the 'order' argument which is HotChocolate's default sorting argument name
    // ArgumentValue returns the deserialized value, null if not provided
    try {
      var orderArg = context.ArgumentValue<object?>("order");
      return orderArg is not null;
    } catch {
      // Argument doesn't exist on this field
      return false;
    }
  }

  /// <summary>
  /// Strips ordering from an IQueryable by visiting its expression tree.
  /// Uses pattern matching (no reflection) for AOT compatibility.
  /// </summary>
  private static object? _stripOrderingFromQueryable(object result) {
    // Check if result is IQueryable
    if (result is not IQueryable queryable) {
      return null;
    }

    // Strip ordering from the expression tree
    var strippedExpression = _visitor.Visit(queryable.Expression);

    // If nothing changed, return null to indicate no modification needed
    if (ReferenceEquals(strippedExpression, queryable.Expression)) {
      return null;
    }

    // Create a new queryable with the stripped expression
    return queryable.Provider.CreateQuery(strippedExpression);
  }
}
