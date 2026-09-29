using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Postgres.Collective;

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
/// A setter whose target is a <c>[PhysicalField]</c> (per the generator-emitted
/// <see cref="PerspectivePhysicalFieldRegistry"/>) assigns the column as a typed parameter, and assigns the
/// document path as well only when the model's storage mode keeps the field in both places. A spec that touches
/// only physical-only fields produces no <c>data =</c> assignment at all (see <see cref="CollectivePhysicalColumns"/>).
/// </para>
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
  /// parameter dictionary it binds. The fragment is the comma-separated
  /// assignment list (<c>data = …</c> and/or physical columns) intended to substitute the entire <c>SET</c> body —
  /// callers prepend their own <c>UPDATE … SET </c> prefix and append
  /// any additional column writes (e.g. <code>last_collective_event_id =
  /// @evt_id</code>) and the <c>WHERE</c> clause.
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

    var visitor = new SetterVisitor(jsonOptions, parameterPrefix);
    visitor.Visit(spec.Setters.Body);

    if (visitor.Properties.Count == 0 && visitor.Columns.Count == 0) {
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
    var removed = removedFields ?? new HashSet<string>(StringComparer.Ordinal);
    var properties = visitor.Properties.Where(p => !removed.Contains(p.JsonbPath)).ToList();
    var columns = visitor.Columns.Where(c => !removed.Contains(c.PropertyName)).Select(c => (c.Column, c.ValueSql)).ToList();

    return new CompiledSetClause(
      SqlFragment: CollectivePhysicalColumns.RenderSetList(properties.Count > 0 ? _buildJsonbSetChain(properties) : null, columns),
      Parameters: visitor.Parameters);
  }

  /// <summary>
  /// Build the nested <c>jsonb_set</c> chain (the new document expression). Innermost is the original
  /// <c>data</c> column; each successive <c>SetProperty</c> wraps with
  /// another <c>jsonb_set</c>.
  /// </summary>
  private static string _buildJsonbSetChain(List<PropertyAssignment> assignments) {
    // jsonb_set(jsonb_set(data, '{A}', @p_A::jsonb), '{B}', to_jsonb((data->'X')::jsonb = @p_B::jsonb))
    var sb = new StringBuilder();
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
  private sealed record PropertyAssignment(string JsonbPath, string ValueSql);

  // A physical-column write: the model property (for hook removal), the column, and the SQL for its new value.
  private sealed record ColumnAssignment(string PropertyName, string Column, string ValueSql);

  /// <summary>
  /// Walks the spec's expression body, collecting one
  /// <see cref="PropertyAssignment"/> per <c>SetProperty</c> call.
  /// </summary>
  private sealed class SetterVisitor(JsonSerializerOptions jsonOptions, string parameterPrefix) : ExpressionVisitor {
    private readonly JsonSerializerOptions _jsonOptions = jsonOptions;
    private readonly string _parameterPrefix = parameterPrefix;
    private int _seq;
    public List<PropertyAssignment> Properties { get; } = [];
    public List<ColumnAssignment> Columns { get; } = [];
    public Dictionary<string, object?> Parameters { get; } = new(StringComparer.Ordinal);

    protected override Expression VisitMethodCall(MethodCallExpression node) {
      // Match: ICollectiveSetters<TModel>.UpsertElement<TElement, TKey>(collection, key, element). The array is
      // rewritten in place by the shared upsert expression, so it rides the same jsonb_set chain.
      if (node.Method.DeclaringType is { IsGenericType: true } upsertDeclaring &&
          upsertDeclaring.GetGenericTypeDefinition() == typeof(ICollectiveSetters<>) &&
          node.Method.Name == "UpsertElement" &&
          node.Arguments.Count == 3) {
        var collectionProperty = _extractScalarProperty(_unwrapLambda(node.Arguments[0]));
        var collection = collectionProperty.Name;
        var target = _physical(collectionProperty);
        if (target is { } keyedColumn) {
          CollectivePhysicalColumns.EnsureKeyedArrayColumn(typeof(TModel), keyedColumn);
        }
        var key = _tryPropertyName(_unwrapLambda(node.Arguments[1]).Body)
          ?? throw new NotSupportedException(
            "UpsertElement's key must be a direct property of the element (c => c.Key); nested or computed keys are not supported.");
        var element = _evaluateValue(node.Arguments[2])
          ?? throw new ArgumentException($"UpsertElement on {collection} needs an element; null cannot be keyed.");
        // The earlier calls in the chain first, so setters are recorded in call order and an upsert can
        // start from the value an earlier setter gave the same property.
        if (node.Object is not null) {
          Visit(node.Object);
        }
        var paramName = _nextParam(collection);
        Parameters[paramName] = JsonSerializer.Serialize(element, element.GetType(), _jsonOptions);
        if (target is not { InDocument: false }) {
          var source = Properties.LastOrDefault(p => p.JsonbPath == collection)?.ValueSql;
          Properties.Add(new PropertyAssignment(collection,
            CollectiveElementUpsertSql.ValueSql(collection, key, $"@{paramName}::jsonb", source)));
        }
        if (target is { } physical) {
          // The same keyed upsert, over the jsonb column: it starts from the column (or from this spec's earlier
          // write to it), so element order, replace-or-append and composition match the document path.
          var source = Columns.LastOrDefault(c => c.Column == physical.ColumnName)?.ValueSql
            ?? CollectivePhysicalColumns.Quote(physical.ColumnName);
          Columns.Add(new ColumnAssignment(collection, physical.ColumnName,
            CollectiveElementUpsertSql.ValueSql(collection, key, $"@{paramName}::jsonb", source)));
        }
        return node;
      }

      // Match: ICollectiveSetters<TModel>.SetProperty<TProp>(selector, value)
      if (node.Method.DeclaringType is { IsGenericType: true } declaring &&
          declaring.GetGenericTypeDefinition() == typeof(ICollectiveSetters<>) &&
          node.Method.Name == "SetProperty" &&
          node.Arguments.Count == 2) {

        var selector = _unwrapLambda(node.Arguments[0]);
        var property = _extractScalarProperty(selector);
        var target = _physical(property);

        // The earlier calls in the chain first, so setters are recorded in call order: on the same
        // property the last call wins, as it does on the EF Core path.
        if (node.Object is not null) {
          Visit(node.Object);
        }
        var valueExpr = node.Arguments[1];
        if (_isLambda(valueExpr)) {
          _compileComputedValue(valueExpr, property.Name, target);
        } else {
          _addValue(property.Name, target, _evaluateValue(valueExpr));
        }
        return node;
      }

      return base.VisitMethodCall(node);
    }

    // A bare lambda is still accepted, but it cannot arrive: SetProperty's parameter is
    // Expression<Func<...>>, so both C# lambda syntax and Expression.Call quote it. Folding that
    // arm into the fallback keeps the tolerance without a line no caller can reach.
    private static LambdaExpression _unwrapLambda(Expression e) =>
      e switch {
        UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression inner } => inner,
        _ => e as LambdaExpression ?? throw new InvalidOperationException(
          $"Expected a lambda expression for the SetProperty selector; got {e.NodeType} of type {e.Type}. The spec's SetProperty calls must pass a property-selector lambda as the first argument.")
      };

    private static bool _isLambda(Expression e) =>
      e is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression } ||
      e is LambdaExpression;

    private static PropertyInfo _extractScalarProperty(LambdaExpression selector) {
      // Strip Convert(s) wrappers (boxing of value types) so the
      // underlying MemberAccess shows through.
      var body = selector.Body;
      while (body is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
        body = convert.Operand;
      }
      if (body is MemberExpression { Expression: ParameterExpression, Member: PropertyInfo prop }) {
        return prop;
      }
      throw new NotSupportedException(
        "DapperCollectiveSpecCompiler only supports scalar top-level property selectors " +
        "(j => j.PropertyName). Nested paths (j => j.Nested.X), indexed access, or computed " +
        "selectors require [CollectiveApplyFor(SpecKind = CollectiveSpecKind.RawSql)].");
    }

    // Append a collective apply-hook constant setter: a pre-evaluated value (not an expression), assigned the same
    // way a spec's constant setter is — the jsonb path, the physical column, or both.
    public void AddConstant(string propertyName, object? value) =>
      _addValue(propertyName, CollectivePhysicalColumns.Resolve(typeof(TModel), propertyName), value);

    // A constant value. A document path binds "@p::jsonb" with the value JSON-serialized; a physical column binds the
    // CLR value as a typed parameter. A field kept in both places gets both, the document path first.
    private void _addValue(string propertyName, PerspectivePhysicalField? target, object? value) {
      if (target is not { } column || column.InDocument) {
        var paramName = _nextParam(propertyName);
        Parameters[paramName] = JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), _jsonOptions);
        Properties.Add(new PropertyAssignment(propertyName, $"@{paramName}::jsonb"));
      }
      if (target is { } physical) {
        var paramName = _nextParam(propertyName);
        if (physical.IsVector) {
          // The Dapper per-event write sends a vector as its text form for the column to parse; so does this.
          Parameters[paramName] = CollectivePhysicalColumns.VectorText(value as float[]);
          Columns.Add(new ColumnAssignment(propertyName, physical.ColumnName, "@" + paramName + "::vector"));
        } else {
          Parameters[paramName] = CollectivePhysicalColumns.ColumnValue(physical, value);
          Columns.Add(new ColumnAssignment(propertyName, physical.ColumnName, "@" + paramName));
        }
      }
    }

    // Computed value source. Supported shape: a property-vs-constant comparison (j => j.SomeProp == value). Over a
    // document field it is a jsonb-to-jsonb comparison; over a physical field it compares the column, null-safely, so
    // it agrees with the C# comparison the replay makes. A document target wraps the boolean in to_jsonb(); a
    // physical target is assigned the boolean itself. Arithmetic, string, and other computed shapes remain RawSql-only.
    private void _compileComputedValue(Expression valueExpr, string targetProperty, PerspectivePhysicalField? target) {
      var lambda = _unwrapLambda(valueExpr);
      if (lambda.Body is BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } bin
          && _tryProperty(bin.Left) is { } comparedProperty) {
        var rhs = _evaluateValue(_stripConvert(bin.Right));
        var paramName = _nextParam(targetProperty);
        var op = bin.NodeType == ExpressionType.Equal ? "=" : "<>";
        string documentValue;
        string columnValue;
        if (_physical(comparedProperty) is { } compared) {
          CollectivePhysicalColumns.EnsureComparable(typeof(TModel), compared);
          Parameters[paramName] = CollectivePhysicalColumns.ColumnValue(compared, rhs);
          columnValue = CollectivePhysicalColumns.NullSafeComparison(
            CollectivePhysicalColumns.Quote(compared.ColumnName), op, "@" + paramName);
          documentValue = $"to_jsonb({columnValue})";
        } else {
          Parameters[paramName] = JsonSerializer.Serialize(rhs, rhs?.GetType() ?? typeof(object), _jsonOptions);
          // (data->'X') is already jsonb; the ::jsonb cast is explicit for readability + a stable SQL shape.
          var comparison = $"(data->'{comparedProperty.Name}')::jsonb {op} @{paramName}::jsonb";
          columnValue = $"({comparison})";
          documentValue = $"to_jsonb({comparison})";
        }
        if (target is not { } column || column.InDocument) {
          Properties.Add(new PropertyAssignment(targetProperty, documentValue));
        }
        if (target is { } physical) {
          Columns.Add(new ColumnAssignment(targetProperty, physical.ColumnName, columnValue));
        }
        return;
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

    private static string? _tryPropertyName(Expression e) => _tryProperty(e)?.Name;

    private static PropertyInfo? _tryProperty(Expression e) =>
      _stripConvert(e) is MemberExpression { Expression: ParameterExpression, Member: PropertyInfo prop }
        ? prop
        : null;

    // The physical column a model property is stored in, or null for a document path (generator-emitted metadata).
    private static PerspectivePhysicalField? _physical(PropertyInfo property) =>
      CollectivePhysicalColumns.Resolve(typeof(TModel), property.Name);

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
