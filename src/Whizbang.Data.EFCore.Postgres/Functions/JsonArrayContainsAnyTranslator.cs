using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions;
using Whizbang.Core.Lenses;

#pragma warning disable EF1001 // Internal EF Core API usage - we deliberately use Npgsql internals for ?| operator support

namespace Whizbang.Data.EFCore.Postgres.Functions;

/// <summary>
/// Translates <see cref="WhizbangJsonDbFunctions.AllowedPrincipalsContainsAny"/> to PostgreSQL's ?| operator.
/// </summary>
/// <remarks>
/// Generates SQL like: <c>scope->'ap' ?| ARRAY['user:alice', 'group:sales']</c>
/// This is much more efficient than multiple OR'd @> containment checks for large arrays.
/// </remarks>
/// <docs>fundamentals/security/security#principal-filtering</docs>
public class JsonArrayContainsAnyTranslator(NpgsqlSqlExpressionFactory sqlExpressionFactory) : IMethodCallTranslator {
  private static readonly MethodInfo _allowedPrincipalsContainsAnyMethod =
    typeof(WhizbangJsonDbFunctions).GetMethod(
      nameof(WhizbangJsonDbFunctions.AllowedPrincipalsContainsAny),
      [typeof(DbFunctions), typeof(List<string>), typeof(string[])])!;

  private readonly NpgsqlSqlExpressionFactory _sqlExpressionFactory = sqlExpressionFactory;

  public SqlExpression? Translate(
      SqlExpression? instance,
      MethodInfo method,
      IReadOnlyList<SqlExpression> arguments,
      IDiagnosticsLogger<DbLoggerCategory.Query> logger) {

    if (!method.Equals(_allowedPrincipalsContainsAnyMethod)) {
      return null;
    }

    // arguments[0] is DbFunctions (unused)
    // arguments[1] is the allowed-principals MEMBER, already rendered by EF as a traversal into the
    // scope document (scope->'ap' — EF uses the serialized name from [JsonPropertyName]).
    // arguments[2] is the string[] values to check.

    // The member, not the whole complex type. PerspectiveScope is mapped with
    // ComplexProperty().ToJson(), so it is a STRUCTURAL type: EF cannot render it as the scalar
    // SqlExpression a DbFunction argument has to be, and abandons translation before any
    // method-call translator is consulted — which is why this function never worked while taking
    // the scope itself. Its collection member does render, so EF hands us the traversal already
    // built and correctly keyed, and all that remains is the operator.
    var allowedPrincipalsPath = arguments[1];
    var values = arguments[2];

    // scope->'ap' ?| ARRAY[...] — the ?| operator asks whether any element of the right-hand text
    // array exists in the left-hand JSONB array, and is GIN-indexable, which is the whole reason
    // this function exists rather than a per-row unnest.
    return _sqlExpressionFactory.MakePostgresBinary(
      PgExpressionType.JsonExistsAny,
      allowedPrincipalsPath,
      values);
  }
}
