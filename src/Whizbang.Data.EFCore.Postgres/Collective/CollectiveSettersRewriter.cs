using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Functions;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// Translates a perspective's <see cref="ICollectiveSpec{TModel}.Setters"/>
/// LINQ expression into the shape EF Core's
/// <see cref="UpdateSettersBuilder{T}"/> expects, using a chained
/// <see cref="WhizbangJsonDbFunctions.JsonbSet{TData}"/> expression so the
/// resulting <c>ExecuteUpdateAsync</c> emits a single
/// <c>SET data = jsonb_set(jsonb_set(data, '{A}', …), '{B}', …)</c>
/// statement.
/// </summary>
/// <remarks>
/// <para>
/// EF Core 10's <c>ExecuteUpdateAsync</c> only supports updating
/// top-level scalar columns via <c>SetProperty</c>. Direct
/// <c>SetProperty(r =&gt; r.Data.X, value)</c> rewrites are rejected at
/// SQL-generation time. The Slice 6 adapter therefore composes a
/// single column-level update of the whole <c>Data</c> jsonb cell, with
/// the per-property mutations folded into a chained
/// <see cref="WhizbangJsonDbFunctions.JsonbSet{TData}"/> call. EF's
/// custom-translator pipeline (<see cref="JsonbSetTranslator"/>)
/// emits <c>jsonb_set(...)</c> SQL, so parameter binding, escaping,
/// and type mapping all flow through EF's normal pipeline — no raw
/// SQL strings in C#.
/// </para>
/// <para>
/// Constraint matrix:
/// </para>
/// <list type="bullet">
///   <item><description>Scalar top-level <c>SetProperty(j =&gt; j.PropName, constant)</c> — supported.</description></item>
///   <item><description>Constant value sources (literal, captured local, captured field) — supported.</description></item>
///   <item><description>Multiple chained <c>SetProperty</c> calls — folded into nested <c>jsonb_set</c>.</description></item>
///   <item><description>Computed expressions (<c>j =&gt; j.X + 1</c>) — UNSUPPORTED in v1.0; throws <see cref="NotSupportedException"/> with a pointer to <c>SpecKind = RawSql</c>. Matches the Slice 9 Dapper compiler's constraint matrix.</description></item>
///   <item><description>Nested paths (<c>j =&gt; j.Nested.X</c>) — UNSUPPORTED in v1.0; throws <see cref="NotSupportedException"/>.</description></item>
/// </list>
/// <para>
/// AOT note: matches the EF Core data-layer pattern of suppressing
/// IL2060 / IL3050 — EF Core inherently uses reflection for query
/// translation. Suppressions documented inline.
/// </para>
/// </remarks>
[SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "EF Core data layer accepts JsonSerializer reflection tradeoff; values come from compile-time selector metadata, not runtime type scanning.")]
[SuppressMessage("AOT", "IL2060:MakeGenericMethod can break functionality when AOT compiling", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2070:UnrecognizedReflectionPattern", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2075:UnrecognizedReflectionPattern", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "EF Core data layer inherently uses reflection for query translation")]
internal static class CollectiveSettersRewriter {
  private static readonly JsonSerializerOptions _jsonOptions = new() {
    PropertyNamingPolicy = null, // preserve PascalCase property names — they're the jsonb path
  };

  // Cached MethodInfo for the open-generic EF.Functions.JsonbSet method.
  private static readonly MethodInfo _jsonbSetOpenGeneric =
    typeof(WhizbangJsonDbFunctions).GetMethods()
      .First(m => m.Name == nameof(WhizbangJsonDbFunctions.JsonbSet) && m.IsGenericMethodDefinition);

  /// <summary>
  /// Rewrite the spec setters into a single column-level
  /// <c>SetProperty(r =&gt; r.Data, r =&gt; chainedJsonbSet)</c> shape that
  /// EF Core 10 can hand to <c>ExecuteUpdateAsync</c>.
  /// </summary>
  public static Expression<Action<UpdateSettersBuilder<PerspectiveRow<TModel>>>> Rewrite<TModel>(
      Expression<Action<ICollectiveSetters<TModel>>> source)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(source);

    // Walk the spec body once and accumulate (propertyName, JsonValue) pairs.
    var visitor = new _propertyCollector(typeof(TModel));
    visitor.Visit(source.Body);

    if (visitor.Assignments.Count == 0) {
      throw new InvalidOperationException(
        $"Spec for {typeof(TModel).Name} produced zero SetProperty calls. " +
        "An ICollectiveSpec must mutate at least one property — empty specs are unsupported because they translate to a SQL UPDATE with no SET clause.");
    }

    // Build:
    //   s => s.SetProperty<TModel>(
    //     r => r.Data,
    //     r => EF.Functions.JsonbSet(
    //            EF.Functions.JsonbSet(r.Data, "<path1>", "<jsonValue1>"),
    //            "<path2>", "<jsonValue2>"));
    var settersParam = Expression.Parameter(
      typeof(UpdateSettersBuilder<PerspectiveRow<TModel>>),
      "s");
    var rowParam = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    var dataProperty = typeof(PerspectiveRow<TModel>).GetProperty(nameof(PerspectiveRow<TModel>.Data))!;
    var rowDotData = Expression.Property(rowParam, dataProperty);

