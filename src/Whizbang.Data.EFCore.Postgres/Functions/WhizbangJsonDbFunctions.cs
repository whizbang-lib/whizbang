using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.Functions;

/// <summary>
/// Custom EF Core database functions for Whizbang JSONB operations.
/// These methods are translated to PostgreSQL operators by custom translators.
/// </summary>
/// <docs>fundamentals/security/security#principal-filtering</docs>
public static class WhizbangJsonDbFunctions {
  /// <summary>
  /// Checks whether a scope's allowed-principals array overlaps any of the specified values.
  /// Translates to PostgreSQL: <c>scope->'ap' ?| ARRAY['value1', 'value2', ...]</c>
  /// </summary>
  /// <param name="_">The DbFunctions instance (unused, for extension method syntax).</param>
  /// <param name="allowedPrincipals">The scope's allowed-principals member, e.g. <c>row.Scope.AllowedPrincipals</c>.</param>
  /// <param name="values">The principal string values to search for.</param>
  /// <returns>True if the AllowedPrincipals array contains any of the specified values.</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown if called directly in C# code. This method is only valid in EF Core LINQ queries.
  /// </exception>
  /// <example>
  /// <code>
  /// // In a LINQ query - translates to: scope->'ap' ?| ARRAY['user:alice', 'group:sales']
  /// var rows = await context.Set&lt;PerspectiveRow&lt;Order&gt;&gt;()
  ///   .Where(r => EF.Functions.AllowedPrincipalsContainsAny(
  ///     r.Scope.AllowedPrincipals,
  ///     new[] { "user:alice", "group:sales" }))
  ///   .ToListAsync();
  /// </code>
  /// </example>
  /// <remarks>
  /// This function requires a GIN index on the scope column for optimal performance:
  /// <code>
  /// entity.HasIndex(e => e.Scope).HasMethod("GIN").HasOperators("jsonb_path_ops");
  /// </code>
  /// </remarks>
  public static bool AllowedPrincipalsContainsAny(
      this DbFunctions _,
      List<string> allowedPrincipals,
      string[] values) {
    throw new InvalidOperationException(
      "This method is only valid in EF Core LINQ queries and cannot be called directly.");
  }
}
