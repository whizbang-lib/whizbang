using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

/// <summary>
/// A deliberately crude reshape of a translated query, to settle whether reshaping one is possible at
/// all before anything is built on the idea.
/// </summary>
/// <remarks>
/// <para>
/// Two claims have to hold before a containment test is worth compiling here rather than on the
/// expression tree. A predicate reached after translation has to be replaceable, which is in doubt
/// because a <see cref="SelectExpression"/> is close to immutable by design. And every node has to
/// carry a type mapping by this point, which is the whole reason to prefer this position: the earlier
/// attempt on the expression tree failed because a converted value reached the SQL tree unmapped and
/// Entity Framework refused the query.
/// </para>
/// <para>
/// This marks the predicates it rewrites instead of producing containment, so a failure means the
/// mechanism does not work rather than that the containment shape is wrong. Those are different
/// problems and only the first is fatal. Nothing here is shipped: it exists to be read alongside its
/// test and then replaced by the real reshape.
/// </para>
/// <para>
/// No reflection anywhere. The visitor dispatches on expression types the compiler knows, and the
/// service replacement is a generic registration.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/ContainmentPostprocessorSpikeTests.cs</tests>
public static class ContainmentPostprocessorSpike {
  /// <summary>
  /// Written into every predicate the spike rewrites, so the rewrite is visible in the compiled SQL.
  /// </summary>
  /// <remarks>
  /// A call to a function that does not exist. That sounds worse than it is: the gate only reads the
  /// compiled text and never executes it, and an unknown function is the one thing no optimizer will
  /// fold away. A comparison of a constant with itself was tried first and disappeared, which is
  /// itself worth knowing: the pipeline folds a tautology after the reshape, so a marker has to be
  /// something it cannot reason about.
  /// </remarks>
  public const string MARKER = "whizbang_reshape_spike()";

  /// <summary>
  /// Whether every node the reshape looked at carried a type mapping.
  /// </summary>
  /// <remarks>
  /// Starts true and is only ever cleared, so a test can assert it after compiling a query. Static
  /// because the spike is a measurement rather than a component; the real reshape reports nothing.
  /// </remarks>
  public static bool EveryVisitedNodeHadAMapping { get; private set; } = true;

  /// <summary>
  /// How many predicates the reshape has actually replaced.
  /// </summary>
  /// <remarks>
  /// Without this the other two observations are vacuous: a flag that starts true and an assertion
  /// that nothing was thrown would both hold if the postprocessor never ran, which is the first thing
  /// to rule out rather than the last.
  /// </remarks>
  public static int PredicatesReshaped { get; private set; }

  /// <summary>Clears what the last reshape observed.</summary>
  public static void Reset() {
    EveryVisitedNodeHadAMapping = true;
    PredicatesReshaped = 0;
  }

  internal static void NoteReshape() => PredicatesReshaped++;

  internal static void NoteMissingMapping() => EveryVisitedNodeHadAMapping = false;
}

/// <summary>
/// Supplies the spike's postprocessor in place of the provider's own.
/// </summary>
/// <docs>contributors/perspective-query-pipeline</docs>
public sealed class ContainmentPostprocessorSpikeFactory(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies)
    : IQueryTranslationPostprocessorFactory {
  /// <inheritdoc/>
  public QueryTranslationPostprocessor Create(QueryCompilationContext queryCompilationContext) =>
    new SpikeQueryTranslationPostprocessor(
        dependencies,
        relationalDependencies,
        (RelationalQueryCompilationContext)queryCompilationContext);
}

/// <summary>
/// Runs the provider's own postprocessing and then reshapes what it produced.
/// </summary>
/// <remarks>
/// The ordering carries the whole argument. The base pass is where type mappings are inferred and
/// assigned, so a visitor that runs afterwards sees a fully mapped tree, which is the thing the
/// expression-tree approach could not arrange for a converted value.
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
public sealed class SpikeQueryTranslationPostprocessor(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies,
    RelationalQueryCompilationContext queryCompilationContext)
    : RelationalQueryTranslationPostprocessor(dependencies, relationalDependencies, queryCompilationContext) {
  /// <inheritdoc/>
  public override Expression Process(Expression query) {
    ArgumentNullException.ThrowIfNull(query);

    var translated = base.Process(query);

    ContainmentPostprocessorSpike.Reset();
    return new SpikePredicateRewriter().Visit(translated);
  }
}

