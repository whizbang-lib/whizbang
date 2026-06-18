using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.Functions;

/// <summary>
/// Custom EF Core database functions for Whizbang JSONB operations.
/// These methods are translated to PostgreSQL operators by custom translators.
/// </summary>
/// <docs>fundamentals/security/security#principal-filtering</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/Functions/WhizbangJsonDbFunctionsTests.cs</tests>
public static class WhizbangJsonDbFunctions {
  /// <summary>
  /// Checks if the AllowedPrincipals array within a PerspectiveScope contains any of the specified values.
  /// Translates to PostgreSQL: <c>scope->'AllowedPrincipals' ?| ARRAY['value1', 'value2', ...]</c>
  /// </summary>
  /// <param name="_">The DbFunctions instance (unused, for extension method syntax).</param>
  /// <param name="scope">The PerspectiveScope JSONB column.</param>
  /// <param name="values">The principal string values to search for.</param>
  /// <returns>True if the AllowedPrincipals array contains any of the specified values.</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown if called directly in C# code. This method is only valid in EF Core LINQ queries.
  /// </exception>
  /// <example>
  /// <code>
  /// // In a LINQ query - translates to: scope->'AllowedPrincipals' ?| ARRAY['user:alice', 'group:sales']
  /// var rows = await context.Set&lt;PerspectiveRow&lt;Order&gt;&gt;()
  ///   .Where(r => EF.Functions.AllowedPrincipalsContainsAny(
  ///     r.Scope,
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
      PerspectiveScope scope,
      string[] values) {
    throw new InvalidOperationException(
      "This method is only valid in EF Core LINQ queries and cannot be called directly.");
  }

  /// <summary>
  /// Set a single top-level property on a jsonb column to a new
  /// JSON-encoded value. Translates to PostgreSQL's
  /// <c>jsonb_set(&lt;data&gt;, '{&lt;path&gt;}', &lt;jsonValue&gt;::jsonb)</c>.
  /// </summary>
  /// <param name="_">The <see cref="DbFunctions"/> instance (unused, for extension method syntax).</param>
  /// <param name="data">The jsonb column the mutation applies to. Pass the property reference itself (e.g. <c>r.Data</c>) so the rewriter can chain multiple mutations.</param>
  /// <param name="path">The TOP-LEVEL JSON property name to mutate (e.g. <c>"Status"</c>). Slice 7b's collective adapter only emits top-level paths; nested paths use the raw-SQL escape hatch.</param>
  /// <param name="jsonValue">The new value, already JSON-serialized (e.g. <c>"\"Archived\""</c> for the string <c>Archived</c>, <c>"42"</c> for the int <c>42</c>). Treated as opaque JSON text and cast to <c>jsonb</c> at the SQL site.</param>
  /// <typeparam name="TData">The CLR type of the jsonb column (typically a perspective's <c>TModel</c>).</typeparam>
  /// <returns>The mutated jsonb value as the same CLR type as <paramref name="data"/>. The runtime never executes the body — it's translated to SQL — so the return is a type-check hint for EF Core.</returns>
  /// <example>
  /// <code>
  /// // Composed by Slice 6's CollectiveSettersRewriter into:
  /// // UPDATE wh_per_job
  /// //   SET data = jsonb_set(jsonb_set(data, '{Status}', '"Archived"'::jsonb),
  /// //                        '{ArchivedAt}', '"2026-06-18T..."'::jsonb)
  /// //   WHERE …
  /// query.ExecuteUpdateAsync(s =&gt; s.SetProperty(
  ///   r =&gt; r.Data,
  ///   r =&gt; EF.Functions.JsonbSet(
  ///          EF.Functions.JsonbSet(r.Data, "Status", "\"Archived\""),
  ///          "ArchivedAt", "\"2026-06-18T...\"")));
  /// </code>
  /// </example>
  /// <exception cref="InvalidOperationException">
  /// Thrown if called directly in C# code. This method is only valid in EF Core LINQ queries.
  /// </exception>
  /// <docs>fundamentals/messaging/collective-events</docs>
  public static TData JsonbSet<TData>(
      this DbFunctions _,
      TData data,
      string path,
      string jsonValue)
      where TData : class {
    throw new InvalidOperationException(
      "This method is only valid in EF Core LINQ queries and cannot be called directly.");
  }
}
