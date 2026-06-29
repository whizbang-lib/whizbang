using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.Dapper.Postgres.Collective;

/// <summary>
/// Compiles a collective apply's composed <c>WHERE</c> predicate
/// (<see cref="Expression{TDelegate}"/> of <c>Func&lt;PerspectiveRow&lt;TModel&gt;, bool&gt;</c>) into a
/// Postgres SQL <c>WHERE</c> fragment over the <c>scope</c> and <c>data</c> jsonb columns + a parameter
/// dictionary, for the Dapper driver. The predicate is the output of <see cref="CollectiveWhereComposer"/>:
/// a resolver scope envelope (over <c>row.Scope</c>) optionally AND-ed with the handler's per-model
/// projection (over <c>row.Data</c>, and/or a cross-perspective <c>q.Of&lt;TOther&gt;().Any(...)</c>). The EF
/// driver composes the predicate directly into <c>Where(...)</c>; Dapper has no LINQ pipeline, so it is
/// translated here.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Supported:</strong> equality over a single jsonb-column field — <c>row.Scope.PropName == value</c>
/// (→ <c>scope-&gt;&gt;'PropName' = @param</c>) or <c>row.Data.PropName == value</c> (→
/// <c>data-&gt;&gt;'PropName' = @param</c>); the top-level <c>row.Id</c> (→ <c>id</c>, for correlation);
/// <c>&amp;&amp;</c>-chained conjunctions; <c>&lt;values&gt;.Contains(row.Data.X)</c> (→ <c>IN</c>); and
/// cross-perspective cohorts <c>q.Of&lt;TOther&gt;().Any(s =&gt; s.Id == r.Id &amp;&amp; …)</c> (→ a correlated
/// <c>EXISTS (SELECT 1 FROM &lt;TOther table&gt; s WHERE …)</c>).
/// </para>
/// <para>
/// <strong>Unsupported (throws <see cref="NotSupportedException"/>):</strong> non-equality operators,
/// disjunctions, arbitrary top-level/system columns (e.g. <c>row.Version</c>), nested <c>EXISTS</c>, or
/// comparisons not rooted at a known row parameter. A handler whose Where needs richer SQL should provide a
/// raw-SQL form (mirrors the <c>DapperCollectiveSpecCompiler</c> constraint matrix).
/// </para>
/// </remarks>
/// <typeparam name="TModel">The perspective model whose <see cref="PerspectiveRow{TModel}"/> the filter ranges over.</typeparam>
/// <docs>fundamentals/messaging/collective-events</docs>
[SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Whizbang.Data.Dapper.Postgres layer compiles captured-value sub-expressions; values come from compile-time selector metadata, not runtime type scanning.")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Whizbang.Data.Dapper.Postgres layer compiles captured-value sub-expressions; values come from compile-time selector metadata, not runtime type scanning.")]
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Matches the Whizbang.Data.Dapper.Postgres pattern of generic-over-TModel static compilers (DapperCollectiveSpecCompiler).")]
public static class DapperCollectiveScopeFilterCompiler<TModel> where TModel : class {
  /// <summary>Compiled <c>WHERE</c> fragment + the named parameters it binds.</summary>
  public sealed record CompiledWhereClause(
    string SqlFragment,
    IReadOnlyDictionary<string, object?> Parameters);

  /// <summary>
  /// Compile a collective-apply WHERE predicate to a SQL fragment over the <c>scope</c>/<c>data</c> jsonb
  /// columns — including correlated <c>EXISTS</c> subqueries for cross-perspective cohorts
  /// (<c>q.Of&lt;TOther&gt;().Any(...)</c>). Parameter names are namespaced by
  /// <paramref name="parameterPrefix"/> to avoid collisions with the SET-clause parameters.
  /// </summary>
  /// <param name="filter">The composed predicate (scope envelope, handler Where, and/or sibling-cohort Any).</param>
  /// <param name="parameterPrefix">Namespaces emitted parameter names.</param>
  /// <param name="outerTableName">
  /// The table being UPDATEd. Required to qualify the outer row inside a correlated <c>EXISTS</c>; pass it
  /// whenever the predicate may reference a sibling perspective.
  /// </param>
  public static CompiledWhereClause Compile(
      Expression<Func<PerspectiveRow<TModel>, bool>> filter,
      string parameterPrefix = "where",
      string? outerTableName = null) {
    ArgumentNullException.ThrowIfNull(filter);
    ArgumentException.ThrowIfNullOrWhiteSpace(parameterPrefix);

    var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
    var sql = new StringBuilder();
    var ctx = new _ctx(filter.Parameters[0], OuterQualifier: "", InnerParam: null, InnerAlias: null, OuterTableName: outerTableName);
    _compilePredicate(filter.Body, ctx, parameterPrefix, sql, parameters);
    return new CompiledWhereClause(sql.ToString(), parameters);
  }

  // How to qualify a member access rooted at the outer row vs. an EXISTS inner row.
  private sealed record _ctx(
    ParameterExpression OuterParam, string OuterQualifier,
    ParameterExpression? InnerParam, string? InnerAlias,
    string? OuterTableName);