    // Innermost is r.Data; each successive Assignment wraps it.
    Expression chained = rowDotData;
    var dbFunctionsParam = Expression.Constant(EF.Functions, typeof(DbFunctions));
    var jsonbSetClosed = _jsonbSetOpenGeneric.MakeGenericMethod(typeof(TModel));
    foreach (var a in visitor.Assignments) {
      chained = Expression.Call(
        method: jsonbSetClosed,
        arguments: [
          dbFunctionsParam,
          chained,
          Expression.Constant(a.PathName, typeof(string)),
          Expression.Constant(a.JsonValue, typeof(string)),
        ]);
    }

    var dataSelector = Expression.Lambda<Func<PerspectiveRow<TModel>, TModel>>(
      rowDotData, rowParam);
    var dataValue = Expression.Lambda<Func<PerspectiveRow<TModel>, TModel>>(
      chained, rowParam);

    // SetProperty<TProperty>(Expression<Func<TSource, TProperty>>, Expression<Func<TSource, TProperty>>)
    var setPropertyMethod = settersParam.Type.GetMethods()
      .Where(m => m.Name == "SetProperty" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
      .First(m => m.GetParameters()[1].ParameterType.IsGenericType &&
                  m.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>))
      .MakeGenericMethod(typeof(TModel));

    var body = Expression.Call(
      settersParam,
      setPropertyMethod,
      Expression.Quote(dataSelector),
      Expression.Quote(dataValue));

    return Expression.Lambda<Action<UpdateSettersBuilder<PerspectiveRow<TModel>>>>(body, settersParam);
  }

  private sealed record _propertyAssignment(string PathName, string JsonValue);

  /// <summary>
  /// Walks the spec body and accumulates one
  /// <see cref="_propertyAssignment"/> per <c>SetProperty</c> call.
  /// Refuses computed expressions and nested paths — those use the
  /// raw-SQL escape hatch.
  /// </summary>
  private sealed class _propertyCollector(Type modelType) : ExpressionVisitor {
    public List<_propertyAssignment> Assignments { get; } = new();

    protected override Expression VisitMethodCall(MethodCallExpression node) {
      if (node.Method.Name != "SetProperty" ||
          node.Method.DeclaringType is not { IsGenericType: true } declaring ||
          declaring.GetGenericTypeDefinition() != typeof(ICollectiveSetters<>) ||
          node.Arguments.Count != 2) {
        return base.VisitMethodCall(node);
      }

      // Visit the receiver first so chained calls are collected in source order.
      if (node.Object is not null) {
        Visit(node.Object);
      }

      var selector = _unwrapLambda(node.Arguments[0]);
      var propertyName = _extractScalarPropertyName(selector);

      // Reject computed expressions — Slice 6 v1.0 supports only
      // constant-value SetProperty, matching the Slice 9 Dapper compiler.
      var valueExpr = node.Arguments[1];
      if (_isLambda(valueExpr)) {
        throw new NotSupportedException(
          $"CollectiveSettersRewriter (Slice 6) does not yet support computed SetProperty (j => j.{propertyName}, j => j.{propertyName} + 1). " +
          "Use [CollectiveApplyFor(SpecKind = CollectiveSpecKind.RawSql)] for computed mutations until the visitor learns the arithmetic shape.");
      }

      var value = _evaluateValue(valueExpr);
      var jsonValue = JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), _jsonOptions);
      Assignments.Add(new _propertyAssignment(propertyName, jsonValue));
      return node;
    }

    private static LambdaExpression _unwrapLambda(Expression e) =>
      e switch {
        UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression inner } => inner,
        LambdaExpression direct => direct,
        _ => throw new InvalidOperationException(
          $"Expected a lambda expression for the SetProperty selector; got {e.NodeType} of type {e.Type}.")
      };

    private static bool _isLambda(Expression e) =>
      e is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression } ||
      e is LambdaExpression;

    private string _extractScalarPropertyName(LambdaExpression selector) {
      var body = selector.Body;
      while (body is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
        body = convert.Operand;
      }
      if (body is MemberExpression { Expression: ParameterExpression, Member: PropertyInfo prop }
          && prop.DeclaringType == modelType) {
        return prop.Name;
      }
      throw new NotSupportedException(
        "CollectiveSettersRewriter (Slice 6) only supports scalar top-level property selectors " +
        "(j => j.PropertyName). Nested paths (j => j.Nested.X), indexed access, or computed " +
        "selectors require [CollectiveApplyFor(SpecKind = CollectiveSpecKind.RawSql)].");
    }

    private static object? _evaluateValue(Expression valueExpr) {
      switch (valueExpr) {
        case ConstantExpression c:
          return c.Value;
        case MemberExpression or UnaryExpression { NodeType: ExpressionType.Convert }:
          // Captured local / field — compile the sub-expression and run it.
          var lambda = Expression.Lambda(valueExpr).Compile();
          return lambda.DynamicInvoke();
        default:
          throw new NotSupportedException(
            $"CollectiveSettersRewriter cannot resolve a value of expression-node kind {valueExpr.NodeType} " +
            "(value sources supported: constant literal, captured local, captured field). " +
            "Use [CollectiveApplyFor(SpecKind = CollectiveSpecKind.RawSql)] for richer value sources.");
      }
    }
  }
}
