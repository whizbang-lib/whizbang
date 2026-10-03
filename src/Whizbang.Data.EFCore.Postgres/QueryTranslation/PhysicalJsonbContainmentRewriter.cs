using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Compiles a filter on a promoted jsonb column into a containment test, the one shape a GIN index on
/// that column can answer.
/// </summary>
/// <remarks>
/// <para>
/// A read model can keep normalized filter values in a second, small jsonb column: a
/// <c>[PhysicalField]</c> holding a dictionary, a list or an object, indexed with GIN. Once
/// <see cref="PhysicalFieldExpressionVisitor"/> has redirected the property to its column, a filter on
/// it is a member access, an indexer or a <c>Contains</c> over a value Entity Framework holds as one
/// converted value, which it cannot translate at all. This turns the shapes containment can express
/// into <c>column @&gt; document</c>, with the document built in SQL by <see cref="JsonbDocument"/>.
/// </para>
/// <para>
/// The shapes, inside a filter (<c>Where</c>, <c>Any</c>, <c>Count</c>, <c>First</c> and the rest of
/// the filtering operators):
/// </para>
/// <list type="bullet">
/// <item><c>col[key].Contains(v)</c> and <c>col[key].Any(e =&gt; e == v)</c> on a dictionary of collections:
/// <c>col @&gt; {key: [v]}</c>.</item>
/// <item><c>col[key] == v</c> on a dictionary of scalars: <c>col @&gt; {key: v}</c>.</item>
/// <item><c>col.Contains(v)</c> and <c>col.Any(e =&gt; e == v)</c> on a collection of scalars:
/// <c>col @&gt; [v]</c>.</item>
/// <item><c>col.Any(e =&gt; e.A == v &amp;&amp; e.B.C == w)</c> on a collection of objects:
/// <c>col @&gt; [{A: v, B: {C: w}}]</c>, one element matching every condition.</item>
/// <item><c>col.A.B == v</c> on an object: <c>col @&gt; {A: {B: v}}</c>.</item>
/// </list>
/// <para>
/// Any of these may be negated: there is no other translation to agree with, so <c>!</c> is simply the
/// negated containment test, and a row whose column is SQL NULL matches neither form. Member names are
/// the stored names, read from the serializer's metadata under the persistence profile, so a
/// <c>[JsonPropertyName]</c> is honored. Values are restricted to the types whose stored text and
/// <c>to_jsonb</c>'s agree (<see cref="JsonbDocument.ValueOverloadFor"/>); anything else, a literal
/// <c>null</c> in an equality, a value read from the row, or a shape not listed is left exactly as it
/// was.
/// </para>
/// <para>
/// Unlike <see cref="JsonbContainmentRewriter"/>, this is not affected by
/// <see cref="JsonbContainmentSwitch"/>: that switch chooses between two ways of compiling a filter that
/// already has a working translation, while a filter on a jsonb column has no other translation to fall
/// back to.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-columns</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/PhysicalJsonbContainmentSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/PhysicalJsonbContainmentIntegrationTests.cs</tests>
/// <param name="model">The model being queried, or null to stand down.</param>
public sealed class PhysicalJsonbContainmentRewriter(IModel? model) : ExpressionVisitor {
  private readonly IModel? _model = model;

  private bool _enabled => _model?.FindDbFunction(JsonbDocument.MemberMethod) is not null;

  private int _predicateDepth;

  private static readonly MethodInfo _efPropertyDefinition = typeof(EF).GetMethod(nameof(EF.Property))!;

  private static readonly MethodInfo _jsonContains =
    ((Func<DbFunctions, object, object, bool>)NpgsqlJsonDbFunctionsExtensions.JsonContains).Method;