  private static void _compilePredicate(
      Expression node, _ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters) {
    if (node is BinaryExpression { NodeType: ExpressionType.AndAlso } and) {
      sql.Append('(');
      _compilePredicate(and.Left, ctx, prefix, sql, parameters);
      sql.Append(" AND ");
      _compilePredicate(and.Right, ctx, prefix, sql, parameters);
      sql.Append(')');
      return;
    }

    if (node is BinaryExpression { NodeType: ExpressionType.Equal } eq) {
      _compileEquality(eq, ctx, prefix, sql, parameters);
      return;
    }

    if (node is MethodCallExpression mc) {
      if (mc.Method.Name == "Any" && mc.Arguments.Count == 2) {
        _compileExists(mc, ctx, prefix, sql, parameters);
        return;
      }
      if (mc.Method.Name == "Contains") {
        _compileContains(mc, ctx, prefix, sql, parameters);
        return;
      }
    }

    throw new NotSupportedException(
      $"DapperCollectiveScopeFilterCompiler<{typeof(TModel).Name}> supports equality over scope/data fields and id, " +
      "&&-chains, Contains (→ IN), and q.Of<TOther>().Any(...) cross-perspective cohorts (→ EXISTS). " +
      $"Got expression node kind '{node.NodeType}'. Provide a raw-SQL form for richer predicates.");
  }

  private static void _compileEquality(
      BinaryExpression eq, _ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters) {
    var leftIsCol = _tryColumn(eq.Left, ctx, out var leftSql, out var leftProp);
    var rightIsCol = _tryColumn(eq.Right, ctx, out var rightSql, out var rightProp);

    if (leftIsCol && rightIsCol) {
      // Column == column → a correlation (e.g. s.id = wh_per_job.id). No parameter.
      sql.Append(leftSql).Append(" = ").Append(rightSql);
      return;
    }
    if (leftIsCol) {
      _appendColumnEqualsValue(leftSql!, leftProp!, eq.Right, prefix, sql, parameters);
      return;
    }
    if (rightIsCol) {
      _appendColumnEqualsValue(rightSql!, rightProp!, eq.Left, prefix, sql, parameters);
      return;
    }
    throw new NotSupportedException(
      "DapperCollectiveScopeFilterCompiler equality requires at least one side to be a scope/data field or id " +
      "(row.Scope.X / row.Data.X / row.Id). Neither side matched.");
  }

  private static void _appendColumnEqualsValue(
      string columnSql, string propName, Expression valueExpr, string prefix, StringBuilder sql,
      Dictionary<string, object?> parameters) {
    var value = _evaluateValue(valueExpr);
    var paramName = $"{prefix}_{propName.ToLowerInvariant()}";
    parameters[paramName] = value?.ToString();
    sql.Append(columnSql).Append(" = @").Append(paramName);
  }

  // <values>.Contains(row.Data.X) → row.data->>'X' IN (@p0, @p1, …).
  private static void _compileContains(
      MethodCallExpression mc, _ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters) {
    Expression sourceExpr;
    Expression itemExpr;
    if (mc.Object is null && mc.Arguments.Count == 2) {           // Enumerable.Contains(source, item)
      sourceExpr = mc.Arguments[0];
      itemExpr = mc.Arguments[1];
    } else if (mc.Object is not null && mc.Arguments.Count == 1) { // list.Contains(item)
      sourceExpr = mc.Object;
      itemExpr = mc.Arguments[0];
    } else {
      throw new NotSupportedException("Unsupported Contains shape in collective scope filter.");
    }

    if (!_tryColumn(itemExpr, ctx, out var itemSql, out var itemProp)) {
      throw new NotSupportedException(
        "Contains is only supported as <values>.Contains(row.Data.X / row.Scope.X) — the item must be a column field.");
    }

    var values = _evaluateValue(sourceExpr) as System.Collections.IEnumerable
      ?? throw new NotSupportedException("Contains source must evaluate to a captured collection of values.");

    var names = new List<string>();
    var i = 0;
    foreach (var v in values) {
      var name = $"{prefix}_{itemProp!.ToLowerInvariant()}_{i}";
      parameters[name] = v?.ToString();
      names.Add("@" + name);
      i++;
    }

    sql.Append(itemSql).Append(" IN (").Append(names.Count == 0 ? "NULL" : string.Join(", ", names)).Append(')');
  }

