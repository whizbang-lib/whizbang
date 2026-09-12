using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

/// <summary>
/// Turns an equality over a JSON member into a containment test, working on what Entity Framework
/// already translated.
/// </summary>
/// <remarks>
/// <para>
/// The advantage of this position over the LINQ tree is that a value converter has already been
/// applied to both sides of the comparison. An identifier held in a value object arrives as the
/// identifier; a number configured to store as text arrives as that text. Either way the document is
/// built in exactly the stored form, with nothing to convert by hand and no type mapping to invent,
/// which is what the equivalent rewrite on the tree could not manage.
/// </para>
/// <para>
/// It follows that there is no per-type dispatch here at all: whatever Entity Framework produced for
/// the comparison is what the document is built from. The single exception is a short exclusion list
/// for the date and time family, whose stored rendering differs from PostgreSQL's own; see
/// <see cref="JsonbContainmentSql.StoredFormNeedsRendering"/>.
/// </para>
/// <para>
/// <strong>Only a predicate is reshaped.</strong> At this level that is structural rather than
/// inferred: a select's predicate, a join's predicate and a having clause are filters, and a
/// projection is not. Inside a filter an absent key excludes the row whether it reads as null from an
/// extraction or as false from a containment test; in a projection that difference becomes the value
/// the caller sees.
/// </para>
/// <para>
/// No reflection. Every decision is a type test the compiler resolves.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/ContainmentSqlRewriterTests.cs</tests>
[SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
  Justification = "Reshaping a translated query means working with the expressions Entity Framework " +
    "produced, and a SelectExpression's predicate is where a filter exists at this stage. The " +
    "alternative is the LINQ tree, which cannot see a value converter's effect and was tried first.")]
internal sealed class ContainmentSqlRewriter : ExpressionVisitor {
  private readonly Func<JsonScalarExpression, bool> _standsDown;

  /// <summary>Creates a rewriter that consults the caller about members it should leave alone.</summary>
  /// <param name="standsDown">
  /// Answers whether a member has an index of its own, in which case rewriting would send the planner
  /// to the document index and leave that one unused.
  /// </param>
  internal ContainmentSqlRewriter(Func<JsonScalarExpression, bool> standsDown) => _standsDown = standsDown;

  /// <inheritdoc/>
  protected override Expression VisitExtension(Expression node) {
    ArgumentNullException.ThrowIfNull(node);

    // The outermost node refuses to be visited generically, and rightly: its shaper is client-side
    // code rather than SQL. Only the query half is ours.
    if (node is ShapedQueryExpression shaped) {
      return shaped.Update(Visit(shaped.QueryExpression), shaped.ShaperExpression);
    }

    if (node is not SelectExpression select) {
      return base.VisitExtension(node);
    }

    // Children first, so a nested select is reshaped before the outer one is rebuilt around it.
    var visited = (SelectExpression)base.VisitExtension(select);

    var predicate = visited.Predicate is null ? null : _reshape(visited.Predicate);
    var having = visited.Having is null ? null : _reshape(visited.Having);

    if (ReferenceEquals(predicate, visited.Predicate) && ReferenceEquals(having, visited.Having)) {
      return visited;
    }

    return visited.Update(
      visited.Tables,
      predicate,
      visited.GroupBy,
      having,
      visited.Projection,
      visited.Orderings,
      visited.Offset,
      visited.Limit);
  }

  /// <summary>
  /// Reshapes the equalities within one boolean expression, descending through the connectives.
  /// </summary>
  /// <remarks>
  /// Descends through <c>AND</c> and <c>OR</c> because each operand is itself a filter, and stops at
  /// a negation because that is where the two forms stop agreeing: in a positive position both an
  /// extraction and a containment test exclude a row whose key is absent, while under <c>NOT</c> the
  /// extraction's null still excludes and the containment test's false includes.
  /// </remarks>
  private SqlExpression _reshape(SqlExpression expression) {
    if (expression is SqlUnaryExpression { OperatorType: ExpressionType.Not }) {
      return expression;
    }

    if (expression is not SqlBinaryExpression binary) {
      return expression;
    }

    if (binary.OperatorType is ExpressionType.AndAlso or ExpressionType.OrElse) {
      var left = _reshape(binary.Left);
      var right = _reshape(binary.Right);

      return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
        ? binary
        : new SqlBinaryExpression(binary.OperatorType, left, right, binary.Type, binary.TypeMapping);
    }

    if (binary.OperatorType != ExpressionType.Equal) {
      return expression;
    }

    return _asContainment(binary.Left, binary.Right)
        ?? _asContainment(binary.Right, binary.Left)
        ?? expression;
  }

  /// <summary>
  /// The containment test for one equality, or null when this one is to be left alone.
  /// </summary>
  private SqlExpression? _asContainment(SqlExpression candidateMember, SqlExpression candidateValue) {
    if (candidateMember is not JsonScalarExpression member) {
      return null;
    }

    // A comparison against null is the one equality containment cannot reproduce: an extraction of an
    // absent key is null and excludes the row, while containment of an explicit JSON null does not
    // match a key that is absent at all.
    if (candidateValue is SqlConstantExpression { Value: null }) {
      return null;
    }

    // The other side has to be a value rather than a second column, or the document would be built
    // from something that varies per row.
    if (!_isValue(candidateValue)) {
      return null;
    }

    // A date's stored rendering is the serializer's and not PostgreSQL's, so a document built from a
    // timestamp would compare a different string.
    if (JsonbContainmentSql.StoredFormNeedsRendering(candidateValue.Type)
        || JsonbContainmentSql.StoredFormNeedsRendering(member.Type)) {
      return null;
    }

    if (_standsDown(member)) {
      return null;
    }

    return JsonbContainmentSql.TryBuild(member, candidateValue);
  }

  /// <summary>
  /// Whether this operand is a value the document can be built from, rather than something read per
  /// row.
  /// </summary>
  private static bool _isValue(SqlExpression expression) =>
    expression is SqlConstantExpression or SqlParameterExpression;
}
