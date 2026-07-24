using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;

namespace Whizbang.Data.Dapper.Postgres.Collective;

/// <summary>
/// Compiles a perspective's <see cref="ICollectiveSpec{TModel}.Setters"/>
/// LINQ expression into a Postgres SQL <c>SET</c> clause + parameter
/// dictionary suitable for handing to Dapper / Npgsql. The Dapper
/// adapter (Slice 9 follow-up) composes this output with scope-filter
/// SQL + matched-id membership to form the final UPDATE statement.
/// </summary>
/// <remarks>
/// <para>
/// Whizbang perspectives are stored as <c>jsonb</c> in the <c>data</c>
/// column, so every <c>SetProperty(j =&gt; j.X, value)</c> call translates
/// to a <c>jsonb_set</c> mutation: <c>data = jsonb_set(data, '{X}', @p_X)</c>.
/// Multiple <c>SetProperty</c> calls chain via nested <c>jsonb_set</c>:
/// </para>
/// <code>
/// data = jsonb_set(
///          jsonb_set(data, '{A}', @p_A),
///          '{B}', @p_B)
/// </code>
/// <para>
/// <strong>Supported (first cut):</strong>
/// </para>
/// <list type="bullet">
///   <item><description>Scalar <c>SetProperty(j =&gt; j.PropName, constant)</c> — single top-level model property + constant value.</description></item>
///   <item><description>Constant value sources (literal, captured local, captured field).</description></item>
///   <item><description>Multiple chained <c>SetProperty</c> calls — composed as nested <c>jsonb_set</c>.</description></item>
/// </list>
/// <para>
/// <strong>Unsupported (use <c>[CollectiveApplyFor(SpecKind = RawSql)]</c> instead):</strong>
/// </para>
/// <list type="bullet">
///   <item><description>Computed expressions (<c>j =&gt; j.X + 1</c>) — Slice 9 follow-up; current shape throws <see cref="NotSupportedException"/>.</description></item>
///   <item><description>Nested property paths (<c>j =&gt; j.Nested.X</c>) — same.</description></item>
///   <item><description>Conditional / arithmetic / collection mutations.</description></item>
/// </list>
/// <para>
/// <strong>AOT story.</strong> Walks the user-supplied expression tree
/// with <see cref="ExpressionVisitor"/> — no runtime reflection on
/// <typeparamref name="TModel"/>. Property names are recovered from the
/// expression's <see cref="MemberExpression.Member"/> (compile-time
/// metadata, not <c>type.GetProperty(...)</c> reflection). Values are
/// JSON-serialized via <see cref="JsonSerializer.Serialize{TValue}(TValue, JsonSerializerOptions?)"/>
/// — the caller picks the options to satisfy AOT trim warnings.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The perspective model the collective event mutates.</typeparam>
/// <docs>fundamentals/messaging/collective-events</docs>
[SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Whizbang.Data.Dapper.Postgres layer accepts the existing JsonSerializer reflection tradeoff; callers supply AOT-safe options.")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Whizbang.Data.Dapper.Postgres layer accepts the existing JsonSerializer reflection tradeoff; callers supply AOT-safe options.")]
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Matches the Whizbang.Data.EFCore.Postgres pattern of generic-over-TModel static helpers (EFCoreCollectiveAdapter, CollectiveEventApplier).")]
public static class DapperCollectiveSpecCompiler<TModel> where TModel : class {
  /// <summary>
  /// Compiled SQL artifact: a <c>SET</c>-clause fragment and the named
  /// parameter dictionary it binds. The fragment is a single
  /// expression intended to substitute the entire <c>SET</c> body —
  /// callers prepend their own <c>UPDATE … SET </c> prefix and append
  /// any additional column writes (e.g. <c>last_collective_event_id =
  /// @evt_id</c>) and the <c>WHERE</c> clause.
  /// </summary>
  public sealed record CompiledSetClause(
    string SqlFragment,
    IReadOnlyDictionary<string, object?> Parameters
  );

