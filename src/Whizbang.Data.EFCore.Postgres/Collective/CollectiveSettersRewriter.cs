using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// Translates a perspective's <see cref="ICollectiveSpec{TModel}.Setters"/>
/// LINQ expression (the spec's <c>s =&gt; s.SetProperty(j =&gt; j.X, value)</c>
/// shape) into the corresponding EF Core
/// <see cref="SetPropertyCalls{T}"/> expression
/// (<c>s =&gt; s.SetProperty(r =&gt; r.Data.X, value)</c>) so the adapter
/// can hand it to <c>ExecuteUpdateAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// Walks the source tree once, replacing two pieces:
/// </para>
/// <list type="number">
///   <item><description>The setters parameter (<see cref="ICollectiveSetters{TModel}"/> → <see cref="SetPropertyCalls{T}"/> over <see cref="PerspectiveRow{TModel}"/>)</description></item>
///   <item><description>Each <c>SetProperty</c> call's selector / computed expressions (model-level <c>j =&gt; j.X</c> → row-level <c>r =&gt; r.Data.X</c>)</description></item>
/// </list>
/// <para>
/// Inner selector rewriting reuses the parameter-swap visitor under the
/// covers so the new <c>r</c> parameter consistently substitutes for the
/// old <c>j</c> parameter throughout the body.
/// </para>
/// <para>
/// AOT note: matches the EF Core data-layer pattern of suppressing
/// IL2060 / IL3050 — EF Core inherently uses reflection for query
/// translation. Suppressions documented inline (see
/// <c>PhysicalFieldExpressionVisitor</c> for the established Whizbang
/// pattern).
/// </para>
/// </remarks>
[SuppressMessage("AOT", "IL2060:MakeGenericMethod can break functionality when AOT compiling", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2070:UnrecognizedReflectionPattern", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2075:UnrecognizedReflectionPattern", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "EF Core data layer inherently uses reflection for query translation")]
internal static class CollectiveSettersRewriter {
  /// <summary>
  /// Rewrite the spec setters to operate on <see cref="PerspectiveRow{TModel}"/>
  /// via <see cref="SetPropertyCalls{T}"/>.
  /// </summary>
  public static Expression<Action<UpdateSettersBuilder<PerspectiveRow<TModel>>>> Rewrite<TModel>(
      Expression<Action<ICollectiveSetters<TModel>>> source)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(source);

    var rowParam = Expression.Parameter(
      typeof(UpdateSettersBuilder<PerspectiveRow<TModel>>),
      "s");

    var visitor = new _settersVisitor(
      sourceSettersParam: source.Parameters[0],
      newSettersParam: rowParam,
      modelType: typeof(TModel),
      rowType: typeof(PerspectiveRow<TModel>));

    var newBody = visitor.Visit(source.Body)
      ?? throw new InvalidOperationException("Visitor returned null body");