  // q.Of<TOther>().Any(s => s.Id == r.Id && …) → EXISTS (SELECT 1 FROM <TOther table> s WHERE …).
  private static void _compileExists(
      MethodCallExpression anyCall, _ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters) {
    if (ctx.InnerParam is not null) {
      throw new NotSupportedException("Nested cross-perspective cohorts (EXISTS within EXISTS) are not supported.");
    }
    if (ctx.OuterTableName is null) {
      throw new NotSupportedException(
        "A cross-perspective cohort (q.Of<TOther>().Any(...)) needs the outer table name to qualify the correlation. " +
        "Pass outerTableName to Compile (the applier supplies it).");
    }

    if (anyCall.Arguments[0] is not MethodCallExpression { Method.IsGenericMethod: true } ofCall
        || ofCall.Method.Name != "Of") {
      throw new NotSupportedException(
        "The Any source must be query.Of<TOther>() — a sibling-perspective queryable from the ICollectiveQuery context.");
    }
    var otherModel = ofCall.Method.GetGenericArguments()[0];
    var queryInstance = _evaluateValue(ofCall.Object
        ?? throw new NotSupportedException("query.Of<TOther>() must be called on an ICollectiveQuery instance."));
    if (queryInstance is not DapperCollectiveQuery dapperQuery) {
      throw new NotSupportedException(
        $"The cross-perspective query context must be a {nameof(DapperCollectiveQuery)} for the Dapper driver.");
    }
    var innerTable = dapperQuery.TableFor(otherModel);

    var predicate = anyCall.Arguments[1];
    while (predicate is UnaryExpression { NodeType: ExpressionType.Quote } quote) {
      predicate = quote.Operand;
    }
    if (predicate is not LambdaExpression lambda) {
      throw new NotSupportedException("The Any predicate must be a lambda.");
    }

    const char alias = 's';
    var innerCtx = ctx with {
      OuterQualifier = ctx.OuterTableName + ".",
      InnerParam = lambda.Parameters[0],
      InnerAlias = alias.ToString(),
    };

    sql.Append("EXISTS (SELECT 1 FROM ").Append(innerTable).Append(' ').Append(alias).Append(" WHERE ");
    _compilePredicate(lambda.Body, innerCtx, prefix, sql, parameters);
    sql.Append(')');
  }

  // row.Scope.X → scope->>'X', row.Data.X → data->>'X', row.Id → id — qualified per context (outer/inner).
  private static bool _tryColumn(Expression e, _ctx ctx, out string? columnSql, out string? propName) {
    columnSql = null;
    propName = null;
    while (e is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
      e = convert.Operand;
    }

    if (e is MemberExpression { Member: PropertyInfo jprop, Expression: MemberExpression { Member.Name: var container, Expression: ParameterExpression jp } }
        && _qualifierFor(jp, ctx) is { } jq
        && _jsonbColumnFor(container) is { } col) {
      columnSql = $"{jq}{col}->>'{jprop.Name}'";
      propName = jprop.Name;
      return true;
    }

    if (e is MemberExpression { Member: PropertyInfo { Name: "Id" }, Expression: ParameterExpression ip }
        && _qualifierFor(ip, ctx) is { } iq) {
      columnSql = $"{iq}id";
      propName = "id";
      return true;
    }

    return false;
  }

  // The SQL qualifier ("" / "{outerTable}." / "{alias}.") for a row param, or null if it isn't a known one.
  private static string? _qualifierFor(ParameterExpression p, _ctx ctx) {
    if (ReferenceEquals(p, ctx.OuterParam)) {
      return ctx.OuterQualifier;
    }
    if (ctx.InnerParam is not null && ReferenceEquals(p, ctx.InnerParam)) {
      return ctx.InnerAlias + ".";
    }
    return null;
  }

  private static string? _jsonbColumnFor(string container) => container switch {
    "Scope" => "scope",
    "Data" => "data",
    _ => null,
  };

  // Resolve a captured value (literal, captured local/field, or a member chain ending at one) by reading
  // members directly rather than IL-compiling + invoking a lambda — compiling a fresh lambda over a value
  // captured in an async test method is fragile (InvalidProgramException / reflection-invoke NotSupported).
  private static object? _evaluateValue(Expression valueExpr) {
    // Strip framework conversions: Convert nodes (boxing/reference) and user-conversion operators —
    // notably the array → ReadOnlySpan op_Implicit the C# binder inserts when `.Contains` resolves to the
    // span-based MemoryExtensions overload. The underlying captured value is the operand.
    while (true) {
      if (valueExpr is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
        valueExpr = convert.Operand;
        continue;
      }
      if (valueExpr is MethodCallExpression { Method.Name: "op_Implicit" or "op_Explicit", Arguments: [var operand] }) {
        valueExpr = operand;
        continue;
      }
      break;
    }
    if (valueExpr is ConstantExpression c) {
      return c.Value;
    }
    if (valueExpr is MemberExpression m) {
      return _readMember(m);
    }
    throw new NotSupportedException(
      $"DapperCollectiveScopeFilterCompiler cannot resolve a value of node kind {valueExpr.NodeType} " +
      "(supported: constant literal, captured local/field).");
  }

  private static object? _readMember(MemberExpression m) {
    var instance = m.Expression is null ? null : _evaluateValue(m.Expression);
    return m.Member switch {
      FieldInfo f => f.GetValue(instance),
      PropertyInfo p => p.GetValue(instance),
      _ => throw new NotSupportedException(
        $"Unsupported member '{m.Member.Name}' in collective scope-filter value."),
    };
  }
}
