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
/// Generates SQL like: <c>scope->'AllowedPrincipals' ?| ARRAY['user:alice', 'group:sales']</c>
/// This is much more efficient than multiple OR'd @> containment checks for large arrays.
/// </remarks>
/// <docs>fundamentals/security/security#principal-filtering</docs>
public class JsonArrayContainsAnyTranslator(NpgsqlSqlExpressionFactory sqlExpressionFactory) : IMethodCallTranslator {
  private static readonly MethodInfo _allowedPrincipalsContainsAnyMethod =
    typeof(WhizbangJsonDbFunctions).GetMethod(
      nameof(WhizbangJsonDbFunctions.AllowedPrincipalsContainsAny),
      [typeof(DbFunctions), typeof(PerspectiveScope), typeof(string[])])!;

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
    // arguments[1] is the Scope JSONB column (e.g., r.Scope)
    // arguments[2] is the string[] values to check

    var scopeColumn = arguments[1];
    var values = arguments[2];

    // Extract the allowed-principals array from the Scope JSONB column: scope->'ap'.
    // The key is the SERIALIZED name, not the CLR one — PerspectiveScope.AllowedPrincipals carries
    // [JsonPropertyName("ap")], so traversing "AllowedPrincipals" reads a key that is not in the
    // document. That returns SQL NULL, `?|` against NULL is NULL, and the row is filtered out:
    // a security predicate that silently matches nothing rather than failing.
    var allowedPrincipalsPath = _sqlExpressionFactory.JsonTraversal(
      scopeColumn,
      [_sqlExpressionFactory.Constant("ap")],
      returnsText: false,  // Returns JSONB, not text
      typeof(string),
      scopeColumn.TypeMapping);

    // Then apply the ?| operator: scope->'ap' ?| ARRAY[...]
    // The ?| operator checks if any of the text array elements exist in the JSONB array
    return _sqlExpressionFactory.MakePostgresBinary(
      PgExpressionType.JsonExistsAny,
      allowedPrincipalsPath,
      values);
  }
}
