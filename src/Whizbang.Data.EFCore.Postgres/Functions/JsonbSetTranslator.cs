using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query;

namespace Whizbang.Data.EFCore.Postgres.Functions;

/// <summary>
/// Translates <see cref="WhizbangJsonDbFunctions.JsonbSet{TData}"/> to
/// PostgreSQL's <c>jsonb_set(&lt;data&gt;, '{&lt;path&gt;}', &lt;value&gt;::jsonb)</c>.
/// Used by Slice 6's collective-event EF adapter to compose chained
/// jsonb mutations entirely inside EF Core's expression tree —
/// parameter binding, escaping, and type mapping all flow through
/// EF's normal pipeline, no raw SQL strings in C#.
/// </summary>
/// <remarks>
/// <para>
/// Emits the path constant as <c>'{<i>name</i>}'</c> (Postgres
/// <c>text[]</c> literal — the property name wrapped in
/// <c>{…}</c>) and the value as a parameterized <c>jsonb</c>-cast
/// expression. Slice 6 mutations target top-level properties only;
/// nested paths use the raw-SQL escape hatch
/// (<c>SpecKind = RawSql</c>).
/// </para>
/// <para>
/// AOT note: matches the established Whizbang custom-translator
/// pattern (<see cref="JsonArrayContainsAnyTranslator"/>). The
/// translator runs at SQL-generation time, not the read/write hot
/// path; reflection on the method handle is a one-shot cache
/// lookup.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
public class JsonbSetTranslator(NpgsqlSqlExpressionFactory sqlExpressionFactory) : IMethodCallTranslator {
  private static readonly MethodInfo _jsonbSetGenericMethodDefinition =
    typeof(WhizbangJsonDbFunctions).GetMethods()
      .First(m => m.Name == nameof(WhizbangJsonDbFunctions.JsonbSet) && m.IsGenericMethodDefinition);

  private readonly NpgsqlSqlExpressionFactory _sqlExpressionFactory = sqlExpressionFactory;

  /// <inheritdoc />
  public SqlExpression? Translate(
      SqlExpression? instance,
      MethodInfo method,
      IReadOnlyList<SqlExpression> arguments,
      IDiagnosticsLogger<DbLoggerCategory.Query> logger) {

    if (!method.IsGenericMethod ||
        !method.GetGenericMethodDefinition().Equals(_jsonbSetGenericMethodDefinition)) {
      return null;
    }

    // arguments[0] is DbFunctions (unused)
    // arguments[1] is the jsonb column (e.g. r.Data)
    // arguments[2] is the top-level property name as a string literal (e.g. "Status")
    // arguments[3] is the JSON-serialized value as a string parameter (e.g. "\"Archived\"")

    var dataColumn = arguments[1];
    var pathArg = arguments[2];
    var valueArg = arguments[3];

    // Build the path text-array literal '{<name>}'. The translator only
    // supports top-level property mutations; multi-segment paths come
    // through the raw-SQL escape hatch.
    if (pathArg is not SqlConstantExpression { Value: string pathName }) {
      throw new InvalidOperationException(
        "WhizbangJsonDbFunctions.JsonbSet expects the path argument to be a constant string literal at SQL-generation time. " +
        "Dynamic paths are not supported through this translator — use SpecKind = RawSql for dynamic mutations.");
    }
    var pathLiteral = _sqlExpressionFactory.Constant(
      "{" + pathName + "}",
      typeof(string));

    // Cast the value parameter to jsonb at the SQL site:
    //   <valueArg>::jsonb
    // The data column already carries the jsonb type mapping (the
    // EF model declared HasColumnType("jsonb") on r.Data) — reuse it
    // for the cast.
    var jsonValueAsJsonb = _sqlExpressionFactory.Convert(
      valueArg,
      typeof(string),
      dataColumn.TypeMapping);

    // jsonb_set(<data>, <path-text-array>, <value-as-jsonb>)
    return _sqlExpressionFactory.Function(
      name: "jsonb_set",
      arguments: [dataColumn, pathLiteral, jsonValueAsJsonb],
      nullable: false,
      argumentsPropagateNullability: [true, false, false],
      returnType: dataColumn.Type,
      typeMapping: dataColumn.TypeMapping);
  }
}
