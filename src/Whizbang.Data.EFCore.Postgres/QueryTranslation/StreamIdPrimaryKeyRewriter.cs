// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Lets a filter on a perspective model reach the row: it moves a filter written after
/// <c>Select(r =&gt; r.Data)</c> beneath the projection, and adds the row's primary key beside an
/// equality on the model's registered stream id.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hoisting.</b> An <c>IQueryable</c> composer such as a GraphQL filtering layer applies its filter to
/// the projected model: <c>rows.Select(r =&gt; r.Data).OrderBy(x =&gt; x.Name).Where(x =&gt; x.Id == id)</c>.
/// The rest of the pipeline only recognizes the row form, <c>r.Data.Id</c>, so such a filter never
/// reaches a promoted column or the primary key. A filter commutes with ordering and with an identity
/// projection, so the <c>Where</c> is moved beneath the <c>Select</c> (and any <c>OrderBy</c>/<c>ThenBy</c>
/// between them) as <c>rows.Where(r =&gt; P(r.Data))</c>, which returns exactly the same rows. Anything
/// that does not commute with a filter (<c>Skip</c>, <c>Take</c>, <c>Distinct</c>, another projection)
/// stops the move, and the filter stays where it was.
/// </para>
/// <para>
/// <b>The primary key.</b> A row's <c>id</c> is its stream id, and the model's <c>[StreamId]</c> property
/// starts out as the same value. For each top-level <c>AND</c> conjunct that compares that property with
/// a value, or tests it against a set, the same test on <c>r.Id</c> is added beside it, and the planner
/// reads by primary key. The original test is kept, so the query never returns a row it did not return
/// before. For a model that keeps the <c>[StreamId]</c> contract the two values are equal and the result is
/// the same; a row whose property was set to something other than its stream id is no longer found by that
/// property. A conjunct under <c>OR</c> or <c>NOT</c> is left alone, because it does not narrow the result
/// to those rows.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#stream-id-filters</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/StreamIdPrimaryKeySqlTests.cs</tests>
[SuppressMessage("AOT", "IL2060:MakeGenericMethod can break functionality when AOT compiling", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Rebuilds the query's own Queryable calls and reads PerspectiveRow.Id, which EF Core maps and keeps; EF Core query translation is reflection-based throughout")]
public sealed class StreamIdPrimaryKeyRewriter : ExpressionVisitor {
  private const string ROW_ID = "Id";
  private const string ROW_DATA = "Data";

  private static readonly MethodInfo _where = typeof(Queryable).GetMethods()
    .Single(m => m.Name == nameof(Queryable.Where)
      && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

  /// <summary>Hoists a model filter beneath an identity projection, then adds the primary key where it can.</summary>
  protected override Expression VisitMethodCall(MethodCallExpression node) {
    var visited = base.VisitMethodCall(node);
    if (visited is not MethodCallExpression call || _filterOf(call) is not { } predicate) {
      return visited;
    }

    return _hoisted(call, predicate) ?? _withPrimaryKey(call, predicate);
  }

  /// <summary>The predicate of a <c>Queryable.Where</c> with a one-parameter lambda; null for anything else.</summary>
  private static LambdaExpression? _filterOf(MethodCallExpression call) =>
    call.Method.DeclaringType == typeof(Queryable)
      && call.Method.Name == nameof(Queryable.Where)
      && call.Arguments.Count == 2
      && _unquote(call.Arguments[1]) is LambdaExpression { Parameters.Count: 1 } predicate
        ? predicate
        : null;

  /// <summary>
  /// Rebuilds <c>Select(rows, r =&gt; r.Data).Ordering*.Where(x =&gt; P(x))</c> as
  /// <c>Select(rows.Where(r =&gt; P(r.Data)), r =&gt; r.Data).Ordering*</c>; null when the chain has another shape.
  /// </summary>
  private static Expression? _hoisted(MethodCallExpression where, LambdaExpression predicate) {
    var orderings = new List<MethodCallExpression>();
    var source = where.Arguments[0];
    while (source is MethodCallExpression ordering && _isOrdering(ordering)) {
      orderings.Add(ordering);
      source = ordering.Arguments[0];
    }

    if (source is not MethodCallExpression select || _identityDataSelector(select) is not { } selector
        || predicate.Parameters[0].Type != selector.Body.Type) {
      return null;
    }

    var row = selector.Parameters[0];
    var rowPredicate = Expression.Lambda(new ParameterReplacer(predicate.Parameters[0], selector.Body).Visit(predicate.Body), row);
    var rowWhere = Expression.Call(null, _where.MakeGenericMethod(row.Type), select.Arguments[0], Expression.Quote(rowPredicate));

    Expression result = Expression.Call(null, select.Method, _withPrimaryKey(rowWhere, rowPredicate), select.Arguments[1]);
    for (var i = orderings.Count - 1; i >= 0; i--) {
      result = Expression.Call(null, orderings[i].Method, result, orderings[i].Arguments[1]);
    }

    return result;
  }

  /// <summary>An <c>OrderBy</c>, <c>OrderByDescending</c>, <c>ThenBy</c> or <c>ThenByDescending</c> with a key selector.</summary>
  private static bool _isOrdering(MethodCallExpression call) =>
    call.Method.DeclaringType == typeof(Queryable)
      && call.Arguments.Count == 2
      && call.Method.Name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending)
        or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending);

  /// <summary>The selector of <c>Queryable.Select(rows, r =&gt; r.Data)</c> over perspective rows; null for any other projection.</summary>
  private static LambdaExpression? _identityDataSelector(MethodCallExpression select) =>
    select.Method.DeclaringType == typeof(Queryable)
      && select.Method.Name == nameof(Queryable.Select)
      && select.Arguments.Count == 2
      && _unquote(select.Arguments[1]) is LambdaExpression { Parameters.Count: 1, Body: MemberExpression { Member.Name: ROW_DATA } data } selector
      && data.Expression == selector.Parameters[0]
      && PhysicalFieldExpressionVisitor.IsPerspectiveRowType(selector.Parameters[0].Type)
        ? selector
        : null;

  /// <summary>
  /// The same <c>Where</c> with <c>r.Id</c> tests added beside each top-level stream-id conjunct, or the
  /// call unchanged when there is none.
  /// </summary>
  private static MethodCallExpression _withPrimaryKey(MethodCallExpression where, LambdaExpression predicate) {
    var row = predicate.Parameters[0];
    if (_modelTypeOf(row.Type) is not { } modelType) {
      return where;
    }

    var keys = _conjuncts(predicate.Body)
      .Select(conjunct => _primaryKeyTest(conjunct, row, modelType))
      .OfType<Expression>()
      .ToList();
    if (keys.Count == 0) {
      return where;
    }

    var body = keys.Aggregate(Expression.AndAlso);
    var withKeys = Expression.Lambda(Expression.AndAlso(body, predicate.Body), row);
    return Expression.Call(null, where.Method, where.Arguments[0], Expression.Quote(withKeys));
  }

  /// <summary>The conjunct with its stream-id operand swapped for <c>r.Id</c>, when it is an equality or a set test on the stream id.</summary>
  private static Expression? _primaryKeyTest(Expression conjunct, ParameterExpression row, Type modelType) {
    (Expression Candidate, Expression Other)[] operands = conjunct switch {
      BinaryExpression { NodeType: ExpressionType.Equal } equal => [(equal.Left, equal.Right), (equal.Right, equal.Left)],
      MethodCallExpression { Method.Name: "Contains" } contains when _collectionAndItem(contains) is var (collection, item) => [(item, collection)],
      _ => [],
    };

    foreach (var (candidate, other) in operands) {
      if (_streamIdMember(candidate, row, modelType) is { } member && !_references(other, row)) {
        Expression id = Expression.Property(row, ROW_ID);

        // An equality is rebuilt with the row's id on the left, whichever side the property was on, so the
        // SQL always reads id = @value; a set test keeps its own shape with the id in place of the property.
        if (conjunct is BinaryExpression) {
          return Expression.Equal(_as(id, other.Type), other);
        }

        return new NodeReplacer(member, _as(id, member.Type)).Visit(conjunct);
      }
    }

    return null;
  }

  /// <summary>The expression converted to <paramref name="type"/> when it is not already of it (Guid to Guid?).</summary>
  private static Expression _as(Expression expression, Type type) =>
    expression.Type == type ? expression : Expression.Convert(expression, type);

  /// <summary>The collection and the item of a <c>Contains</c> call, whether it is an instance or an extension method.</summary>
  private static (Expression Collection, Expression Item) _collectionAndItem(MethodCallExpression contains) =>
    contains.Object is { } instance
      ? (instance, contains.Arguments[^1])
      : (contains.Arguments[0], contains.Arguments[^1]);

  /// <summary><c>r.Data.P</c> (through any conversion) when <c>P</c> is the model's registered Guid stream id; null otherwise.</summary>
  private static MemberExpression? _streamIdMember(Expression expression, ParameterExpression row, Type modelType) {
    while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion) {
      expression = conversion.Operand;
    }

    return expression is MemberExpression { Member: PropertyInfo property, Expression: MemberExpression { Member.Name: ROW_DATA } data } member
      && data.Expression == row
      && (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(Guid)
      && StreamIdRegistry.IsStreamIdProperty(modelType, property.Name)
        ? member
        : null;
  }

  /// <summary>The <c>TModel</c> of a <c>PerspectiveRow&lt;TModel&gt;</c> (or a type deriving from one); null for anything else.</summary>
  private static Type? _modelTypeOf(Type rowType) {
    for (var type = rowType; type is not null; type = type.BaseType) {
      if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Whizbang.Core.Lenses.PerspectiveRow<>)) {
        return type.GetGenericArguments()[0];
      }
    }

    return null;
  }

  private static IEnumerable<Expression> _conjuncts(Expression body) =>
    body is BinaryExpression { NodeType: ExpressionType.AndAlso } and
      ? _conjuncts(and.Left).Concat(_conjuncts(and.Right))
      : [body];

  private static bool _references(Expression expression, ParameterExpression parameter) {
    var finder = new ParameterFinder(parameter);
    finder.Visit(expression);
    return finder.Found;
  }

  private static Expression _unquote(Expression expression) {
    while (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote) {
      expression = quote.Operand;
    }

    return expression;
  }

  private sealed class ParameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor {
    protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
  }

  private sealed class NodeReplacer(Expression from, Expression to) : ExpressionVisitor {
    public override Expression? Visit(Expression? node) => node == from ? to : base.Visit(node);
  }

  private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor {
    public bool Found { get; private set; }

    protected override Expression VisitParameter(ParameterExpression node) {
      Found |= node == parameter;
      return node;
    }
  }
}
