using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// Walks a perspective's <see cref="ICollectiveSpec{TModel}.Setters"/> LINQ
/// expression and returns each top-level property assignment pre-serialized
/// to the exact JSON text the jsonb column stores, so the adapter can build
/// a single nested <c>jsonb_set(data, '{Prop}', @value::jsonb)</c> UPDATE
/// that mutates the <c>Data</c> column's sub-properties directly.
/// </summary>
/// <remarks>
/// <para>
/// The adapter (<see cref="EFCoreCollectiveAdapter{TModel}"/>) drives every
/// mapping — complex-JSON, scalar/polymorphic, and null setters — through one
/// raw <c>jsonb_set</c> path, so the rewriter's only job is to hand back the
/// serialized <c>(PathName, JsonValue, IsNull)</c> triples. Values are
/// serialized with the <see cref="SerializationProfile.Persistence"/> profile
/// so they match byte-for-byte how the column stores them (e.g. WhizbangId
/// object-mode, plain Guid as a JSON string, null as JSON null).
/// </para>
/// <para>
/// Constraint matrix:
/// </para>
/// <list type="bullet">
///   <item><description>Scalar top-level <c>SetProperty(j =&gt; j.PropName, constant)</c> — supported.</description></item>
///   <item><description>Constant value sources (literal, captured local, captured field) — supported.</description></item>
///   <item><description>Multiple chained <c>SetProperty</c> calls — collected in source order.</description></item>
///   <item><description>Computed expressions (<c>j =&gt; j.X + 1</c>) — UNSUPPORTED in v1.0; throws <see cref="NotSupportedException"/> with a pointer to <c>SpecKind = RawSql</c>. Matches the Dapper compiler's constraint matrix.</description></item>
///   <item><description>Nested paths (<c>j =&gt; j.Nested.X</c>) — UNSUPPORTED in v1.0; throws <see cref="NotSupportedException"/>.</description></item>
/// </list>
/// <para>
/// AOT note: value evaluation compiles a captured sub-expression
/// (<c>Expression.Lambda(...).Compile()</c>) and serializes via
/// <see cref="JsonSerializer"/>, so IL2026 / IL3050 are suppressed — the
/// values come from compile-time selector metadata, not runtime type
/// scanning. Suppressions documented inline.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
[SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Values come from compile-time selector metadata serialized via the persistence JSON context, not runtime type scanning.")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Captured value sub-expressions are compiled from spec setter metadata known at build time, not synthesized at runtime.")]
internal static class CollectiveSettersRewriter {
  // Persistence-profile options so a value serializes byte-for-byte the way the jsonb column stores it
  // (e.g. WhizbangId object-mode, plain Guid as a JSON string, null as JSON null).
  private static readonly JsonSerializerOptions _persistenceJsonOptions =
    JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);

  /// <summary>
  /// A single property assignment from the spec, with its value pre-serialized to the JSON text the jsonb
  /// column stores. Feeds the adapter's nested <c>jsonb_set(data, '{PathName}', @value::jsonb)</c> UPDATE;
  /// <see cref="IsNull"/> is carried so a null setter serializes to JSON <c>null</c> (which EF Core 10
  /// cannot express against a <c>ComplexProperty().ToJson()</c> sub-property via <c>ExecuteUpdate</c>).
  /// </summary>
  public sealed record CollectiveSetterAssignment(string PathName, string JsonValue, bool IsNull, CollectiveComputedComparison? Comparison = null);

  /// <summary>
  /// A computed setter of the shape <c>j =&gt; j.SomeProp == value</c> (or <c>!=</c>): the new value is a
  /// jsonb-to-jsonb comparison of the row's <see cref="ComparedProperty"/> against the assignment's
  /// (constant) <c>JsonValue</c>, wrapped by the adapter as
  /// <c>to_jsonb((data-&gt;'ComparedProperty')::jsonb &lt;op&gt; @value::jsonb)</c>. Null for constant setters.
  /// </summary>
  public sealed record CollectiveComputedComparison(string ComparedProperty, string SqlOperator);

  /// <summary>
  /// Walk the spec setters and return each top-level assignment as (property name, JSON-serialized value,
  /// is-null), per the class-level constraint matrix (constant values, scalar top-level selectors).
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
      // For a computed comparison the serialized value is the RHS constant; the adapter wraps it in the
      // to_jsonb(...) comparison. For a constant setter it is the value itself. Serialize against the RHS's own
      // runtime type (comparison) or the target property type (constant).
      var valueType = a.Value?.GetType() ?? (a.Comparison is null ? a.Property.PropertyType : typeof(object));
      var json = JsonSerializer.Serialize(a.Value, valueType, _persistenceJsonOptions);
      result.Add(new CollectiveSetterAssignment(a.Property.Name, json, a.Value is null && a.Comparison is null, a.Comparison));
    }
    return result;
  }

  /// <summary>
  /// Turn collective apply-hook <see cref="SetPropertyOp"/>s (property name + already-evaluated value) into the
  /// same <see cref="CollectiveSetterAssignment"/> shape the spec setters produce, serialized with the identical
  /// persistence profile so a hook-set field is byte-for-byte consistent with a spec-set one. Constant setters
  /// only — hooks never carry computed comparisons.
  /// </summary>
  public static IReadOnlyList<CollectiveSetterAssignment> FromHookSetters(IEnumerable<SetPropertyOp> setters) {
    ArgumentNullException.ThrowIfNull(setters);
    var result = new List<CollectiveSetterAssignment>();
    foreach (var setter in setters) {
      var valueType = setter.Value?.GetType() ?? setter.PropertyType;
      var json = JsonSerializer.Serialize(setter.Value, valueType, _persistenceJsonOptions);
      result.Add(new CollectiveSetterAssignment(setter.PropertyName, json, setter.Value is null, Comparison: null));
    }
    return result;
  }

  private sealed record _propertyAssignment(PropertyInfo Property, object? Value, CollectiveComputedComparison? Comparison);

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

      // Computed setter: currently a property-vs-constant comparison (j => j.SomeProp == value). The RHS
      // constant becomes the assignment value; the comparison metadata tells the adapter to emit the
      // to_jsonb((data->'X')::jsonb <op> @value::jsonb) shape. Arithmetic / string ops remain RawSql-only.
      var valueExpr = node.Arguments[1];
      if (_isLambda(valueExpr)) {
        var (comparison, rhs) = _parseComputedComparison(valueExpr, property.Name);
        Assignments.Add(new _propertyAssignment(property, rhs, comparison));
        return node;
      }

      Assignments.Add(new _propertyAssignment(property, _evaluateValue(valueExpr), Comparison: null));
      return node;
    }

    private (CollectiveComputedComparison Comparison, object? Rhs) _parseComputedComparison(Expression valueExpr, string targetProperty) {
      var lambda = _unwrapLambda(valueExpr);
      if (lambda.Body is BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } bin
          && _tryComparedProperty(bin.Left) is { } comparedProperty) {
        var op = bin.NodeType == ExpressionType.Equal ? "=" : "<>";
        return (new CollectiveComputedComparison(comparedProperty, op), _evaluateValue(_stripConvert(bin.Right)));
      }
      throw new NotSupportedException(
        $"CollectiveSettersRewriter supports computed SetProperty only as a property-vs-constant comparison " +
        $"(j => j.{targetProperty}, j => j.SomeProp == value). Arithmetic, string, and other computed shapes require [CollectiveApplyFor(SpecKind = CollectiveSpecKind.RawSql)].");
    }

    private string? _tryComparedProperty(Expression e) =>
      _stripConvert(e) is MemberExpression { Expression: ParameterExpression, Member: PropertyInfo prop }
          && prop.DeclaringType == modelType
        ? prop.Name
        : null;

    private static Expression _stripConvert(Expression e) {
      while (e is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
        e = convert.Operand;
      }
      return e;
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