/// <summary>
/// Marks every predicate it can reach, which is the only thing the spike is measuring.
/// </summary>
/// <docs>contributors/perspective-query-pipeline</docs>
[SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
  Justification = "Reshaping a translated query is the point of the spike, and a SelectExpression's " +
    "predicate is the only place a filter exists at this stage. Whether it can be replaced from here " +
    "at all is exactly the question being settled, so there is nothing to be gained by asking it " +
    "through a narrower surface.")]
internal sealed class SpikePredicateRewriter : ExpressionVisitor {
  /// <inheritdoc/>
  protected override Expression VisitExtension(Expression node) {
    // The outermost node refuses to be visited generically and says so: its shaper is client-side
    // code rather than SQL, so descending into it blindly is meaningless. Only the query half is
    // ours to reshape.
    if (node is ShapedQueryExpression shaped) {
      return shaped.Update(Visit(shaped.QueryExpression), shaped.ShaperExpression);
    }

    if (node is not SelectExpression select) {
      return base.VisitExtension(node);
    }

    // Visit the children first, so a nested select is reshaped too and the outer update sees the
    // reshaped inner one.
    var visited = (SelectExpression)base.VisitExtension(select);
    if (visited.Predicate is null) {
      return visited;
    }

    _noteMappings(visited.Predicate);
    ContainmentPostprocessorSpike.NoteReshape();

    // ANDed onto the predicate purely so it shows up in the compiled text. Not foldable, which a
    // constant comparison turned out to be.
    var marker = new SqlFunctionExpression(
        "whizbang_reshape_spike",
        arguments: [],
        nullable: false,
        argumentsPropagateNullability: [],
        typeof(bool),
        BoolTypeMapping.Default);

    var combined = new SqlBinaryExpression(
        ExpressionType.AndAlso, visited.Predicate, marker, typeof(bool), BoolTypeMapping.Default);

    return visited.Update(
        visited.Tables,
        combined,
        visited.GroupBy,
        visited.Having,
        visited.Projection,
        visited.Orderings,
        visited.Offset,
        visited.Limit);
  }

  /// <summary>
  /// Records whether the nodes of a predicate carry type mappings, which is the second gate.
  /// </summary>
  private static void _noteMappings(SqlExpression expression) {
    if (expression.TypeMapping is null) {
      ContainmentPostprocessorSpike.NoteMissingMapping();
    }

    if (expression is SqlBinaryExpression binary) {
      _noteMappings(binary.Left);
      _noteMappings(binary.Right);
    }
  }
}

/// <summary>
/// Registers the spike's postprocessor on a context, for its tests only.
/// </summary>
/// <docs>contributors/perspective-query-pipeline</docs>
public static class ContainmentPostprocessorSpikeExtensions {
  /// <summary>Replaces the query translation postprocessor with the spike's.</summary>
  /// <param name="builder">The options being built.</param>
  /// <returns>The same builder.</returns>
  public static DbContextOptionsBuilder UseWhizbangTranslatedTreeContainmentSpike(
      this DbContextOptionsBuilder builder) {
    ArgumentNullException.ThrowIfNull(builder);

    return builder
      .ReplaceService<IQueryTranslationPostprocessorFactory, ContainmentPostprocessorSpikeFactory>();
  }

  /// <summary>Replaces the query translation postprocessor with the spike's.</summary>
  /// <typeparam name="TContext">The context being configured.</typeparam>
  /// <param name="builder">The options being built.</param>
  /// <returns>The same builder, so its typed Options remain reachable.</returns>
  public static DbContextOptionsBuilder<TContext> UseWhizbangTranslatedTreeContainmentSpike<TContext>(
      this DbContextOptionsBuilder<TContext> builder)
      where TContext : DbContext {
    ArgumentNullException.ThrowIfNull(builder);

    return builder
      .ReplaceService<IQueryTranslationPostprocessorFactory, ContainmentPostprocessorSpikeFactory>();
  }
}
