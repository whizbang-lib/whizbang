using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Query;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// Translates a perspective's <see cref="ICollectiveSpec{TModel}.Setters"/>
/// LINQ expression into the shape EF Core's
/// <see cref="UpdateSettersBuilder{T}"/> expects, emitting one native
/// nested <c>SetProperty(r =&gt; r.Data.&lt;Prop&gt;, value)</c> call per
/// mutated property so the resulting <c>ExecuteUpdateAsync</c> updates the
/// JSON sub-properties of the <c>Data</c> column directly.
/// </summary>
/// <remarks>
/// <para>
/// The turnkey perspective mapping declares <c>Data</c> as an EF Core 10
/// <c>ComplexProperty().ToJson()</c> complex type (see the generated
/// configuration in <c>EFCoreServiceRegistrationGenerator</c>), not a
/// scalar <c>jsonb</c> column. Against that mapping EF Core 10 natively
/// supports <c>SetProperty(r =&gt; r.Data.PropName, value)</c> and emits the
/// appropriate <c>jsonb_set</c> SQL itself — so the rewriter simply
/// re-targets each model-level setter (<c>j =&gt; j.PropName</c>) onto the
/// row's <c>Data</c> sub-property and lets EF's own JSON-update pipeline do
/// the work. No custom <c>jsonb_set</c> translator and no raw SQL strings.
/// </para>
/// <para>
/// (An earlier revision folded every mutation into a single column-level
/// <c>SetProperty(r =&gt; r.Data, r =&gt; jsonb_set(...))</c> via a custom
/// translator. That assumed <c>Data</c> was a scalar <c>jsonb</c> column;
/// it threw "does not represent a valid value" against the real
/// <c>ComplexProperty().ToJson()</c> mapping because <c>r.Data</c> does not
/// translate to a scalar column. The native nested form is both simpler and
/// correct for the production mapping.)
/// </para>
/// <para>
/// Constraint matrix:
/// </para>
/// <list type="bullet">
///   <item><description>Scalar top-level <c>SetProperty(j =&gt; j.PropName, constant)</c> — supported.</description></item>
///   <item><description>Constant value sources (literal, captured local, captured field) — supported.</description></item>
///   <item><description>Multiple chained <c>SetProperty</c> calls — emitted as a fluent chain of native <c>SetProperty</c> calls.</description></item>
///   <item><description>Computed expressions (<c>j =&gt; j.X + 1</c>) — UNSUPPORTED in v1.0; throws <see cref="NotSupportedException"/> with a pointer to <c>SpecKind = RawSql</c>. Matches the Slice 9 Dapper compiler's constraint matrix.</description></item>
///   <item><description>Nested paths (<c>j =&gt; j.Nested.X</c>) — UNSUPPORTED in v1.0; throws <see cref="NotSupportedException"/>.</description></item>
/// </list>
/// <para>
/// AOT note: matches the EF Core data-layer pattern of suppressing
/// IL2060 / IL3050 — EF Core inherently uses reflection for query
/// translation. Suppressions documented inline.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
[SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "EF Core data layer accepts reflection tradeoff; values come from compile-time selector metadata, not runtime type scanning.")]
[SuppressMessage("AOT", "IL2060:MakeGenericMethod can break functionality when AOT compiling", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2070:UnrecognizedReflectionPattern", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2075:UnrecognizedReflectionPattern", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "EF Core data layer inherently uses reflection for query translation")]
internal static class CollectiveSettersRewriter {
  /// <summary>
  /// Rewrite the spec setters into a fluent chain of native
  /// <c>SetProperty(r =&gt; r.Data.&lt;Prop&gt;, value)</c> calls that
  /// EF Core 10 can hand to <c>ExecuteUpdateAsync</c>.
  /// </summary>
  public static Expression<Action<UpdateSettersBuilder<PerspectiveRow<TModel>>>> Rewrite<TModel>(
      Expression<Action<ICollectiveSetters<TModel>>> source)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(source);

    // Walk the spec body once and accumulate (property, value) pairs.
    var visitor = new _propertyCollector(typeof(TModel));
    visitor.Visit(source.Body);

    if (visitor.Assignments.Count == 0) {
      throw new InvalidOperationException(
        $"Spec for {typeof(TModel).Name} produced zero SetProperty calls. " +
        "An ICollectiveSpec must mutate at least one property — empty specs are unsupported because they translate to a SQL UPDATE with no SET clause.");
    }

    // Build:
    //   s => s.SetProperty(r => r.Data.<Prop1>, value1)
    //         .SetProperty(r => r.Data.<Prop2>, value2)
    var settersParam = Expression.Parameter(
      typeof(UpdateSettersBuilder<PerspectiveRow<TModel>>),
      "s");
    var rowParam = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    var dataProperty = typeof(PerspectiveRow<TModel>).GetProperty(nameof(PerspectiveRow<TModel>.Data))!;
    var rowDotData = Expression.Property(rowParam, dataProperty);

