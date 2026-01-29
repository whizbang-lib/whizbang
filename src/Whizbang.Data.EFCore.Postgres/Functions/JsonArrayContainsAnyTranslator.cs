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
/// <docs>core-concepts/security#principal-filtering</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/Functions/JsonArrayContainsAnyTranslatorTests.cs</tests>
public class JsonArrayContainsAnyTranslator : IMethodCallTranslator {
  private static readonly MethodInfo _allowedPrincipalsContainsAnyMethod =
    typeof(WhizbangJsonDbFunctions).GetMethod(
      nameof(WhizbangJsonDbFunctions.AllowedPrincipalsContainsAny),
      [typeof(DbFunctions), typeof(PerspectiveScope), typeof(string[])])!;

  private readonly NpgsqlSqlExpressionFactory _sqlExpressionFactory;

  public JsonArrayContainsAnyTranslator(NpgsqlSqlExpressionFactory sqlExpressionFactory) {
    _sqlExpressionFactory = sqlExpressionFactory;
  }

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

    // First, extract the AllowedPrincipals array from the Scope JSONB column
    // This generates: scope->'AllowedPrincipals'
    var allowedPrincipalsPath = _sqlExpressionFactory.JsonTraversal(
      scopeColumn,
      new[] { _sqlExpressionFactory.Constant("AllowedPrincipals") },
      returnsText: false,  // Returns JSONB, not text
      typeof(string),
      scopeColumn.TypeMapping);

    // Then apply the ?| operator: scope->'AllowedPrincipals' ?| ARRAY[...]
    // The ?| operator checks if any of the text array elements exist in the JSONB array
    return _sqlExpressionFactory.MakePostgresBinary(
      PgExpressionType.JsonExistsAny,
      allowedPrincipalsPath,
      values);
  }
}
