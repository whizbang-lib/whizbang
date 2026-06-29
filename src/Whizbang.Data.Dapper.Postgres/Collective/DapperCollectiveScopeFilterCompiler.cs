using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.Dapper.Postgres.Collective;

/// <summary>
/// Compiles a collective scope resolver's <c>ScopeFilter</c> predicate
/// (<see cref="Expression{TDelegate}"/> of <c>Func&lt;PerspectiveRow&lt;TModel&gt;, bool&gt;</c>) into a
/// Postgres SQL <c>WHERE</c> fragment over the <c>scope</c> jsonb column + a parameter dictionary, for the
/// Dapper driver. The EF driver composes the predicate directly into <c>Where(...)</c>; Dapper has no LINQ
/// pipeline, so the predicate is translated here.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Supported (first cut, matching the built-in resolvers):</strong> equality comparisons over a
/// single scope field — <c>row.Scope.PropName == value</c> — and <c>&amp;&amp;</c>-chained conjunctions of
/// them. Each compiles to <c>scope-&gt;&gt;'PropName' = @param</c> (text comparison), mirroring how
/// <c>TenantCollectiveScopeResolver</c> filters <c>row.Scope.TenantId == tenantId</c>.
/// </para>
/// <para>
/// <strong>Unsupported (throws <see cref="NotSupportedException"/>):</strong> non-equality operators,
/// disjunctions, computed/nested members, or comparisons not rooted at <c>row.Scope</c>. A custom scope
/// resolver whose filter needs richer SQL should provide a raw-SQL form (mirrors the
/// <c>DapperCollectiveSpecCompiler</c> constraint matrix).
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
  /// Compile a scope-filter predicate to a SQL <c>WHERE</c> fragment over the <c>scope</c> jsonb column.
  /// Parameter names are namespaced by <paramref name="parameterPrefix"/> to avoid collisions with the
  /// SET-clause parameters when composed into one statement.
  /// </summary>
  public static CompiledWhereClause Compile(
      Expression<Func<PerspectiveRow<TModel>, bool>> filter,
      string parameterPrefix = "where") {
    ArgumentNullException.ThrowIfNull(filter);
    ArgumentException.ThrowIfNullOrWhiteSpace(parameterPrefix);

    var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
    var sql = new StringBuilder();
    _compilePredicate(filter.Body, filter.Parameters[0], parameterPrefix, sql, parameters);
    return new CompiledWhereClause(sql.ToString(), parameters);
  }

  private static void _compilePredicate(
      Expression node, ParameterExpression rowParam, string prefix, StringBuilder sql,
      Dictionary<string, object?> parameters) {
    if (node is BinaryExpression { NodeType: ExpressionType.AndAlso } and) {
      sql.Append('(');
      _compilePredicate(and.Left, rowParam, prefix, sql, parameters);
      sql.Append(" AND ");
      _compilePredicate(and.Right, rowParam, prefix, sql, parameters);
      sql.Append(')');
      return;
    }

    if (node is BinaryExpression { NodeType: ExpressionType.Equal } eq) {
      var (scopeProp, valueExpr) = _orderScopeMemberAndValue(eq, rowParam);
      var value = _evaluateValue(valueExpr);
      var paramName = $"{prefix}_{scopeProp.ToLowerInvariant()}";
      parameters[paramName] = value?.ToString();
      sql.Append("scope->>'").Append(scopeProp).Append("' = @").Append(paramName);
      return;
    }

    throw new NotSupportedException(
      $"DapperCollectiveScopeFilterCompiler<{typeof(TModel).Name}> supports only equality comparisons over a single " +
      "scope field (row.Scope.Prop == value) and &&-chained conjunctions of them. " +
      $"Got expression node kind '{node.NodeType}'. Provide a raw-SQL scope form for richer predicates.");
  }

  // One side must be `row.Scope.<Prop>`; the other is the value. Returns (propName, valueExpr).
  private static (string ScopeProp, Expression ValueExpr) _orderScopeMemberAndValue(
      BinaryExpression eq, ParameterExpression rowParam) {
    if (_tryGetScopeProperty(eq.Left, rowParam, out var leftProp)) {
      return (leftProp!, eq.Right);
    }
    if (_tryGetScopeProperty(eq.Right, rowParam, out var rightProp)) {
      return (rightProp!, eq.Left);
    }
    throw new NotSupportedException(
      "DapperCollectiveScopeFilterCompiler only supports equality where one side is a scope field " +
      "(row.Scope.PropertyName). Neither side matched that shape.");
  }

  private static bool _tryGetScopeProperty(Expression e, ParameterExpression rowParam, out string? propName) {
    // Strip boxing converts.
    while (e is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
      e = convert.Operand;
    }
    if (e is MemberExpression { Member: PropertyInfo prop, Expression: MemberExpression { Member.Name: "Scope", Expression: ParameterExpression p } }
        && ReferenceEquals(p, rowParam)) {
      propName = prop.Name;
      return true;
    }
    propName = null;
    return false;
  }

  private static object? _evaluateValue(Expression valueExpr) {
    switch (valueExpr) {
      case ConstantExpression c:
        return c.Value;
      case MemberExpression or UnaryExpression { NodeType: ExpressionType.Convert }:
        return Expression.Lambda(valueExpr).Compile().DynamicInvoke();
      default:
        throw new NotSupportedException(
          $"DapperCollectiveScopeFilterCompiler cannot resolve a scope-filter value of node kind {valueExpr.NodeType} " +
          "(supported: constant literal, captured local, captured field).");
    }
  }
}