  /// <inheritdoc/>
  protected override Expression VisitMethodCall(MethodCallExpression node) {
    ArgumentNullException.ThrowIfNull(node);

    if (_predicateDepth > 0 && _enabled
        && (_tryRewriteContains(node, out var rewritten) || _tryRewriteAny(node, out rewritten))) {
      return rewritten;
    }

    var name = node.Method.Name;
    if (name.EndsWith("Async", StringComparison.Ordinal)) {
      name = name[..^"Async".Length];
    }

    if (!JsonbContainmentRewriter.PredicateOperators.Contains(name) || node.Arguments.Count < 2) {
      return base.VisitMethodCall(node);
    }

    // The source is not a predicate; the arguments after it are.
    var arguments = new Expression[node.Arguments.Count];
    arguments[0] = Visit(node.Arguments[0]);

    _predicateDepth++;
    try {
      for (var i = 1; i < node.Arguments.Count; i++) {
        arguments[i] = Visit(node.Arguments[i]);
      }
    } finally {
      _predicateDepth--;
    }

    return node.Update(Visit(node.Object), arguments);
  }

  /// <inheritdoc/>
  protected override Expression VisitBinary(BinaryExpression node) {
    ArgumentNullException.ThrowIfNull(node);

    if (node.NodeType == ExpressionType.Equal && _predicateDepth > 0 && _enabled
        && (_tryRewriteEquality(node.Left, node.Right, out var rewritten)
            || _tryRewriteEquality(node.Right, node.Left, out rewritten))) {
      return rewritten;
    }

    return base.VisitBinary(node);
  }

  /// <summary><c>col.A.B == v</c> and <c>col[key] == v</c>: the value at a path of an object or a dictionary.</summary>
  private bool _tryRewriteEquality(Expression candidateMember, Expression candidateValue, out Expression rewritten) {
    rewritten = candidateMember;

    var value = _stripConverts(candidateValue);
    if (value is ConstantExpression { Value: null } || !_columnPath(candidateMember, out var column, out var steps)
        || steps.Count == 0 || !_valueDocument(_stripConverts(candidateMember).Type, candidateValue, out var leaf)) {
      return false;
    }

    rewritten = _containment(column, _wrap(steps, leaf));
    return true;
  }

  /// <summary><c>col.Contains(v)</c> and <c>col[key].Contains(v)</c>, as an instance or an extension call.</summary>
  private bool _tryRewriteContains(MethodCallExpression node, out Expression rewritten) {
    rewritten = node;

    if (!string.Equals(node.Method.Name, nameof(Enumerable.Contains), StringComparison.Ordinal)
        || node.Method.DeclaringType == typeof(string)) {
      return false;
    }

    if (node.Arguments.Count != (node.Object is null ? 2 : 1)) {
      return false;
    }

    var (source, value) = node.Object is not null
      ? (node.Object, node.Arguments[0])
      : (_unwrapSpan(node.Arguments[0]), node.Arguments[1]);

    if (!_columnPath(source, out var column, out var steps) || !_valueDocument(value.Type, value, out var element)) {
      return false;
    }

    rewritten = _containment(column, _wrap(steps, Expression.Call(JsonbDocument.ArrayMethod, element)));
    return true;
  }

  /// <summary>
  /// <c>col.Any(e =&gt; e == v)</c> and <c>col.Any(e =&gt; e.A == v &amp;&amp; e.B == w)</c>: one element
  /// matching every condition.
  /// </summary>
  private bool _tryRewriteAny(MethodCallExpression node, out Expression rewritten) {
    rewritten = node;

    if (!string.Equals(node.Method.Name, nameof(Enumerable.Any), StringComparison.Ordinal)
        || node.Method.DeclaringType != typeof(Enumerable) || node.Arguments.Count != 2
        || node.Arguments[1] is not LambdaExpression { Parameters: [var element] } predicate
        || !_columnPath(node.Arguments[0], out var column, out var steps)
        || !_elementDocument(predicate.Body, element, out var document)) {
      return false;
    }

    rewritten = _containment(column, _wrap(steps, Expression.Call(JsonbDocument.ArrayMethod, document)));
    return true;
  }

