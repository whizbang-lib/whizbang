using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Data.EFCore.Postgres.Functions;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Extension methods for filtering perspective rows by security principals.
/// Uses PostgreSQL JSONB containment operator (@&gt;) with GIN index optimization.
/// </summary>
/// <docs>core-concepts/security#principal-filtering</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/PrincipalFilterExtensionsTests.cs</tests>
/// <remarks>
/// <para>
/// This extension provides principal-based row filtering using EF Core and PostgreSQL JSONB.
/// The AllowedPrincipals field in PerspectiveScope is stored as a JSONB array of strings.
/// </para>
/// <para>
/// <strong>Query Pattern:</strong> Uses OR'd @&gt; (containment) checks that PostgreSQL
/// optimizes using GIN index bitmap scans:
/// <code>
/// WHERE scope @&gt; '{"AllowedPrincipals":["user:alice"]}'
///    OR scope @&gt; '{"AllowedPrincipals":["group:sales"]}'
///    -- PostgreSQL uses bitmap index scans with GIN, combining results efficiently
/// </code>
/// </para>
/// <para>
/// <strong>Performance:</strong> A GIN index on the scope column is required for optimal
/// performance. The generated EF Core configuration includes:
/// <code>entity.HasIndex(e =&gt; e.Scope).HasMethod("GIN").HasOperators("jsonb_path_ops");</code>
/// With this index, PostgreSQL performs bitmap index scans for each OR condition and
/// combines them efficiently, making even 100+ principal checks performant.
/// </para>
/// </remarks>
public static class PrincipalFilterExtensions {
  /// <summary>
  /// Threshold for switching from OR'd containment checks to array overlap.
  /// For small sets, OR'd checks have similar performance and simpler SQL.
  /// For larger sets, array overlap is significantly more efficient.
  /// </summary>
  private const int ARRAY_OVERLAP_THRESHOLD = 10;
  /// <summary>
  /// Filters perspective rows where AllowedPrincipals contains any of the caller's principals.
  /// Uses PostgreSQL's ?| (array overlap) operator for efficient filtering with GIN index.
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
  /// Uses OR'd @&gt; containment checks for all principal counts.
  /// The GIN index with jsonb_path_ops enables efficient bitmap index scans,
  /// making even 100+ principal checks performant via bitmap OR operations.
  /// For large principal sets (&gt; 10), the query is tagged for diagnostics.
  /// </remarks>
  public static IQueryable<PerspectiveRow<TModel>> FilterByPrincipals<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      IReadOnlySet<SecurityPrincipalId> callerPrincipals)
      where TModel : class {

    if (callerPrincipals == null || callerPrincipals.Count == 0) {
      // No principals = no access (return empty result)
      return query.Where(r => false);
    }

    // For small sets, use OR'd containment checks (simpler SQL, similar performance)
    // For larger sets, use array overlap operator (much more efficient)
    if (callerPrincipals.Count <= ARRAY_OVERLAP_THRESHOLD) {
      return _filterByPrincipalsContainment(query, callerPrincipals);
    }

    // Use PostgreSQL array overlap: scope->'AllowedPrincipals' ?| ARRAY[...]
    // This generates a single efficient index scan instead of N OR'd conditions
    return _filterByPrincipalsArrayOverlap(query, callerPrincipals);
  }

  /// <summary>
  /// Internal method using OR'd containment checks for small principal sets.
  /// </summary>
  private static IQueryable<PerspectiveRow<TModel>> _filterByPrincipalsContainment<TModel>(
      IQueryable<PerspectiveRow<TModel>> query,
      IReadOnlySet<SecurityPrincipalId> callerPrincipals)
      where TModel : class {

    // Build OR expression: (AllowedPrincipals contains P1) OR (AllowedPrincipals contains P2) ...
    // Each containment check uses EF.Functions.JsonContains which translates to @>
    Expression<Func<PerspectiveRow<TModel>, bool>>? combinedPredicate = null;

    foreach (var principal in callerPrincipals) {
      // Create JSON containment check using the full Scope object
      // This translates to: scope @> '{"AllowedPrincipals": ["user:alice"]}'
      // SecurityPrincipalId values are simple strings (user:xxx, group:xxx, svc:xxx)
      // without special characters that need JSON escaping
      var containmentJson = _buildContainmentJson(principal.Value);

      Expression<Func<PerspectiveRow<TModel>, bool>> predicate = r =>
        EF.Functions.JsonContains(r.Scope, containmentJson);

      combinedPredicate = combinedPredicate == null
        ? predicate
        : _combineOr(combinedPredicate, predicate);
    }

    return query.Where(combinedPredicate!);
  }

  /// <summary>
  /// Internal method for large principal sets. Uses PostgreSQL's ?| array overlap operator.
  /// </summary>
  /// <remarks>
  /// Uses a custom EF Core function translator that maps to PostgreSQL's ?| operator:
  /// <code>
  /// -- Single efficient query with array overlap
  /// WHERE scope->'AllowedPrincipals' ?| ARRAY['user:alice', 'group:sales', ...]
  /// </code>
  /// This is much more efficient than N OR'd containment checks for large principal sets.
  /// Requires <see cref="WhizbangDbContextOptionsExtensions.UseWhizbangFunctions"/> to be
  /// called during DbContext configuration.
  /// </remarks>
  private static IQueryable<PerspectiveRow<TModel>> _filterByPrincipalsArrayOverlap<TModel>(
      IQueryable<PerspectiveRow<TModel>> query,
      IReadOnlySet<SecurityPrincipalId> callerPrincipals)
      where TModel : class {

    // Convert principals to string array for the ?| operator
    var principalValues = callerPrincipals.Select(p => p.Value).ToArray();

    // Use custom AllowedPrincipalsContainsAny function which translates to:
    // scope->'AllowedPrincipals' ?| ARRAY['user:alice', 'group:sales', ...]
    return query
      .Where(r => EF.Functions.AllowedPrincipalsContainsAny(r.Scope, principalValues))
      .TagWith("PrincipalFilter:ArrayOverlap");
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
  /// Uses the same optimization as <see cref="FilterByPrincipals{TModel}"/>:
  /// GIN index with jsonb_path_ops enables efficient bitmap index scans for OR'd conditions.
  /// </remarks>
  public static IQueryable<PerspectiveRow<TModel>> FilterByUserOrPrincipals<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      string? userId,
      IReadOnlySet<SecurityPrincipalId> callerPrincipals)
      where TModel : class {

    // Build: (UserId = currentUser) OR (AllowedPrincipals contains any caller principal)
    Expression<Func<PerspectiveRow<TModel>, bool>>? combinedPredicate = null;

    // Add user match predicate if userId is provided
    if (!string.IsNullOrEmpty(userId)) {
      Expression<Func<PerspectiveRow<TModel>, bool>> userPredicate = r =>
        r.Scope.UserId == userId;
      combinedPredicate = userPredicate;
    }

    // Add principal predicates using GIN-optimized containment checks
    if (callerPrincipals != null && callerPrincipals.Count > 0) {
      foreach (var principal in callerPrincipals) {
        // Create JSON containment check using the full Scope object
        // GIN index with jsonb_path_ops enables efficient bitmap index scans
        var containmentJson = _buildContainmentJson(principal.Value);

        Expression<Func<PerspectiveRow<TModel>, bool>> predicate = r =>
          EF.Functions.JsonContains(r.Scope, containmentJson);

        combinedPredicate = combinedPredicate == null
          ? predicate
          : _combineOr(combinedPredicate, predicate);
      }
    }

    // If no predicates, return empty result
    if (combinedPredicate == null) {
      return query.Where(r => false);
    }

    // Tag query for large principal sets
    var result = query.Where(combinedPredicate);
    if (callerPrincipals != null && callerPrincipals.Count > ARRAY_OVERLAP_THRESHOLD) {
      result = result.TagWith("PrincipalFilter:UserOrPrincipals:LargeSet");
    }
    return result;
  }

  /// <summary>
  /// Builds a JSON containment string for AllowedPrincipals check.
  /// AOT compatible - no reflection or dynamic code generation.
  /// </summary>
  /// <param name="principalValue">The principal value (e.g., "user:alice")</param>
  /// <returns>JSON string like {"AllowedPrincipals":["user:alice"]}</returns>
  private static string _buildContainmentJson(string principalValue) {
    // SecurityPrincipalId values follow a simple pattern: type:identifier
    // e.g., "user:alice", "group:sales-team", "svc:api-gateway"
    // These don't contain JSON-special characters (quotes, backslashes, etc.)
    // If the pattern ever changes, this would need proper JSON escaping
    return $"{{\"AllowedPrincipals\":[\"{principalValue}\"]}}";
  }

  /// <summary>
  /// Combines two predicate expressions using OR logic.
  /// </summary>
  private static Expression<Func<T, bool>> _combineOr<T>(
      Expression<Func<T, bool>> left,
      Expression<Func<T, bool>> right) {

    var parameter = Expression.Parameter(typeof(T), "r");

    // Replace parameter in both expressions
    var leftBody = new ParameterReplacer(left.Parameters[0], parameter).Visit(left.Body);
    var rightBody = new ParameterReplacer(right.Parameters[0], parameter).Visit(right.Body);

    // Combine with OR
    var body = Expression.OrElse(leftBody, rightBody);

    return Expression.Lambda<Func<T, bool>>(body, parameter);
  }

  /// <summary>
  /// Expression visitor that replaces one parameter with another.
  /// </summary>
  private sealed class ParameterReplacer : ExpressionVisitor {
    private readonly ParameterExpression _oldParameter;
    private readonly ParameterExpression _newParameter;

    public ParameterReplacer(ParameterExpression oldParameter, ParameterExpression newParameter) {
      _oldParameter = oldParameter;
      _newParameter = newParameter;
    }

    protected override Expression VisitParameter(ParameterExpression node) {
      return node == _oldParameter ? _newParameter : base.VisitParameter(node);
    }
  }
}
