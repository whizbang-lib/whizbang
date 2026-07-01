using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// Shared (driver-neutral) compiler that translates a collective apply's composed <c>WHERE</c> predicate
/// (<see cref="Expression{TDelegate}"/> of <c>Func&lt;PerspectiveRow&lt;TModel&gt;, bool&gt;</c>) into a
/// Postgres SQL <c>WHERE</c> fragment over the <c>scope</c> and <c>data</c> jsonb columns + a parameter
/// dictionary. The predicate is the output of <see cref="CollectiveWhereComposer"/>: a resolver scope
/// envelope (over <c>row.Scope</c>) AND-ed with the handler's per-model projection (over <c>row.Data</c>,
/// and/or a cross-perspective <c>q.Of&lt;TOther&gt;().Any(...)</c>).
/// </summary>
/// <remarks>
/// <para>
/// Both drivers use this so there is one predicate-translation code path: the Dapper applier (which has no
/// LINQ provider) and the EF Core raw jsonb_set path (which cannot use <c>ExecuteUpdateAsync</c> for scalar/
/// polymorphic jsonb or null-valued setters) both emit <c>UPDATE … WHERE &lt;compiled&gt;</c> — no id
/// materialization, no <c>SELECT id</c> round-trip.
/// </para>
/// <para>
/// <strong>Supported:</strong> equality over a single jsonb-column field — <c>row.Scope.PropName == value</c>
/// (→ <c>scope-&gt;&gt;'PropName' = @param</c>) or <c>row.Data.PropName == value</c> (→
/// <c>data-&gt;&gt;'PropName' = @param</c>); the top-level <c>row.Id</c> (→ <c>id</c>, for correlation);
/// <c>&amp;&amp;</c>-chained conjunctions; <c>&lt;values&gt;.Contains(row.Data.X)</c> (→ <c>IN</c>); and
/// cross-perspective cohorts <c>q.Of&lt;TOther&gt;().Any(s =&gt; s.Id == r.Id &amp;&amp; …)</c> (→ a correlated
/// <c>EXISTS (SELECT 1 FROM &lt;TOther table&gt; s WHERE …)</c>, the table resolved via
/// <see cref="ICollectiveSiblingTableSource"/> read off the <c>q.Of&lt;TOther&gt;()</c> node).
/// </para>
/// <para>
/// <strong>Unsupported (throws <see cref="NotSupportedException"/>):</strong> non-equality operators,
/// disjunctions, arbitrary top-level/system columns (e.g. <c>row.Version</c>), nested <c>EXISTS</c>, or
/// comparisons not rooted at a known row parameter.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The perspective model whose <see cref="PerspectiveRow{TModel}"/> the filter ranges over.</typeparam>
/// <docs>fundamentals/messaging/collective-events</docs>
[SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Compiles captured-value sub-expressions; values come from compile-time selector metadata, not runtime type scanning.")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Compiles captured-value sub-expressions; values come from compile-time selector metadata, not runtime type scanning.")]
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Matches the established Whizbang.Data.Postgres pattern of generic-over-TModel static compilers.")]
public static class CollectivePredicateSqlCompiler<TModel> where TModel : class {
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

    if (node is UnaryExpression { NodeType: ExpressionType.Not } not) {
      // `!q.Of<T>().Any(...)` (not-in-cohort) → NOT EXISTS; `!(pred)` → NOT (pred).
      sql.Append("NOT (");
      _compilePredicate(not.Operand, ctx, prefix, sql, parameters);
      sql.Append(')');
      return;
    }

    if (node is BinaryExpression { NodeType: ExpressionType.Equal } eq) {
      _compileComparison(eq, "=", ctx, prefix, sql, parameters);
      return;
    }

    if (node is BinaryExpression { NodeType: ExpressionType.NotEqual } neq) {
      _compileComparison(neq, "<>", ctx, prefix, sql, parameters);
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
      $"CollectivePredicateSqlCompiler<{typeof(TModel).Name}> supports equality over scope/data fields and id, " +
      "&&-chains, Contains (→ IN), and q.Of<TOther>().Any(...) cross-perspective cohorts (→ EXISTS). " +
      $"Got expression node kind '{node.NodeType}'. Provide a raw-SQL form for richer predicates.");
  }

  private static void _compileComparison(
      BinaryExpression cmp, string op, _ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters) {
    var leftIsCol = _tryColumn(cmp.Left, ctx, out var leftSql, out var leftProp);
    var rightIsCol = _tryColumn(cmp.Right, ctx, out var rightSql, out var rightProp);

    if (leftIsCol && rightIsCol) {
      // Column <op> column → a correlation (e.g. s.id = wh_per_job.id). No parameter.
      sql.Append(leftSql).Append(' ').Append(op).Append(' ').Append(rightSql);
      return;
    }
    if (leftIsCol) {
      _appendColumnCompareValue(leftSql!, leftProp!, op, cmp.Right, prefix, sql, parameters);
      return;
    }
    if (rightIsCol) {
      _appendColumnCompareValue(rightSql!, rightProp!, op, cmp.Left, prefix, sql, parameters);
      return;
    }
    throw new NotSupportedException(
      "CollectivePredicateSqlCompiler comparison requires at least one side to be a scope/data field or id " +
      "(row.Scope.X / row.Data.X / row.Id). Neither side matched.");
  }

  private static void _appendColumnCompareValue(
      string columnSql, string propName, string op, Expression valueExpr, string prefix, StringBuilder sql,
      Dictionary<string, object?> parameters) {
    var value = _evaluateValue(valueExpr);
    var paramName = $"{prefix}_{propName.ToLowerInvariant()}";
    parameters[paramName] = value?.ToString();
    sql.Append(columnSql).Append(' ').Append(op).Append(" @").Append(paramName);
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
    // Driver-neutral table resolution: the query binding (EF/Dapper) implements ICollectiveSiblingTableSource,
    // read straight off the q.Of<TOther>() node — no driver-specific cast in the shared compiler.
    if (queryInstance is not ICollectiveSiblingTableSource tableSource) {
      throw new NotSupportedException(
        $"The cross-perspective query context must implement {nameof(ICollectiveSiblingTableSource)} so the " +
        "compiler can resolve the sibling table for the EXISTS correlation.");
    }
    var innerTable = tableSource.TableFor(otherModel);

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
      // The jsonb KEY is the serialized name, which honors [JsonPropertyName] — e.g. PerspectiveScope.TenantId
      // is [JsonPropertyName("t")], so it persists as scope->>'t', NOT scope->>'TenantId'. Emit the short key
      // (matches EF's own translation for the native path). The PARAMETER name stays the property name.
      var jsonKey = jprop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? jprop.Name;
      columnSql = $"{jq}{col}->>'{jsonKey}'";
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
      $"CollectivePredicateSqlCompiler cannot resolve a value of node kind {valueExpr.NodeType} " +
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
