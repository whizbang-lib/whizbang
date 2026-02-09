using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Extension methods for filtering perspective rows by security principals.
/// Uses EF Core 10 ComplexProperty().ToJson() with native LINQ support.
/// </summary>
/// <docs>core-concepts/security#principal-filtering</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/PrincipalFilterExtensionsTests.cs</tests>
/// <remarks>
/// <para>
/// This extension provides principal-based row filtering using EF Core's native
/// LINQ support for ComplexProperty().ToJson() columns. AllowedPrincipals is a
/// List&lt;string&gt; within the Scope complex property.
/// </para>
/// <para>
/// <strong>Query Pattern:</strong> Uses standard LINQ Any/Contains which EF Core 10
/// translates to efficient PostgreSQL JSONB queries.
/// </para>
/// <para>
/// <strong>Performance:</strong> A GIN index on the scope column is required for optimal
/// performance. EF Core translates LINQ collection queries to native PostgreSQL JSONB operations.
/// </para>
/// </remarks>
public static class PrincipalFilterExtensions {
  /// <summary>
  /// Filters perspective rows where AllowedPrincipals contains any of the caller's principals.
  /// Uses EF Core 10's native LINQ support for ComplexProperty().ToJson().
  /// </summary>
  /// <typeparam name="TModel">The perspective model type.</typeparam>
  /// <param name="query">The queryable to filter.</param>
  /// <param name="callerPrincipals">The security principals of the caller.</param>
  /// <returns>Filtered queryable with principal matching.</returns>
  /// <example>
  /// <code>
  /// var callerPrincipals = new HashSet&lt;SecurityPrincipalId&gt; {
  ///   SecurityPrincipalId.User("alice"),
  ///   SecurityPrincipalId.Group("sales-team")
  /// };
  ///
  /// var accessibleRows = await context.Set&lt;PerspectiveRow&lt;Order&gt;&gt;()
  ///   .FilterByPrincipals(callerPrincipals)
  ///   .ToListAsync();
  /// </code>
  /// </example>
  /// <remarks>
  /// Uses LINQ Any() on AllowedPrincipals collection, which EF Core 10 translates
  /// to efficient PostgreSQL JSONB queries. A GIN index on the scope column
  /// provides optimal query performance.
  /// </remarks>
  public static IQueryable<PerspectiveRow<TModel>> FilterByPrincipals<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      IReadOnlySet<SecurityPrincipalId> callerPrincipals)
      where TModel : class {

    if (callerPrincipals == null || callerPrincipals.Count == 0) {
      // No principals = no access (return empty result)
      return query.Where(r => false);
    }

    // Convert principals to string list for LINQ Contains
    var principalValues = callerPrincipals.Select(p => p.Value).ToList();

    // Use EF Core 10's native LINQ support for ComplexProperty().ToJson()
    // This translates to efficient PostgreSQL JSONB array operations
    return query.Where(r => r.Scope.AllowedPrincipals.Any(p => principalValues.Contains(p)));
  }

  /// <summary>
  /// Filters perspective rows where the user owns the row (UserId matches)
  /// OR the row is shared with any of the caller's principals.
  /// This implements the "my records or shared with me" pattern.
  /// </summary>
  /// <typeparam name="TModel">The perspective model type.</typeparam>
  /// <param name="query">The queryable to filter.</param>
  /// <param name="userId">The current user's ID.</param>
  /// <param name="callerPrincipals">The security principals of the caller.</param>
  /// <returns>Filtered queryable with user OR principal matching.</returns>
  /// <example>
  /// <code>
  /// var accessibleRows = await context.Set&lt;PerspectiveRow&lt;Order&gt;&gt;()
  ///   .FilterByUserOrPrincipals("user-123", callerPrincipals)
  ///   .ToListAsync();
  /// </code>
  /// </example>
  /// <remarks>
  /// Uses EF Core 10's native LINQ support for ComplexProperty().ToJson().
  /// Combines user ownership check with principal matching in a single OR expression.
  /// </remarks>
  public static IQueryable<PerspectiveRow<TModel>> FilterByUserOrPrincipals<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      string? userId,
      IReadOnlySet<SecurityPrincipalId> callerPrincipals)
      where TModel : class {

    // Convert principals to string list for LINQ Contains
    var principalValues = callerPrincipals?.Select(p => p.Value).ToList() ?? [];

    // Handle empty inputs
    var hasUserId = !string.IsNullOrEmpty(userId);
    var hasPrincipals = principalValues.Count > 0;

    if (!hasUserId && !hasPrincipals) {
      // No filter criteria = no access (return empty result)
      return query.Where(r => false);
    }

    // Build OR predicate: (UserId = currentUser) OR (AllowedPrincipals contains any caller principal)
    if (hasUserId && hasPrincipals) {
      return query.Where(r =>
        r.Scope.UserId == userId ||
        r.Scope.AllowedPrincipals.Any(p => principalValues.Contains(p)));
    } else if (hasUserId) {
      return query.Where(r => r.Scope.UserId == userId);
    } else {
      return query.Where(r => r.Scope.AllowedPrincipals.Any(p => principalValues.Contains(p)));
    }
  }
}
