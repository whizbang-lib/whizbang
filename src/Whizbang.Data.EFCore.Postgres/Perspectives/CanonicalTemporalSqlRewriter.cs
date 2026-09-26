using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Reads a temporal out of a stored document as the number it is, rather than as the timestamp its
/// CLR type suggests.
/// </summary>
/// <remarks>
/// <para>
/// A document a perspective stores as one serialized value carries no mapping for the members
/// inside it, so a member read in a query is translated from its CLR type alone and a date becomes
/// a cast of the extracted text to a timestamp. The stored form is the canonical count of
/// microseconds, which PostgreSQL will not read as an instant, so the statement is refused outright
/// rather than answering differently.
/// </para>
/// <para>
/// The fix is to cast to the number instead. Ordering needs nothing further, because the canonical
/// unit counts forward and so the number's order is the instant's order. A comparison needs its
/// bound in the same unit, which is a conversion of a value already in hand for a literal, and an
/// arithmetic one in the database for a parameter. Both are decided from the shape of the query
/// alone, so the SQL still caches.
/// </para>
/// <para>
/// Only a temporal extracted from a document is touched. A temporal in a column of its own is typed
/// for what it holds and is read correctly already.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OpaqueDocumentRoundTripTests.cs:OrderingAndFilteringOnATemporalInsideTheDocumentAnswerAsync</tests>
[SuppressMessage("Usage", "EF1001:Internal EF Core API usage",
  Justification = "Correcting how a translated query reads a stored value means working with the " +
    "expressions Entity Framework produced; there is no public expression type for a cast.")]
internal sealed class CanonicalTemporalSqlRewriter(RelationalTypeMapping microseconds) : ExpressionVisitor {
  private const long MICROSECONDS_PER_SECOND = 1_000_000;

  /// <inheritdoc/>
  protected override Expression VisitExtension(Expression node) {
    ArgumentNullException.ThrowIfNull(node);

    // The outermost node refuses to be visited generically, and rightly: its shaper is client-side
    // code rather than SQL. Only the query half is ours.
    if (node is ShapedQueryExpression shaped) {
      return shaped.Update(Visit(shaped.QueryExpression), shaped.ShaperExpression);
    }

    // A comparison is visited as a whole, because correcting one side without the other would
    // compare a count of microseconds with an instant.
    if (node is SqlBinaryExpression binary && _isComparison(binary.OperatorType)) {
      var left = _asStoredNumber(binary.Left);
      var right = _asStoredNumber(binary.Right);

      if (left is not null || right is not null) {
        return new SqlBinaryExpression(
          binary.OperatorType,
          left ?? _inMicroseconds(binary.Left),
          right ?? _inMicroseconds(binary.Right),
          binary.Type,
          binary.TypeMapping);
      }
    }

    return _asStoredNumber(node) ?? base.VisitExtension(node);
  }

  /// <summary>Whether both operands of this operator have to be in the same unit.</summary>
  private static bool _isComparison(ExpressionType operatorType) =>
    operatorType is ExpressionType.Equal or ExpressionType.NotEqual
      or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
      or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

  /// <summary>
  /// The same extraction read as the stored number, or null when this is not a temporal read out of
  /// a document.
  /// </summary>
  private SqlUnaryExpression? _asStoredNumber(Expression node) =>
    node is SqlUnaryExpression { OperatorType: ExpressionType.Convert } cast
      && CanonicalTemporalConvention.KindOf(cast.Type) is not null
      && cast.Operand is PgJsonTraversalExpression traversal
        ? new SqlUnaryExpression(ExpressionType.Convert, traversal, typeof(long), microseconds)
        : null;

  /// <summary>The bound a stored number is compared with, in the stored unit.</summary>
  /// <remarks>
  /// A value in hand is converted here, by the same code that wrote the row. A parameter is not in
  /// hand and must not be looked at, because reading a parameter costs the cached SQL for the whole
  /// query, so the conversion is handed to the database, where it is a constant per statement.
  /// </remarks>
  private SqlExpression _inMicroseconds(SqlExpression bound) => bound switch {
    SqlConstantExpression { Value: DateTimeOffset at } =>
      new SqlConstantExpression(CanonicalTemporalFormat.ToEpochMicroseconds(at), typeof(long), microseconds),
    SqlConstantExpression { Value: DateTime at } =>
      new SqlConstantExpression(CanonicalTemporalFormat.ToEpochMicroseconds(at), typeof(long), microseconds),
    SqlConstantExpression { Value: DateOnly on } =>
      new SqlConstantExpression(CanonicalTemporalFormat.ToEpochMicroseconds(on), typeof(long), microseconds),
    _ => _epochOf(bound),
  };

  /// <summary>The bound converted in the database, which is what a parameter needs.</summary>
  private SqlUnaryExpression _epochOf(SqlExpression bound) =>
    new(
      ExpressionType.Convert,
      new SqlBinaryExpression(
        ExpressionType.Multiply,
        // date_part rather than extract: the two compute the same thing, but extract takes a
        // grammar of its own that an ordinary function call cannot render, and this is an ordinary
        // two-argument function. Whether the two agree with the serializer to the microsecond is
        // not assumed; it is asserted against the database.
        new SqlFunctionExpression(
          "date_part",
          [new SqlConstantExpression("epoch", typeof(string), StringTypeMapping.Default), bound],
          nullable: true,
          argumentsPropagateNullability: [false, true],
          typeof(double),
          null),
        new SqlConstantExpression(MICROSECONDS_PER_SECOND, typeof(long), microseconds),
        typeof(double),
        null),
      typeof(long),
      microseconds);
}
