using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace Whizbang.Data.EFCore.Postgres.Functions;

/// <summary>Translates <see cref="WhizbangSearchDbFunctions.FoldedContains"/>.</summary>
/// <docs>fundamentals/perspectives/physical-fields#search</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/SearchQueryShapeTests.cs</tests>
public sealed class FoldedContainsTranslator(ISqlExpressionFactory sqlExpressionFactory) : IMethodCallTranslator {
  private static readonly MethodInfo _foldedContains = typeof(WhizbangSearchDbFunctions).GetMethod(
    nameof(WhizbangSearchDbFunctions.FoldedContains), [typeof(DbFunctions), typeof(string), typeof(string)])!;

  private readonly ISqlExpressionFactory _factory = sqlExpressionFactory;

  /// <inheritdoc />
  /// <remarks>
  /// <c>wh_fold(value) LIKE wh_fold_pattern(term)</c>: the value side is exactly the trigram index's
  /// expression, and the pattern is the folded term with LIKE's wildcards escaped, wrapped in %. Both are
  /// IMMUTABLE, so with the term known the planner folds the pattern to a constant and uses the index.
  /// Unqualified, like the framework's other SQL functions, so they resolve through the search path to the
  /// schema the migrations created them in.
  /// </remarks>
  public SqlExpression? Translate(
      SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments,
      IDiagnosticsLogger<DbLoggerCategory.Query> logger) {
    ArgumentNullException.ThrowIfNull(method);
    ArgumentNullException.ThrowIfNull(arguments);
    if (!method.Equals(_foldedContains)) {
      return null;
    }

    // arguments[0] is DbFunctions and carries nothing.
    return _factory.Like(_fold("wh_fold", arguments[1]), _fold("wh_fold_pattern", arguments[2]));
  }

  private SqlExpression _fold(string function, SqlExpression argument) =>
    _factory.Function(
      function,
      [_factory.ApplyDefaultTypeMapping(argument)],
      nullable: true,
      argumentsPropagateNullability: [true],
      typeof(string));
}