  /// <summary>
  /// The document one element has to contain: a scalar for <c>e == v</c>, or one object merged from
  /// every member condition of a conjunction.
  /// </summary>
  private static bool _elementDocument(Expression body, ParameterExpression element, out Expression document) {
    document = body;

    var conditions = new List<BinaryExpression>();
    if (!_conjuncts(body, conditions)) {
      return false;
    }

    var names = new HashSet<string>(StringComparer.Ordinal);
    Expression? merged = null;

    foreach (var condition in conditions) {
      if (!_elementCondition(condition, element, out var steps, out var leaf)) {
        return false;
      }

      // A bare element (e == v) can only be the whole condition: it cannot be merged into an object.
      if (steps.Count == 0) {
        document = leaf;
        return conditions.Count == 1;
      }

      // Merging is a shallow concatenation, so two conditions under one top-level member would replace
      // each other rather than both apply. That shape is left alone.
      if (steps[0] is not ConstantExpression { Value: string first } || !names.Add(first)) {
        return false;
      }

      var part = _wrap(steps, leaf);
      merged = merged is null ? part : Expression.Call(JsonbDocument.MergeMethod, merged, part);
    }

    document = merged!;
    return true;
  }

  /// <summary>One <c>==</c> of a conjunction inside an element predicate, either way round.</summary>
  private static bool _elementCondition(
      BinaryExpression condition, ParameterExpression element, out List<Expression> steps, out Expression leaf) {
    leaf = condition;
    steps = [];

    foreach (var (member, value) in new[] { (condition.Left, condition.Right), (condition.Right, condition.Left) }) {
      if (_stripConverts(value) is not ConstantExpression { Value: null }
          && _path(member, root => root == element, steps)
          && _valueDocument(_stripConverts(member).Type, value, out leaf)) {
        return true;
      }

      steps.Clear();
    }

    return false;
  }

  /// <summary>Flattens an <c>&amp;&amp;</c> tree of equalities; anything else is not a supported element predicate.</summary>
  private static bool _conjuncts(Expression body, List<BinaryExpression> conditions) {
    switch (body) {
      case BinaryExpression { NodeType: ExpressionType.AndAlso } and:
        return _conjuncts(and.Left, conditions) && _conjuncts(and.Right, conditions);
      case BinaryExpression { NodeType: ExpressionType.Equal } equality:
        conditions.Add(equality);
        return true;
      default:
        return false;
    }
  }

  /// <summary>
  /// <c>JsonbDocument.Value(v)</c> for a value whose type the containment document can carry faithfully,
  /// typed by <paramref name="memberType"/>, the type of what it is compared with.
  /// </summary>
  private static bool _valueDocument(Type memberType, Expression value, out Expression document) {
    document = value;

    var overload = JsonbDocument.ValueOverloadFor(memberType);
    if (overload is null || _references(value, null)) {
      return false;
    }

    var parameterType = overload.GetParameters()[0].ParameterType;
    var stripped = _stripConverts(value);
    var argument = stripped.Type == parameterType ? stripped : Expression.Convert(stripped, parameterType);
    document = Expression.Call(overload, argument);
    return true;
  }

  /// <summary>Nests <paramref name="leaf"/> inside one single-member object per step, innermost last.</summary>
  private static Expression _wrap(List<Expression> steps, Expression leaf) {
    var document = leaf;
    for (var i = steps.Count - 1; i >= 0; i--) {
      document = Expression.Call(JsonbDocument.MemberMethod, steps[i], document);
    }

    return document;
  }

  private static MethodCallExpression _containment(Expression column, Expression document) =>
    Expression.Call(
      _jsonContains,
      Expression.Constant(EF.Functions),
      Expression.Convert(column, typeof(object)),
      Expression.Convert(document, typeof(object)));

  /// <summary>The jsonb column an expression is rooted at, and the keys from the column to it.</summary>
  private bool _columnPath(Expression expression, out Expression column, out List<Expression> steps) {
    Expression? found = null;
    steps = [];
    var matched = _path(expression, candidate => {
      if (_isJsonbColumn(candidate)) {
        found = candidate;
        return true;
      }

      return false;
    }, steps);

    column = found!;
    return matched;
  }