  /// <summary>
  /// Compile a spec to a SQL <c>SET</c> clause that targets the
  /// <c>data</c> jsonb column. Parameter names are namespaced by
  /// <paramref name="parameterPrefix"/> to avoid collisions when the
  /// caller is composing the fragment into a larger statement.
  /// </summary>
  public static CompiledSetClause Compile(
      ICollectiveSpec<TModel> spec,
      JsonSerializerOptions jsonOptions,
      string parameterPrefix = "set",
      IEnumerable<SetPropertyOp>? hookSetters = null,
      IReadOnlySet<string>? removedFields = null) {
    ArgumentNullException.ThrowIfNull(spec);
    ArgumentNullException.ThrowIfNull(jsonOptions);
    ArgumentException.ThrowIfNullOrWhiteSpace(parameterPrefix);

    var visitor = new _setterVisitor(jsonOptions, parameterPrefix);
    visitor.Visit(spec.Setters.Body);

    if (visitor.Properties.Count == 0) {
      throw new InvalidOperationException(
        $"Spec for {typeof(TModel).Name} produced zero SetProperty calls. " +
        "An ICollectiveSpec must mutate at least one property — empty specs are unsupported because they translate to a SQL UPDATE with no SET clause.");
    }

    // Fold in the collective apply-hook contributions: append any hook-added constant setters, then drop any
    // field a hook removed. Kept after the spec so hook writes win on the same jsonb path (nested jsonb_set).
    if (hookSetters is not null) {
      foreach (var setter in hookSetters) {
        visitor.AddConstant(setter.PropertyName, setter.Value);
      }
    }
    var properties = removedFields is { Count: > 0 }
      ? visitor.Properties.Where(p => !removedFields.Contains(p.JsonbPath)).ToList()
      : visitor.Properties;

    return new CompiledSetClause(
      SqlFragment: _buildJsonbSetChain(properties),
      Parameters: visitor.Parameters);
  }

  /// <summary>
  /// Build the nested <c>jsonb_set</c> chain. Innermost is the original
  /// <c>data</c> column; each successive <c>SetProperty</c> wraps with
  /// another <c>jsonb_set</c>.
  /// </summary>
  private static string _buildJsonbSetChain(List<_propertyAssignment> assignments) {
    // jsonb_set(jsonb_set(data, '{A}', @p_A::jsonb), '{B}', to_jsonb((data->'X')::jsonb = @p_B::jsonb))
    var sb = new StringBuilder("data = ");
    foreach (var _ in assignments) {
      sb.Append("jsonb_set(");
    }
    sb.Append("data");
    foreach (var a in assignments) {
      sb.Append(", '{");
      sb.Append(a.JsonbPath);
      sb.Append("}', ");
      sb.Append(a.ValueSql);
      sb.Append(')');
    }
    return sb.ToString();
  }

  // ValueSql is the SQL for the new jsonb value: "@p::jsonb" for a constant, or a computed expression like
  // "to_jsonb((data->'X')::jsonb = @p::jsonb)" for a property-vs-constant comparison.
  private sealed record _propertyAssignment(string JsonbPath, string ValueSql);

  /// <summary>
  /// Walks the spec's expression body, collecting one
  /// <see cref="_propertyAssignment"/> per <c>SetProperty</c> call.
  /// </summary>
  private sealed class _setterVisitor : ExpressionVisitor {
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _parameterPrefix;
    private int _seq;
    public List<_propertyAssignment> Properties { get; } = new();
    public Dictionary<string, object?> Parameters { get; } = new(StringComparer.Ordinal);

    public _setterVisitor(JsonSerializerOptions jsonOptions, string parameterPrefix) {
      _jsonOptions = jsonOptions;
      _parameterPrefix = parameterPrefix;
    }

    protected override Expression VisitMethodCall(MethodCallExpression node) {
      // Match: ICollectiveSetters<TModel>.SetProperty<TProp>(selector, value)
      if (node.Method.DeclaringType is { IsGenericType: true } declaring &&
          declaring.GetGenericTypeDefinition() == typeof(ICollectiveSetters<>) &&
          node.Method.Name == "SetProperty" &&
          node.Arguments.Count == 2) {

        var selector = _unwrapLambda(node.Arguments[0]);
        var propertyName = _extractScalarPropertyName(selector);

        var valueExpr = node.Arguments[1];
        var valueSql = _isLambda(valueExpr)
          ? _compileComputedValue(valueExpr, propertyName)
          : _compileConstantValue(valueExpr, propertyName);
        Properties.Add(new _propertyAssignment(propertyName, valueSql));

        // Continue visiting in case this is part of a chain — the visitor
        // call below recurses into the Object expression of the next
        // chained call.
        if (node.Object is not null) {
          Visit(node.Object);
        }
        return node;
      }

      return base.VisitMethodCall(node);
    }

    private static LambdaExpression _unwrapLambda(Expression e) =>
      e switch {
        UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression inner } => inner,
        LambdaExpression direct => direct,
        _ => throw new InvalidOperationException(
          $"Expected a lambda expression for the SetProperty selector; got {e.NodeType} of type {e.Type}. The spec's SetProperty calls must pass a property-selector lambda as the first argument.")
      };

