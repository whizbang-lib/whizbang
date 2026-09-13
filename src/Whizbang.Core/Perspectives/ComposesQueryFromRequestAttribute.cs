namespace Whizbang.Core.Perspectives;

/// <summary>
/// Marks an attribute as one that lets a request shape the query, so the framework can see an
/// exposure it did not write.
/// </summary>
/// <remarks>
/// <para>
/// Put this on <em>your own attribute</em>, not on a query. An integration that composes sorting,
/// filtering or an expression from a request is making a claim the build cannot otherwise check:
/// that any field of the model may reach an <c>ORDER BY</c> or a <c>WHERE</c>. Marking the attribute
/// says so once, and every method carrying it is then covered.
/// </para>
/// <para>
/// <strong>Why a marker rather than a list of known names.</strong> The framework ships two such
/// surfaces and cannot know about the rest. A domain language that builds arbitrary expressions over
/// any queryable is the widest exposure there is, and it would be invisible to a hardcoded list
/// while being exactly the case worth reporting. Anything that can reference this assembly can opt
/// in without the framework being changed or released.
/// </para>
/// <para>
/// For an attribute this assembly cannot reach, a third party's own, list its fully qualified name
/// in an analyzer configuration option instead. The marker is the better path when it is available,
/// because it travels with the package rather than with each consumer's configuration.
/// </para>
/// <para>
/// <strong>What it does not say.</strong> Only that a query is composed from the request, never
/// which fields become reachable. That is deliberate: an integration rarely knows, and a surface
/// offering "sort by any column" genuinely means any. The diagnostic therefore reports the model
/// and what is unaccounted for, and leaves the choice of which fields to index to the author.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <tests>tests/Whizbang.Generators.Tests/QueryExposureAnalyzerTests.cs</tests>
/// <example>
/// <code>
/// // A domain language that turns a request string into a LINQ expression over any queryable.
/// [ComposesQueryFromRequest(QueryExposures.Expression)]
/// [AttributeUsage(AttributeTargets.Method)]
/// public sealed class UseExpressionAttribute : Attribute { }
/// </code>
/// </example>
/// <param name="exposure">
/// What a request can shape. Defaults to ordering and filtering together, which is what a sorting
/// or filtering middleware offers; say <see cref="QueryExposures.Expression"/> for a surface that
/// builds arbitrary predicates.
/// </param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ComposesQueryFromRequestAttribute(
    QueryExposures exposure = QueryExposures.Ordering | QueryExposures.Filtering) : Attribute {
  /// <summary>What a request can shape through the marked attribute.</summary>
  public QueryExposures Exposure { get; } = exposure;
}