  /// <summary>
  /// Walks member accesses and string-keyed dictionary indexers down to a root, collecting the stored
  /// key of each step as an expression: a constant for a member, the indexer's argument for a key.
  /// </summary>
  private static bool _path(Expression expression, Func<Expression, bool> isRoot, List<Expression> steps) {
    var current = _stripConverts(expression);

    if (isRoot(current)) {
      return true;
    }

    switch (current) {
      // The root is found first, so the serializer is only asked about members under a jsonb column.
      case MemberExpression { Expression: { } owner, Member: PropertyInfo property }:
        if (!_path(owner, isRoot, steps) || _storedName(owner.Type, property.Name) is not { } name) {
          return false;
        }

        steps.Add(Expression.Constant(name));
        return true;

      case MethodCallExpression { Object: { } dictionary, Method.Name: "get_Item", Arguments: [var key] }
          when key.Type == typeof(string) && !_references(key, null):
        if (!_path(dictionary, isRoot, steps)) {
          return false;
        }

        steps.Add(key);
        return true;

      default:
        return false;
    }
  }

  /// <summary>
  /// The name a member is stored under, from the serializer's metadata for its declaring type under the
  /// persistence profile, which is what wrote the column. Null when the metadata does not know it.
  /// </summary>
  private static string? _storedName(Type owner, string member) {
    if (!PerspectiveDocumentSerialization.Options.TryGetTypeInfo(owner, out var info)
        || info.Kind != JsonTypeInfoKind.Object) {
      return null;
    }

    foreach (var property in info.Properties) {
      // Source-generated metadata lists a [JsonIgnore] member too; it is never written, so it has no stored name.
      if (property.AttributeProvider is MemberInfo declared && string.Equals(declared.Name, member, StringComparison.Ordinal)) {
        return declared.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }
          ? null
          : property.Name;
      }
    }

    // Metadata built without an attribute provider (the framework's own generated contexts) cannot say which
    // member a JSON property came from. Those contexts write a member under its own name, so a JSON property
    // of exactly that name is the member's; anything else stands down.
    foreach (var property in info.Properties) {
      if (property.AttributeProvider is null && string.Equals(property.Name, member, StringComparison.Ordinal)) {
        return property.Name;
      }
    }

    return null;
  }

  /// <summary>Whether this is <c>EF.Property(row, name)</c> for a property the model stores in a jsonb column.</summary>
  private bool _isJsonbColumn(Expression expression) =>
    expression is MethodCallExpression { Method.IsGenericMethod: true, Arguments: [var entity, ConstantExpression { Value: string name }] } call
    && call.Method.GetGenericMethodDefinition() == _efPropertyDefinition
    && _entityType(entity.Type)?.FindProperty(name)?.GetColumnType() is { } columnType
    && string.Equals(columnType, "jsonb", StringComparison.OrdinalIgnoreCase);

  /// <summary>The model's entity type for a row type.</summary>
  /// <remarks>
  /// Matched over the model's entity types rather than looked up by type: the lookup by type demands
  /// trimming annotations an expression's type cannot carry, and the row type of a query being compiled
  /// is always one the model already holds.
  /// </remarks>
  private IEntityType? _entityType(Type rowType) =>
    _model!.GetEntityTypes().FirstOrDefault(entityType => entityType.ClrType == rowType);

  /// <summary>
  /// The array under C# 14's span conversion: <c>array.Contains(v)</c> in an expression tree binds to
  /// <c>MemoryExtensions.Contains(op_Implicit(array), v)</c>.
  /// </summary>
  private static Expression _unwrapSpan(Expression source) =>
    source is MethodCallExpression { Method.Name: "op_Implicit", Arguments: [var inner] } ? inner : source;

  /// <summary>
  /// Whether the subtree reads a parameter: any parameter when <paramref name="parameter"/> is null,
  /// which makes it something other than a value.
  /// </summary>
  private static bool _references(Expression expression, ParameterExpression? parameter) {
    var finder = new ParameterFinder(parameter);
    finder.Visit(expression);
    return finder.Found;
  }

  private static Expression _stripConverts(Expression expression) {
    var current = expression;
    while (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary) {
      current = unary.Operand;
    }

    return current;
  }

  private sealed class ParameterFinder(ParameterExpression? target) : ExpressionVisitor {
    public bool Found { get; private set; }

    protected override Expression VisitParameter(ParameterExpression node) {
      Found |= target is null || node == target;
      return node;
    }
  }
}
