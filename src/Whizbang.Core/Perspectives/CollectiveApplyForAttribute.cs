namespace Whizbang.Core.Perspectives;

/// <summary>
/// How the framework composes the SQL UPDATE's WHERE clause for a
/// collective-event handler.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public enum CollectiveScopeHandling {
  /// <summary>
  /// Safe default. The framework wraps the perspective's mutation spec
  /// with the resolver's <see cref="ICollectiveScopeResolver.ScopeFilter{TModel}"/>
  /// outer WHERE plus the event's <see cref="Messaging.ICollectiveEvent.MatchedStreamIds"/>
  /// id-membership clause. The perspective only writes the SET clauses;
  /// it can't accidentally over-mutate.
  /// </summary>
  Framework,

  /// <summary>
  /// Opt-out. The framework calls
  /// <see cref="ICollectiveScopeResolver.EnterContext"/> to set the
  /// ambient <c>ScopeContextAccessor</c>, then invokes the perspective's
  /// <c>Apply</c>. The perspective reads the context and writes the
  /// full WHERE itself. Use when the scope filter is non-trivial
  /// (multi-field, joined, conditional) — and accept the
  /// burden of including the scope clause correctly.
  /// </summary>
  Custom,
}

/// <summary>
/// What execution shape the perspective's <see cref="ICollectiveSpec{TModel}"/>
/// uses. Generators (Slice 5) read this at compile time to route to the
/// right adapter; runtime never reflects on it.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public enum CollectiveSpecKind {
  /// <summary>
  /// Default. The spec carries a LINQ expression tree
  /// (<see cref="ICollectiveSpec{TModel}.Setters"/>) that the EF or
  /// Dapper adapter walks with <see cref="System.Linq.Expressions.ExpressionVisitor"/>.
  /// Type-safe, AOT-friendly, covers the bulk of mutation shapes.
  /// </summary>
  Linq,

  /// <summary>
  /// Escape hatch for cases the LINQ surface can't express — nested
  /// jsonb merges, cross-row aggregates, CTEs, etc. The spec carries
  /// a raw SQL fragment + parameter bag the adapter executes directly.
  /// </summary>
  RawSql,
}

/// <summary>
/// Marks a method as a collective-event handler. Read by the source
/// generator (Slice 5) at compile time; emitted into the perspective
/// runner's static dispatch table. Reflection-free at runtime —
/// AOT-clean by construction.
/// </summary>
/// <remarks>
/// <para>
/// Attach to methods whose signature matches
/// <see cref="ICollectiveApplyFor{TModel, TEvent}.Apply"/> — the
/// generator infers <c>TModel</c> and <c>TEvent</c> from the method's
/// return type and parameter.
/// </para>
/// <para>
/// Configure handler behavior via the two enum properties:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="ScopeHandling"/> — how the WHERE clause is composed.</description></item>
///   <item><description><see cref="SpecKind"/> — which adapter pipeline executes the spec.</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Default: framework composes scope filter, LINQ spec.
/// [CollectiveApplyFor]
/// public ICollectiveSpec&lt;JobModel&gt; ArchiveJobs(ArchiveJobsCollectiveEvent e) =&gt;
///   Update&lt;JobModel&gt;(s =&gt; s.SetProperty(j =&gt; j.Status, "Archived"));
///
/// // Opt-out for custom scope handling:
/// [CollectiveApplyFor(ScopeHandling = CollectiveScopeHandling.Custom)]
/// public ICollectiveSpec&lt;JobModel&gt; ComplexUpdate(ComplexCollectiveEvent e) {
///   var ctx = ScopeContextAccessor.CurrentContext;
///   return /* full WHERE including scope */;
/// }
///
/// // Raw-SQL escape hatch for jsonb-heavy mutations:
/// [CollectiveApplyFor(SpecKind = CollectiveSpecKind.RawSql)]
/// public ICollectiveSpec&lt;JobModel&gt; JsonbMerge(JsonbHeavyEvent e) =&gt;
///   new RawSqlCollectiveSpec("data = jsonb_set(data, '{X}', ...)", ...);
/// </code>
/// </example>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveSpecContractTests.cs:CollectiveApplyForAttribute_CarriesScopeHandlingDefaultAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveSpecContractTests.cs:CollectiveApplyForAttribute_AcceptsExplicitScopeHandlingCustomAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveSpecContractTests.cs:CollectiveApplyForAttribute_AcceptsExplicitSpecKindRawSqlAsync</tests>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CollectiveApplyForAttribute : Attribute {
  /// <summary>
  /// How the framework composes the WHERE clause for this handler.
  /// Default <see cref="CollectiveScopeHandling.Framework"/>.
  /// </summary>
  public CollectiveScopeHandling ScopeHandling { get; init; } = CollectiveScopeHandling.Framework;

  /// <summary>
  /// Which adapter pipeline executes the returned spec. Default
  /// <see cref="CollectiveSpecKind.Linq"/>.
  /// </summary>
  public CollectiveSpecKind SpecKind { get; init; } = CollectiveSpecKind.Linq;
}
