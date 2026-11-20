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
/// at a time. Anything else (<c>Skip</c>, <c>Take</c>, <c>Distinct</c>) stops the move, and whatever is
/// above it stays where it was.
/// </para>
/// <para>
/// <b>Groupings and aggregates (#2424).</b> An aggregate over the projection (<c>Sum</c>, <c>Min</c>, <c>Max</c>,
/// <c>Average</c>, <c>Count</c>, <c>LongCount</c>, <c>Any</c>, <c>All</c>) is the same aggregate over the rows, its
/// lambda rewritten over <c>r.Data</c>. A <c>GroupBy</c> over it groups the rows: a key alone as
/// <c>rows.GroupBy(r =&gt; K(r.Data), r =&gt; r.Data)</c>, the same groups of models, and element and result selectors
/// rewritten over the row. A <c>Select</c> over such groups of models is then moved to the groups of rows by
/// <see cref="GroupedRowsRewriter"/>, so an aggregate over a group's members reads the row too. Without this a
/// grouping key or an aggregate over a promoted field read the document, which under Split storage holds the field's
/// default: every row fell in one group and sums were zero, and the query still succeeded.
/// </para>
/// <para>
/// <b>The primary key.</b> A row's <c>id</c> is its stream id, and the model's <c>[StreamId]</c> property
/// starts out as the same value. For each top-level <c>AND</c> conjunct that compares that property with
/// a value, or tests it against a set, the same test on <c>r.Id</c> is added beside it, and the planner
/// reads by primary key. The original test is kept, so the query never returns a row it did not return
/// before. For a model that keeps the <c>[StreamId]</c> contract the two values are equal and the result is
/// the same; a row whose property was set to something other than its stream id is no longer found by that
/// property. A conjunct under <c>OR</c> or <c>NOT</c> is left alone, because it does not narrow the result
/// to those rows. The primary key is never null, so a comparison with a null value, or a set that can hold
/// null, is not answered by it: those keep finding the rows whose document has no stream id.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#stream-id-filters</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/StreamIdPrimaryKeySqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/PhysicalFieldGroupingSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/GroupedRowsRewriterTests.cs</tests>
[SuppressMessage("AOT", "IL2060:MakeGenericMethod can break functionality when AOT compiling", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Rebuilds the query's own Queryable calls and reads PerspectiveRow.Id, which EF Core maps and keeps; EF Core query translation is reflection-based throughout")]
public sealed class StreamIdPrimaryKeyRewriter : ExpressionVisitor {
  private const string ROW_ID = "Id";
  private const string ROW_DATA = "Data";

  private static readonly MethodInfo _where = typeof(Queryable).GetMethods()
    .Single(m => m.Name == nameof(Queryable.Where)
      && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

  /// <summary>The sorts that move beneath an identity projection, which stays above them.</summary>
  private static readonly HashSet<string> _orderings = new(StringComparer.Ordinal) {
    nameof(Queryable.OrderBy), nameof(Queryable.OrderByDescending), nameof(Queryable.ThenBy), nameof(Queryable.ThenByDescending),
  };

  /// <summary>The operators applied to the rows in place of the identity projection: a projection and the aggregates.</summary>
  private static readonly HashSet<string> _foldedOrAggregated = new(StringComparer.Ordinal) {
    nameof(Queryable.Select), nameof(Queryable.Sum), nameof(Queryable.Min), nameof(Queryable.Max), nameof(Queryable.Average),
    nameof(Queryable.Count), nameof(Queryable.LongCount), nameof(Queryable.Any), nameof(Queryable.All),
  };

  private static readonly MethodInfo _groupByKey = _groupBy(parameters: 2, lastFuncArity: 2);
  private static readonly MethodInfo _groupByElement = _groupBy(parameters: 3, lastFuncArity: 2);

  /// <summary>The <c>Queryable.GroupBy</c> overload with no comparer, by its parameter count and its last lambda's arity.</summary>
  private static MethodInfo _groupBy(int parameters, int lastFuncArity) => typeof(Queryable).GetMethods()
    .Single(m => m.Name == nameof(Queryable.GroupBy)
      && m.GetParameters() is { } p
      && p.Length == parameters
      && p[^1].ParameterType.IsGenericType
      && p[^1].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>)
      && p[^1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == lastFuncArity);

  /// <summary>Moves an operator beneath an identity projection, then adds the primary key to a row filter where it can.</summary>
  protected override Expression VisitMethodCall(MethodCallExpression node) {
    if (node.Method.DeclaringType != typeof(Queryable) || node.Object is not null) {
      return base.VisitMethodCall(node);
    }

    var arguments = Visit(node.Arguments).ToArray();

    // Decided from the rewritten arguments before the call is rebuilt: a ThenBy whose primary sort has just
    // moved beneath the projection now sits on an IQueryable, not an IOrderedQueryable, and could not be
    // rebuilt where it stands, only moved beneath the projection with it.
    if (_hoisted(node.Method, arguments) is { } hoisted) {
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
  /// <c>rows.Op(r =&gt; L(r.Data)).Select(r =&gt; r.Data)</c>, <c>Select</c> folded into
  /// <c>rows.Select(r =&gt; f(r.Data))</c>, an aggregate as <c>rows.Op(r =&gt; L(r.Data))</c>, and a grouping as the
  /// same grouping of the rows (#2424). Null when the call is not one of those over an identity projection.
  /// </summary>
  private static MethodCallExpression? _hoisted(MethodInfo method, Expression[] arguments) {
    if (method.Name == nameof(Queryable.Select) && _regrouped(method, arguments) is { } regrouped) {
      return regrouped;
    }
    if (arguments.Length < 2
        || arguments[0] is not MethodCallExpression select
        || _identityDataSelector(select) is not { } selector
        || _unquote(arguments[1]) is not LambdaExpression { Parameters.Count: 1 } lambda
        || lambda.Parameters[0].Type != selector.Body.Type) {
      return null;
    }

    var row = selector.Parameters[0];
    var rows = select.Arguments[0];
    if (method.Name == nameof(Queryable.GroupBy)) {
      return _groupedRows(method, arguments, rows, selector);
    }
    if (arguments.Length != 2) {
      return null;
    }
    var rowLambda = _overRow(lambda, selector);

    if (method.Name == nameof(Queryable.Where)) {
      var rowWhere = Expression.Call(null, _where.MakeGenericMethod(row.Type), rows, Expression.Quote(rowLambda));
      return Expression.Call(null, select.Method, _withPrimaryKey(rowWhere, rowLambda), select.Arguments[1]);
    }

    // A secondary sort can only sit on this projection when its primary sort was applied to the same
    // projection, and the inside-out rewrite has already moved that one beneath it, so the rows are ordered.
    if (_orderings.Contains(method.Name)) {
      return Expression.Call(null, select.Method, _onRows(method, rows, row, rowLambda), select.Arguments[1]);
    }

    // A projection folds into one, and an aggregate's value is the same over the rows as over the models projected
    // from them (#2424).
    return _foldedOrAggregated.Contains(method.Name) ? _onRows(method, rows, row, rowLambda) : null;
  }

  /// <summary><c>x =&gt; L(x)</c> over the model as <c>r =&gt; L(r.Data)</c> over the row the identity projection reads.</summary>
  private static LambdaExpression _overRow(LambdaExpression lambda, LambdaExpression selector) =>
    Expression.Lambda(new ParameterReplacer(lambda.Parameters[0], selector.Body).Visit(lambda.Body), selector.Parameters[0]);

  /// <summary>
  /// A <c>GroupBy</c> over the identity projection as the same grouping of the rows, so its key reads the row (#2424).
  /// A key alone groups the rows with the models as the elements, which leaves the type every later operator sees the
  /// same, and <see cref="_regrouped"/> then moves an operator over those groups to the rows. An element selector is
  /// rewritten over the row, and a result selector over the members over the rows. Null for a grouping with a comparer.
  /// </summary>
  private static MethodCallExpression? _groupedRows(MethodInfo method, Expression[] arguments, Expression rows, LambdaExpression selector) {
    var row = selector.Parameters[0];
    var lambdas = arguments.Skip(1).Select(_unquote).OfType<LambdaExpression>().ToArray();
    if (lambdas.Length != arguments.Length - 1) {
      return null;
    }
    var key = _overRow(lambdas[0], selector);
    var types = method.GetGenericArguments();
    types[0] = row.Type;

    switch (lambdas.Length) {
      case 1:
        return Expression.Call(null, _groupByElement.MakeGenericMethod(row.Type, key.ReturnType, selector.ReturnType),
          rows, Expression.Quote(key), Expression.Quote(selector));

      case 2 when lambdas[1].Parameters.Count == 1:
        return Expression.Call(null, method.GetGenericMethodDefinition().MakeGenericMethod(types),
          rows, Expression.Quote(key), Expression.Quote(_overRow(lambdas[1], selector)));

      case 2:
        var result = lambdas[1];
        var members = Expression.Parameter(typeof(IEnumerable<>).MakeGenericType(row.Type), result.Parameters[1].Name);
        // A sequence of members, unlike a group, always has a row form: read as a value, it is the rows projected back.
        var body = GroupedRowsRewriter.Rewrite(result.Body, result.Parameters[1], members, selector.ReturnType, row.Type)!;
        return Expression.Call(null, method.GetGenericMethodDefinition().MakeGenericMethod(types),
          rows, Expression.Quote(key), Expression.Quote(Expression.Lambda(body, result.Parameters[0], members)));

      default:
        return Expression.Call(null, method.GetGenericMethodDefinition().MakeGenericMethod(types),
          rows, Expression.Quote(key), Expression.Quote(_overRow(lambdas[1], selector)), Expression.Quote(lambdas[2]));
    }
  }

  /// <summary>
  /// A <c>Select</c> over <c>rows.GroupBy(r =&gt; K, r =&gt; r.Data)</c>, the groups of models a hoisted grouping makes, as
  /// the same <c>Select</c> over the groups of rows, every use of a group rewritten over its rows (#2424). Null when the
  /// call is not one, or a use of the group has no row form.
  /// </summary>
  private static MethodCallExpression? _regrouped(MethodInfo method, Expression[] arguments) {
    if (arguments[0] is not MethodCallExpression { Method.IsGenericMethod: true } grouped
        || grouped.Method.GetGenericMethodDefinition() != _groupByElement
        || !_isIdentityData((LambdaExpression)_unquote(grouped.Arguments[2]))
        || _unquote(arguments[1]) is not LambdaExpression { Parameters.Count: 1 } projection) {
      return null;
    }

    var elements = (LambdaExpression)_unquote(grouped.Arguments[2]);
    var row = elements.Parameters[0].Type;
    var key = (LambdaExpression)_unquote(grouped.Arguments[1]);
    var keyType = key.ReturnType;
    var groupOfRows = Expression.Parameter(typeof(IGrouping<,>).MakeGenericType(keyType, row), projection.Parameters[0].Name);
    if (GroupedRowsRewriter.Rewrite(projection.Body, projection.Parameters[0], groupOfRows, elements.ReturnType, row) is not { } body) {
      return null;
    }

    var groups = Expression.Call(null, _groupByKey.MakeGenericMethod(row, keyType), grouped.Arguments[0], Expression.Quote(key));
    return Expression.Call(null, method.GetGenericMethodDefinition().MakeGenericMethod(groupOfRows.Type, body.Type),
      groups, Expression.Quote(Expression.Lambda(body, groupOfRows)));
  }

  /// <summary>The same <c>Queryable</c> operator applied to the rows, with its lambda rewritten over <c>r.Data</c>.</summary>
  private static MethodCallExpression _onRows(MethodInfo method, Expression rows, ParameterExpression row, LambdaExpression rowLambda) {
    var definition = method.GetGenericMethodDefinition();
    Type[] types = definition.GetGenericArguments().Length == 1 ? [row.Type] : [row.Type, rowLambda.ReturnType];
    return Expression.Call(null, definition.MakeGenericMethod(types), rows, Expression.Quote(rowLambda));
  }

  /// <summary>The selector of <c>Queryable.Select(rows, r =&gt; r.Data)</c> over perspective rows; null for any other projection.</summary>
  private static LambdaExpression? _identityDataSelector(MethodCallExpression select) =>
    select.Method.DeclaringType == typeof(Queryable)
      && select.Method.Name == nameof(Queryable.Select)
      && select.Arguments.Count == 2
      && _unquote(select.Arguments[1]) is LambdaExpression selector
      && _isIdentityData(selector)
        ? selector
        : null;

  /// <summary>Whether <paramref name="lambda"/> is <c>r =&gt; r.Data</c> over a perspective row.</summary>
  private static bool _isIdentityData(LambdaExpression lambda) =>
    lambda is { Parameters.Count: 1, Body: MemberExpression { Member.Name: ROW_DATA } data }
      && data.Expression == lambda.Parameters[0]
      && PhysicalFieldExpressionVisitor.IsPerspectiveRowType(lambda.Parameters[0].Type);

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
      MethodCallExpression { Method.Name: "Contains" } contains => [_itemAndCollection(contains)],
      _ => [],
    };

    foreach (var (candidate, other) in operands) {
      if (_streamIdMember(candidate, row, modelType) is { } member && !_references(other, row)) {
        Expression id = Expression.Property(row, ROW_ID);

        // An equality is rebuilt with the row's id on the left, whichever side the property was on, so the
        // SQL always reads id = @value; a set test keeps its own shape with the id in place of the property.
        // The primary key is never null, while a null comparison matches a document with no stream id. So
        // an equality with a value that can be null adds the key only when the value is not null (EF Core
        // settles that from the parameter's value), and a set that can hold null stays a document search.
        if (conjunct is BinaryExpression) {
          var key = Expression.Equal(_as(id, other.Type), other);
          return _canBeNull(other.Type)
            ? Expression.OrElse(Expression.Equal(other, Expression.Constant(null, other.Type)), key)
            : key;
        }

        return _canBeNull(candidate.Type) ? null : new NodeReplacer(member, _as(id, member.Type)).Visit(conjunct);
      }
    }

    return null;
  }

  /// <summary>The expression converted to <paramref name="type"/> when it is not already of it (Guid to Guid?).</summary>
  private static Expression _as(Expression expression, Type type) =>
    expression.Type == type ? expression : Expression.Convert(expression, type);

  private static bool _canBeNull(Type type) => Nullable.GetUnderlyingType(type) is not null;

  /// <summary>The item and the collection of a <c>Contains</c> call, whether it is an instance or an extension method.</summary>
  private static (Expression Item, Expression Collection) _itemAndCollection(MethodCallExpression contains) =>
    (contains.Arguments[^1], contains.Object ?? contains.Arguments[0]);

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
