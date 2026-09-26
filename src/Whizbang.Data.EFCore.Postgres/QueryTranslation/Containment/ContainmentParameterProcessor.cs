using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

/// <summary>
/// Corrects a set-membership containment test whose candidates turn out to include a null, at the
/// one point in the pipeline where the candidates are known.
/// </summary>
/// <remarks>
/// <para>
/// A membership filter is compiled into a containment test against one single-key document per
/// candidate. A candidate that is null builds a document holding an explicit JSON null, and
/// containment of an explicit null does not match a row whose key is absent, while the membership
/// test it replaced does. So the rewrite loses rows, and only for the candidate lists that contain
/// a null.
/// </para>
/// <para>
/// Whether a null is among them is not a question about the nullability of the parameter itself,
/// which is the only question the SQL cache keys on. Asking anything more costs the cached SQL for
/// that query, so the cost is confined to the filters that can actually be wrong: the candidates
/// are examined only when the element type can hold a null, which is the reference-typed sets, and
/// a query holding none of those never reaches the parameter dictionary at all and caches exactly
/// as before. Identifier and number sets cannot carry a null and are left untouched.
/// </para>
/// <para>
/// The correction is an added null test rather than a rebuilt filter. The document built for the
/// null candidate matches only rows that carry an explicit null, which the null test matches too,
/// so nothing has to be removed from the candidates to keep the answer exact.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/JsonbContainmentSqlMatrixTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/GinContainmentIntegrationTests.cs:AMembershipFilterIncludingANullCandidate_StillMatchesTheRowsWhoseKeyIsAbsentAsync</tests>
[SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
  Justification = "Correcting a translated filter means working with the expressions Entity Framework " +
    "produced, and the parameter-based processor is the only stage that sees both the SQL and the " +
    "values it will run with.")]
public class ContainmentParameterProcessor(
    RelationalParameterBasedSqlProcessorDependencies dependencies,
    RelationalParameterBasedSqlProcessorParameters parameters)
  : NpgsqlParameterBasedSqlProcessor(dependencies, parameters) {

  /// <inheritdoc/>
  public override Expression Process(Expression queryExpression, ParametersCacheDecorator parametersDecorator) {
    ArgumentNullException.ThrowIfNull(parametersDecorator);

    var processed = base.Process(queryExpression, parametersDecorator);

    if (!JsonbContainmentSwitch.Enabled || !ProviderCapabilities.ContainmentRewriteRequired) {
      return processed;
    }

    // Looked for before anything is asked about the values: a query with no nullable-element set
    // filter must not pay the cached SQL for a correction it cannot need.
    var finder = new NullableCandidateSetFinder();
    finder.Visit(processed);

    return finder.Found ? new NullCandidateGuard(parametersDecorator).Visit(processed)! : processed;
  }

  /// <summary>The marker a set-membership containment test compiles to, or null when this is not one.</summary>
  /// <param name="node">The expression to inspect.</param>
  /// <returns>The column, the document key and the candidate parameter, or null.</returns>
  private static (ColumnExpression Column, string Key, SqlParameterExpression Candidates)? _asSetMembership(Expression node) =>
    node is PgUnknownBinaryExpression { Operator: "@> ANY" } binary
      && binary.Left is ColumnExpression column
      && binary.Right is SqlFunctionExpression { Name: "", Arguments: [SqlFunctionExpression documents] }
      && string.Equals(documents.Name, "jsonb_containment_set", StringComparison.Ordinal)
      && documents.Arguments is [SqlConstantExpression { Value: string key }, SqlParameterExpression candidates]
      && !candidates.Type.IsValueType
        ? (column, key, candidates)
        : null;

  /// <summary>Whether a candidate list that could hold a null appears anywhere in the query.</summary>
  private sealed class NullableCandidateSetFinder : ExpressionVisitor {
    public bool Found { get; private set; }

    public override Expression? Visit(Expression? node) {
      if (node is not null && _asSetMembership(node) is not null) {
        Found = true;
      }

      return base.Visit(node);
    }
  }

  /// <summary>Adds the null test to every membership filter whose candidates include a null.</summary>
  private sealed class NullCandidateGuard(ParametersCacheDecorator decorator) : ExpressionVisitor {
    private Dictionary<string, object?>? _values;

    public override Expression? Visit(Expression? node) {
      if (node is null || _asSetMembership(node) is not { } membership) {
        return base.Visit(node);
      }

      // The first membership filter is what makes the values worth their cost, so the dictionary is
      // taken here rather than up front.
      _values ??= decorator.GetAndDisableCaching();

      return _holdsNull(membership.Candidates.Name) ? _guarded(node, membership.Column, membership.Key) : node;
    }

    private bool _holdsNull(string parameterName) =>
      _values!.TryGetValue(parameterName, out var value)
      && value is IEnumerable candidates and not string
      && candidates.Cast<object?>().Any(candidate => candidate is null);

    /// <summary>The membership test, or the row simply not carrying the key at all.</summary>
    private static SqlBinaryExpression _guarded(Expression membership, ColumnExpression column, string key) =>
      new(
        ExpressionType.OrElse,
        (SqlExpression)membership,
        new SqlUnaryExpression(
          ExpressionType.Equal,
          new JsonScalarExpression(
            column, [new PathSegment(key)], typeof(string), StringTypeMapping.Default, nullable: true),
          typeof(bool),
          BoolTypeMapping.Default),
        typeof(bool),
        BoolTypeMapping.Default);
  }
}

/// <summary>Supplies <see cref="ContainmentParameterProcessor"/> to the query pipeline.</summary>
/// <param name="dependencies">The processor's dependencies.</param>
[SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
  Justification = "Replacing a provider service is how a correction reaches the stage that owns it.")]
public class ContainmentParameterProcessorFactory(RelationalParameterBasedSqlProcessorDependencies dependencies)
  : IRelationalParameterBasedSqlProcessorFactory {

  /// <inheritdoc/>
  public RelationalParameterBasedSqlProcessor Create(RelationalParameterBasedSqlProcessorParameters parameters)
    => new ContainmentParameterProcessor(dependencies, parameters);
}