    // The (Expression<Func<TSource,TProperty>> propertyExpression, TProperty value) overload — the second
    // argument is the bare constant value, not a value selector. EF Core embeds it as a parameter and emits
    // the JSON-column update itself. (The sibling overload takes a value *selector* and trips EF's coercion
    // validation for nullable JSON sub-properties; the bare-value form is both simpler and correct.)
    var setPropertyValueOverload = settersParam.Type.GetMethods()
      .Where(m => m.Name == "SetProperty" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
      .First(m => {
        var ps = m.GetParameters();
        return ps[0].ParameterType.IsGenericType
            && ps[0].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>)
            && ps[1].ParameterType.IsGenericMethodParameter;
      });

    Expression chain = settersParam;
    foreach (var a in visitor.Assignments) {
      var propType = a.Property.PropertyType;
      var selectorFuncType = typeof(Func<,>).MakeGenericType(typeof(PerspectiveRow<TModel>), propType);

      // r => r.Data.<Prop>
      var memberAccess = Expression.Property(rowDotData, a.Property);
      var propSelector = Expression.Lambda(selectorFuncType, memberAccess, rowParam);

      // The bare value, typed to the property type (handles nullable widening, e.g. DateTimeOffset -> DateTimeOffset?).
      Expression valueArg = a.Value is null
        ? Expression.Constant(null, propType)
        : Expression.Convert(Expression.Constant(a.Value), propType);

      var setProperty = setPropertyValueOverload.MakeGenericMethod(propType);
      chain = Expression.Call(
        chain,
        setProperty,
        Expression.Quote(propSelector),
        valueArg);
    }

    // The fluent chain returns UpdateSettersBuilder<...>; the Action lambda discards it.
    return Expression.Lambda<Action<UpdateSettersBuilder<PerspectiveRow<TModel>>>>(chain, settersParam);
  }

  // Persistence-profile options so a value serializes byte-for-byte the way the jsonb column stores it
  // (e.g. WhizbangId object-mode, plain Guid as a JSON string, null as JSON null).
  private static readonly JsonSerializerOptions _persistenceJsonOptions =
    JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);

  /// <summary>
  /// A single property assignment from the spec, with its value pre-serialized to the JSON text the jsonb
  /// column stores. Used by the raw-SQL <c>jsonb_set</c> path the adapter falls back to when a setter value
  /// is null — EF Core 10 cannot set a <c>ComplexProperty().ToJson()</c> sub-property to null via
  /// <c>ExecuteUpdate</c>.
  /// </summary>
  public sealed record CollectiveSetterAssignment(string PathName, string JsonValue, bool IsNull);

  /// <summary>
  /// Walk the spec setters and return each top-level assignment as (property name, JSON-serialized value,
  /// is-null). Same constraint matrix as <see cref="Rewrite"/> (constant values, scalar top-level selectors).
  /// </summary>
  public static IReadOnlyList<CollectiveSetterAssignment> CollectAssignments<TModel>(
      Expression<Action<ICollectiveSetters<TModel>>> source)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(source);

    var visitor = new _propertyCollector(typeof(TModel));
    visitor.Visit(source.Body);

    if (visitor.Assignments.Count == 0) {
      throw new InvalidOperationException(
        $"Spec for {typeof(TModel).Name} produced zero SetProperty calls. " +
        "An ICollectiveSpec must mutate at least one property.");
    }

    var result = new List<CollectiveSetterAssignment>(visitor.Assignments.Count);
    foreach (var a in visitor.Assignments) {
      var json = JsonSerializer.Serialize(a.Value, a.Value?.GetType() ?? a.Property.PropertyType, _persistenceJsonOptions);
      result.Add(new CollectiveSetterAssignment(a.Property.Name, json, a.Value is null));
    }
    return result;
  }

  private sealed record _propertyAssignment(PropertyInfo Property, object? Value);

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
      var property = _extractScalarProperty(selector);

      // Reject computed expressions — v1.0 supports only constant-value
      // SetProperty, matching the Dapper compiler.
      var valueExpr = node.Arguments[1];
      if (_isLambda(valueExpr)) {
        throw new NotSupportedException(
          $"CollectiveSettersRewriter does not yet support computed SetProperty (j => j.{property.Name}, j => j.{property.Name} + 1). " +
          "Use [CollectiveApplyFor(SpecKind = CollectiveSpecKind.RawSql)] for computed mutations until the visitor learns the arithmetic shape.");
      }

      Assignments.Add(new _propertyAssignment(property, _evaluateValue(valueExpr)));
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

    private PropertyInfo _extractScalarProperty(LambdaExpression selector) {
      var body = selector.Body;
      while (body is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
        body = convert.Operand;
      }
      if (body is MemberExpression { Expression: ParameterExpression, Member: PropertyInfo prop }
          && prop.DeclaringType == modelType) {
        return prop;
      }
      throw new NotSupportedException(
        "CollectiveSettersRewriter only supports scalar top-level property selectors " +
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
