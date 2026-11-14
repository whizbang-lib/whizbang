// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Lets a query written over a perspective model reach the row: it moves the filters, sorts and
/// projections written after <c>Select(r =&gt; r.Data)</c> beneath it, and adds the row's primary key
/// beside an equality on the model's registered stream id.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hoisting.</b> An <c>IQueryable</c> composer such as a GraphQL filtering and sorting layer works on the
/// projected model: <c>rows.Select(r =&gt; r.Data).OrderBy(x =&gt; x.Name).Where(x =&gt; x.Id == id)</c>.
/// The rest of the pipeline only recognizes the row form, <c>r.Data.Name</c>, so such a query never
/// reaches a promoted column or the primary key. An identity projection commutes with a filter and with
/// ordering, so a <c>Where</c>, <c>OrderBy</c>, <c>OrderByDescending</c>, <c>ThenBy</c> or
/// <c>ThenByDescending</c> applied directly to it is moved beneath it, as
/// <c>rows.Where(r =&gt; P(r.Data)).Select(r =&gt; r.Data)</c>, and a <c>Select(x =&gt; f(x))</c> applied to it
/// is folded into one projection, <c>rows.Select(r =&gt; f(r.Data))</c>. The rows, their order and their
/// shape are unchanged. The tree is rewritten from the inside out, so a chain of such operators moves one
/// at a time. Anything else (<c>Skip</c>, <c>Take</c>, <c>Distinct</c>, a grouping) stops the move, and
/// whatever is above it stays where it was.
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

  /// <summary>Moves an operator beneath an identity projection, then adds the primary key to a row filter where it can.</summary>
  protected override Expression VisitMethodCall(MethodCallExpression node) {
    if (node.Method.DeclaringType != typeof(Queryable) || node.Object is not null) {
      return base.VisitMethodCall(node);
    }

    var arguments = node.Arguments.Select(a => Visit(a) ?? a).ToArray();

    // Decided from the rewritten arguments before the call is rebuilt: a ThenBy whose primary sort has just
    // moved beneath the projection now sits on an IQueryable, not an IOrderedQueryable, and could not be
    // rebuilt where it stands, only moved beneath the projection with it.
    if (arguments.Length == 2 && _hoisted(node.Method, arguments[0], arguments[1]) is { } hoisted) {
      return hoisted;
    }

    // A rewrite beneath that changed a type this operator depends on (an ordered source under a ThenBy that
    // could not move with it) is undone by keeping the original subtree, which translates as it always did.
    var parameters = node.Method.GetParameters();
    if (arguments.Where((a, i) => !parameters[i].ParameterType.IsAssignableFrom(a.Type)).Any()) {
      return node;
    }

    var call = arguments.SequenceEqual(node.Arguments) ? node : node.Update(null, arguments);
    return _filterOf(call) is { } predicate ? _withPrimaryKey(call, predicate) : call;
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
  /// The operator moved beneath the identity projection it is applied to: <c>Where</c> and the orderings as
  /// <c>rows.Op(r =&gt; L(r.Data)).Select(r =&gt; r.Data)</c>, and <c>Select</c> folded into
  /// <c>rows.Select(r =&gt; f(r.Data))</c>. Null when the call is not one of those over an identity projection.
  /// </summary>
  private static MethodCallExpression? _hoisted(MethodInfo method, Expression source, Expression argument) {
    if (source is not MethodCallExpression select
        || _identityDataSelector(select) is not { } selector
        || _unquote(argument) is not LambdaExpression { Parameters.Count: 1 } lambda
        || lambda.Parameters[0].Type != selector.Body.Type) {
      return null;
    }

    var row = selector.Parameters[0];
    var rows = select.Arguments[0];
    var rowLambda = Expression.Lambda(new ParameterReplacer(lambda.Parameters[0], selector.Body).Visit(lambda.Body), row);

    switch (method.Name) {
      case nameof(Queryable.Where):
        var rowWhere = Expression.Call(null, _where.MakeGenericMethod(row.Type), rows, Expression.Quote(rowLambda));
        return Expression.Call(null, select.Method, _withPrimaryKey(rowWhere, rowLambda), select.Arguments[1]);

      case nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending):
        return Expression.Call(null, select.Method, _onRows(method, rows, row, rowLambda), select.Arguments[1]);

      // A secondary sort needs its primary beneath the projection too, which the inside-out rewrite has
      // already done whenever the primary sort was applied to the same projection.
      case nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending)
          when typeof(IOrderedQueryable<>).MakeGenericType(row.Type).IsAssignableFrom(rows.Type):
        return Expression.Call(null, select.Method, _onRows(method, rows, row, rowLambda), select.Arguments[1]);

      case nameof(Queryable.Select):
        return _onRows(method, rows, row, rowLambda);

      default:
        return null;
    }
  }

  /// <summary>The same <c>Queryable</c> operator applied to the rows, with its lambda rewritten over <c>r.Data</c>.</summary>
  private static MethodCallExpression _onRows(MethodInfo method, Expression rows, ParameterExpression row, LambdaExpression rowLambda) =>
    Expression.Call(null, method.GetGenericMethodDefinition().MakeGenericMethod(row.Type, rowLambda.ReturnType), rows, Expression.Quote(rowLambda));

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
