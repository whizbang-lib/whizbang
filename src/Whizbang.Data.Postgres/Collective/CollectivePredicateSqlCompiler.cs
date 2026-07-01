using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// A jsonb column path a compiled predicate filters a VALUE against (equality, <c>&lt;&gt;</c>, or
/// <c>IN</c>) — e.g. <c>(wh_per_draft_job, "scope-&gt;&gt;'t'")</c> or
/// <c>(wh_per_status, "data-&gt;&gt;'Status'")</c>. Each is a candidate for a btree expression index
/// <c>CREATE INDEX … ON &lt;Table&gt; ((&lt;ColumnExpression&gt;))</c>; a plain <c>gin(data)</c>/<c>gin(scope)</c>
/// index cannot serve <c>-&gt;&gt;</c> equality, so without these every apply seq-scans. <c>ColumnExpression</c>
/// is UNqualified (no table/alias prefix) so it drops straight into the index DDL. The top-level <c>id</c>
/// correlation column is never recorded — it is already the primary key. Driver-neutral and non-generic so the
/// EF Core index ensurer can consume the paths a <see cref="CollectivePredicateSqlCompiler{TModel}"/> emitted.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public sealed record ReferencedJsonPath(string Table, string ColumnExpression);

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
  /// <summary>Compiled <c>WHERE</c> fragment + the named parameters it binds + the jsonb column paths it
  /// filters on (§7 — btree expression-index candidates).</summary>
  public sealed record CompiledWhereClause(
    string SqlFragment,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<ReferencedJsonPath> ReferencedJsonPaths);

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
    var refs = new List<ReferencedJsonPath>();
    var sql = new StringBuilder();
    var ctx = new _ctx(filter.Parameters[0], OuterQualifier: "", InnerParam: null, InnerAlias: null,
      OuterTableName: outerTableName, InnerTableName: null);
    _compilePredicate(filter.Body, ctx, parameterPrefix, sql, parameters, refs);
    // Distinct so a column referenced twice (e.g. two comparisons on data->>'Status') yields one index candidate.
    var distinctRefs = refs.Distinct().ToList();
    return new CompiledWhereClause(sql.ToString(), parameters, distinctRefs);
  }

  // How to qualify a member access rooted at the outer row vs. an EXISTS inner row.
  private sealed record _ctx(
    ParameterExpression OuterParam, string OuterQualifier,
    ParameterExpression? InnerParam, string? InnerAlias,
    string? OuterTableName, string? InnerTableName);

  private static void _compilePredicate(
      Expression node, _ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters,
      List<ReferencedJsonPath> refs) {
    if (node is BinaryExpression { NodeType: ExpressionType.AndAlso } and) {
      sql.Append('(');
      _compilePredicate(and.Left, ctx, prefix, sql, parameters, refs);
      sql.Append(" AND ");
      _compilePredicate(and.Right, ctx, prefix, sql, parameters, refs);
      sql.Append(')');
      return;
    }

    if (node is UnaryExpression { NodeType: ExpressionType.Not } not) {
      // `!q.Of<T>().Any(...)` (not-in-cohort) → NOT EXISTS; `!(pred)` → NOT (pred).
      sql.Append("NOT (");
      _compilePredicate(not.Operand, ctx, prefix, sql, parameters, refs);
      sql.Append(')');
      return;
    }

    if (node is BinaryExpression { NodeType: ExpressionType.Equal } eq) {
      _compileComparison(eq, "=", ctx, prefix, sql, parameters, refs);
      return;
    }

    if (node is BinaryExpression { NodeType: ExpressionType.NotEqual } neq) {
      _compileComparison(neq, "<>", ctx, prefix, sql, parameters, refs);
      return;
    }

    if (node is MethodCallExpression mc) {
      if (mc.Method.Name == "Any" && mc.Arguments.Count == 2) {
        _compileExists(mc, ctx, prefix, sql, parameters, refs);
        return;
      }
      if (mc.Method.Name == "Contains") {
        _compileContains(mc, ctx, prefix, sql, parameters, refs);
        return;
      }
    }

    throw new NotSupportedException(
      $"CollectivePredicateSqlCompiler<{typeof(TModel).Name}> supports equality over scope/data fields and id, " +
      "&&-chains, Contains (→ IN), and q.Of<TOther>().Any(...) cross-perspective cohorts (→ EXISTS). " +
      $"Got expression node kind '{node.NodeType}'. Provide a raw-SQL form for richer predicates.");
  }

  private static void _compileComparison(
      BinaryExpression cmp, string op, _ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters,
      List<ReferencedJsonPath> refs) {
    var leftIsCol = _tryColumn(cmp.Left, ctx, refs, out var leftSql, out var leftProp);
    var rightIsCol = _tryColumn(cmp.Right, ctx, refs, out var rightSql, out var rightProp);

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
      MethodCallExpression mc, _ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters,
      List<ReferencedJsonPath> refs) {
    Expression sourceExpr;
    Expression itemExpr;
    if (mc.Object is null && mc.Arguments.Count == 2) {           // Enumerable.Contains(source, item)
      sourceExpr = mc.Arguments[0];
      itemExpr = mc.Arguments[1];
    } else if (mc.Object is null && mc.Arguments.Count == 3) {
      // MemoryExtensions.Contains(ReadOnlySpan<T> span, T item, IEqualityComparer<T>? comparer) — the span
      // overload the C# binder resolves `array.Contains(x)` to for some element types (notably value-type
      // enums; reference types like string land on the 2-arg form). The source array reaches arg[0] via an
      // array→span op_Implicit that _evaluateValue already unwraps. Only the default (null) comparer preserves
      // plain equality semantics — a custom comparer would change matching and isn't translatable to SQL.
      if (mc.Arguments[2] is not (ConstantExpression { Value: null } or DefaultExpression)) {
        throw new NotSupportedException(
          "Contains with a custom IEqualityComparer is not supported in a collective scope filter — it can't be translated to SQL IN.");
      }
      sourceExpr = mc.Arguments[0];
      itemExpr = mc.Arguments[1];
    } else if (mc.Object is not null && mc.Arguments.Count == 1) { // list.Contains(item)
      sourceExpr = mc.Object;
      itemExpr = mc.Arguments[0];
    } else {
      var arg0 = mc.Arguments.Count > 0 ? mc.Arguments[0].Type.Name : "-";
      var arg1 = mc.Arguments.Count > 1 ? mc.Arguments[1].Type.Name : "-";
      throw new NotSupportedException(
        $"Unsupported Contains shape in collective scope filter. Method={mc.Method.DeclaringType?.Name}.{mc.Method.Name}, " +
        $"Object={(mc.Object is null ? "null" : mc.Object.Type.Name)}, Args={mc.Arguments.Count} [{arg0}, {arg1}].");
    }

    if (!_tryColumn(itemExpr, ctx, refs, out var itemSql, out var itemProp)) {
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
      MethodCallExpression anyCall, _ctx ctx, string prefix, StringBuilder sql, Dictionary<string, object?> parameters,
      List<ReferencedJsonPath> refs) {
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
      InnerTableName = innerTable,
    };

    sql.Append("EXISTS (SELECT 1 FROM ").Append(innerTable).Append(' ').Append(alias).Append(" WHERE ");
    _compilePredicate(lambda.Body, innerCtx, prefix, sql, parameters, refs);
    sql.Append(')');
  }

  // row.Scope.X → scope->>'X', row.Data.X → data->>'X', row.Id → id — qualified per context (outer/inner).
  // When the match is a jsonb column, also records the UNqualified path against its table in <paramref name="refs"/>
  // as an expression-index candidate (§7).
  private static bool _tryColumn(Expression e, _ctx ctx, List<ReferencedJsonPath> refs, out string? columnSql, out string? propName) {
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
      var unqualified = $"{col}->>'{jsonKey}'";
      columnSql = $"{jq}{unqualified}";
      propName = jprop.Name;
      // Attribute the path to its table (outer vs. EXISTS-inner) so the index lands on the right relation.
      // Null table (Compile called without an outer table name) → skip: can't build the DDL, so no candidate.
      var table = ReferenceEquals(jp, ctx.OuterParam) ? ctx.OuterTableName
                : ctx.InnerParam is not null && ReferenceEquals(jp, ctx.InnerParam) ? ctx.InnerTableName
                : null;
      if (table is not null) {
        refs.Add(new ReferencedJsonPath(table, unqualified));
      }
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