    return Expression.Lambda<Action<UpdateSettersBuilder<PerspectiveRow<TModel>>>>(newBody, rowParam);
  }

  /// <summary>
  /// Rewrites a model-level selector expression
  /// (<c>j =&gt; j.X</c>) to a row-level selector
  /// (<c>r =&gt; r.Data.X</c>).
  /// </summary>
  internal static LambdaExpression RewriteModelSelectorToRowSelector(
      LambdaExpression selector, Type modelType, Type rowType) {
    var rowParam = Expression.Parameter(rowType, "r");
    var dataProperty = rowType.GetProperty("Data") ?? throw new InvalidOperationException(
      $"PerspectiveRow<TModel> is expected to expose a 'Data' property; reflection failed against {rowType.FullName}.");
    var dataAccess = Expression.Property(rowParam, dataProperty);

    // Substitute the model parameter inside the selector body with `r.Data`.
    var swapper = new _parameterSwapVisitor(selector.Parameters[0], dataAccess);
    var newBody = swapper.Visit(selector.Body)
      ?? throw new InvalidOperationException("Parameter-swap visitor returned null body");

    // Construct Expression<Func<PerspectiveRow<TModel>, TProperty>>.
    var newDelegateType = typeof(Func<,>).MakeGenericType(rowType, selector.ReturnType);
    return Expression.Lambda(newDelegateType, newBody, rowParam);
  }

  private sealed class _settersVisitor : ExpressionVisitor {
    private readonly ParameterExpression _sourceSettersParam;
    private readonly ParameterExpression _newSettersParam;
    private readonly Type _modelType;
    private readonly Type _rowType;

    public _settersVisitor(
      ParameterExpression sourceSettersParam,
      ParameterExpression newSettersParam,
      Type modelType,
      Type rowType) {
      _sourceSettersParam = sourceSettersParam;
      _newSettersParam = newSettersParam;
      _modelType = modelType;
      _rowType = rowType;
    }

    protected override Expression VisitParameter(ParameterExpression node) {
      if (node == _sourceSettersParam) {
        return _newSettersParam;
      }
      return base.VisitParameter(node);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node) {
      // Looking for `<expr>.SetProperty<TProp>(selector, valueOrComputed)`
      // where `<expr>` is anything that yields ICollectiveSetters<TModel> —
      // covers both the top-level call (object == _sourceSettersParam) and
      // chained calls (object is the return of a prior SetProperty).
      if (node.Object is not null &&
          node.Method.Name == "SetProperty" &&
          node.Arguments.Count == 2 &&
          node.Method.DeclaringType is { IsGenericType: true } declaring &&
          declaring.GetGenericTypeDefinition() == typeof(ICollectiveSetters<>)) {

        // Visit the object first so chained calls translate left-to-right.
        var newObject = Visit(node.Object)
          ?? throw new InvalidOperationException("Visitor returned null object on chained SetProperty call");

        // Selector: arg 0 is Expression<Func<TModel, TProp>>; unwrap from any quote.
        var selector = _unwrapLambda(node.Arguments[0]);
        var newSelector = RewriteModelSelectorToRowSelector(selector, _modelType, _rowType);

        // Second arg: either a constant/raw value, or another lambda (computed value).
        Expression newSecondArg;
        if (_tryUnwrapLambda(node.Arguments[1], out var computed)) {
          var newComputed = RewriteModelSelectorToRowSelector(computed, _modelType, _rowType);
          newSecondArg = Expression.Quote(newComputed);
        } else {
          // Visit constants and references in case they reference outer captures.
          newSecondArg = Visit(node.Arguments[1]) ?? node.Arguments[1];
        }

        // Find the matching SetProperty method on SetPropertyCalls<PerspectiveRow<TModel>>.
        var tProp = selector.ReturnType;
        var newMethod = _findSetPropertyMethod(newSecondArg, tProp);

        return Expression.Call(
          newObject,
          newMethod,
          Expression.Quote(newSelector),
          newSecondArg);
      }
      return base.VisitMethodCall(node);
    }

    private MethodInfo _findSetPropertyMethod(Expression secondArg, Type tProp) {
      // SetPropertyCalls<T> exposes two SetProperty<TProperty> overloads:
      //  - (Expression<Func<T, TProperty>>, TProperty value)
      //  - (Expression<Func<T, TProperty>>, Expression<Func<T, TProperty>> computed)
      // Pick the right one by inspecting secondArg's resulting type.
      var spcType = _newSettersParam.Type;
      var candidates = spcType.GetMethods()
        .Where(m => m.Name == "SetProperty" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
        .ToArray();

      // Decide: if secondArg is a Quote (UnaryExpression wrapping a lambda) or
      // its declared type is an Expression<Func<,>>, we want the computed
      // overload; otherwise the constant overload.
      var isComputed = secondArg is UnaryExpression { NodeType: ExpressionType.Quote } ||
                       (secondArg.Type.IsGenericType && secondArg.Type.GetGenericTypeDefinition() == typeof(Expression<>));

      foreach (var candidate in candidates) {
        var ps = candidate.GetParameters();
        var p1IsExpression = ps[1].ParameterType.IsGenericType &&
                             ps[1].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>);
        if (p1IsExpression == isComputed) {
          return candidate.MakeGenericMethod(tProp);
        }
      }

      throw new InvalidOperationException(
        $"Could not find SetProperty<{tProp.Name}> overload on {spcType.Name} matching {(isComputed ? "computed" : "constant")} second-argument shape.");
    }

    private static LambdaExpression _unwrapLambda(Expression e) =>
      e switch {
        UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression inner } => inner,
        LambdaExpression direct => direct,
        _ => throw new InvalidOperationException(
          $"Expected a lambda (selector) expression; got {e.NodeType} of type {e.Type}. The spec's SetProperty calls must pass a property-selector lambda as the first argument.")
      };

    private static bool _tryUnwrapLambda(Expression e, out LambdaExpression lambda) {
      switch (e) {
        case UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression inner }:
          lambda = inner;
          return true;
        case LambdaExpression direct:
          lambda = direct;
          return true;
        default:
          lambda = null!;
          return false;
      }
    }
  }

  private sealed class _parameterSwapVisitor : ExpressionVisitor {
    private readonly ParameterExpression _from;
    private readonly Expression _to;

    public _parameterSwapVisitor(ParameterExpression from, Expression to) {
      _from = from;
      _to = to;
    }

    protected override Expression VisitParameter(ParameterExpression node) =>
      node == _from ? _to : base.VisitParameter(node);
  }
}