    private static bool _isLambda(Expression e) =>
      e is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression } ||
      e is LambdaExpression;

    private static string _extractScalarPropertyName(LambdaExpression selector) {
      // Strip Convert(s) wrappers (boxing of value types) so the
      // underlying MemberAccess shows through.
      var body = selector.Body;
      while (body is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
        body = convert.Operand;
      }
      if (body is MemberExpression { Expression: ParameterExpression, Member: PropertyInfo prop }) {
        return prop.Name;
      }
      throw new NotSupportedException(
        "DapperCollectiveSpecCompiler only supports scalar top-level property selectors " +
        "(j => j.PropertyName). Nested paths (j => j.Nested.X), indexed access, or computed " +
        "selectors require [CollectiveApplyFor(SpecKind = CollectiveSpecKind.RawSql)].");
    }

    // Append a collective apply-hook constant setter: a pre-evaluated value (not an expression) bound as
    // "@p::jsonb" on the given jsonb path. Mirrors _compileConstantValue but starts from the raw value.
    public void AddConstant(string propertyName, object? value) {
      var paramName = _nextParam(propertyName);
      Parameters[paramName] = JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), _jsonOptions);
      Properties.Add(new _propertyAssignment(propertyName, $"@{paramName}::jsonb"));
    }

    // Constant value source → "@p::jsonb", with the value JSON-serialized into the parameter dictionary.
    private string _compileConstantValue(Expression valueExpr, string propertyName) {
      var value = _evaluateValue(valueExpr);
      var paramName = _nextParam(propertyName);
      Parameters[paramName] = JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), _jsonOptions);
      return $"@{paramName}::jsonb";
    }

    // Computed value source. Supported shape: a property-vs-constant comparison (j => j.SomeProp == value),
    // which compiles to a jsonb-to-jsonb comparison wrapped in to_jsonb() so the result is itself a jsonb
    // boolean. Arithmetic, string, and other computed shapes remain RawSql-only.
    private string _compileComputedValue(Expression valueExpr, string targetProperty) {
      var lambda = _unwrapLambda(valueExpr);
      if (lambda.Body is BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } bin
          && _tryPropertyName(bin.Left) is { } comparedProperty) {
        var rhs = _evaluateValue(_stripConvert(bin.Right));
        var paramName = _nextParam(targetProperty);
        Parameters[paramName] = JsonSerializer.Serialize(rhs, rhs?.GetType() ?? typeof(object), _jsonOptions);
        var op = bin.NodeType == ExpressionType.Equal ? "=" : "<>";
        // (data->'X') is already jsonb; the ::jsonb cast is explicit for readability + a stable SQL shape.
        return $"to_jsonb((data->'{comparedProperty}')::jsonb {op} @{paramName}::jsonb)";
      }

      throw new NotSupportedException(
        $"DapperCollectiveSpecCompiler<{typeof(TModel).Name}> supports computed SetProperty only as a property-vs-constant comparison " +
        $"(j => j.{targetProperty}, j => j.SomeProp == value). Arithmetic, string, and other computed shapes require [CollectiveApplyFor(SpecKind = CollectiveSpecKind.RawSql)].");
    }

    private string _nextParam(string propertyName) {
      var paramName = $"{_parameterPrefix}_{_seq}_{propertyName.ToLowerInvariant()}";
      _seq++;
      return paramName;
    }

    private static string? _tryPropertyName(Expression e) =>
      _stripConvert(e) is MemberExpression { Expression: ParameterExpression, Member: PropertyInfo prop }
        ? prop.Name
        : null;

    private static Expression _stripConvert(Expression e) {
      while (e is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
        e = convert.Operand;
      }
      return e;
    }

    private static object? _evaluateValue(Expression valueExpr) {
      switch (valueExpr) {
        case ConstantExpression c:
          return c.Value;
        case MemberExpression or UnaryExpression { NodeType: ExpressionType.Convert }:
          // Captured field/local — compile the sub-expression and run it.
          // Cost is one delegate compile per spec, amortized across the
          // (presumed-large) matched set.
          var lambda = Expression.Lambda(valueExpr).Compile();
          return lambda.DynamicInvoke();
        default:
          throw new NotSupportedException(
            $"DapperCollectiveSpecCompiler cannot resolve a value of expression-node kind {valueExpr.NodeType} " +
            "(value sources supported in this slice: constant literal, captured local, captured field). " +
            "Use [CollectiveApplyFor(SpecKind = CollectiveSpecKind.RawSql)] for richer value sources.");
      }
    }
  }
}
