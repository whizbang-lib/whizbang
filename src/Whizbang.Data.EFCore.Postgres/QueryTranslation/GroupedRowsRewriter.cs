// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Rewrites a lambda written over a group of projected models (<c>g =&gt; new { g.Key, Total = g.Sum(x =&gt; x.Price) }</c>)
/// into the same lambda over the group of rows those models were projected from, so every member access reads
/// <c>r.Data.Price</c>, which the physical-field pass moves to the promoted column (#2424).
/// </summary>
/// <remarks>
/// <para>
/// A group's <c>Key</c> is the rows' group's key. An <c>Enumerable</c> operator applied to the group, or to what an
/// earlier rewritten operator returned, is applied to the rows instead, its lambdas rewritten over <c>r.Data</c>, when
/// that leaves its result the same: an aggregate (<c>Count</c>, <c>Sum</c>, <c>Max</c>, ...), a projection, or a
/// filter (<c>Where</c>, <c>Skip</c>, <c>Take</c>), whose rows are projected back to models for what follows. Any other
/// operator (an ordering, <c>First</c>, <c>Distinct</c>) is applied to the models the rows were projected to, as
/// written, so it reads the document as it always did. A use of the group itself as a value has no row form, and the
/// whole lambda is left as it was.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/PhysicalFieldGroupingSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/GroupedRowsRewriterTests.cs</tests>
[SuppressMessage("AOT", "IL2060:MakeGenericMethod can break functionality when AOT compiling", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Rebuilds the query's own Enumerable calls and reads PerspectiveRow.Data, which EF Core maps and keeps; EF Core query translation is reflection-based throughout")]
internal sealed class GroupedRowsRewriter : ExpressionVisitor {
  private const string ROW_DATA = "Data";
  private const string GROUP_KEY = "Key";

  private static readonly MethodInfo _select = typeof(Enumerable).GetMethods()
    .Single(m => m.Name == nameof(Enumerable.Select)
      && m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2);

  private readonly ParameterExpression _models;
  private readonly ParameterExpression _rows;
  private readonly Type _model;
  private readonly Type _row;
  private readonly Dictionary<Expression, Expression> _rowsBehind = [];
  private bool _failed;

  private GroupedRowsRewriter(ParameterExpression models, ParameterExpression rows, Type model, Type row) {
    _models = models;
    _rows = rows;
    _model = model;
    _row = row;
  }

  /// <summary>
  /// <paramref name="body"/>, written over <paramref name="models"/>, rewritten over <paramref name="rows"/>; null when a
  /// use of <paramref name="models"/> has no row form.
  /// </summary>
  /// <param name="body">The lambda body.</param>
  /// <param name="models">The group of models, or the sequence of them, that <paramref name="body"/> reads.</param>
  /// <param name="rows">The group, or sequence, of the rows the models were projected from.</param>
  /// <param name="model">The model type.</param>
  /// <param name="row">The row type, a <c>PerspectiveRow&lt;TModel&gt;</c>.</param>
  internal static Expression? Rewrite(Expression body, ParameterExpression models, ParameterExpression rows, Type model, Type row) {
    var rewriter = new GroupedRowsRewriter(models, rows, model, row);
    var rewritten = rewriter.Visit(body);
    return rewriter._failed ? null : rewritten;
  }

  /// <summary>A group's key is the rows' group's key: the one member a group, or a sequence, of models has.</summary>
  protected override Expression VisitMember(MemberExpression node) =>
    node.Expression == _models
      ? Expression.Property(_rows, GROUP_KEY)
      : base.VisitMember(node);

  /// <summary>
  /// A sequence of models read as a value is the rows projected to models; a group read as a value has no row form.
  /// </summary>
  protected override Expression VisitParameter(ParameterExpression node) {
    if (node != _models) {
      return node;
    }
    if (node.Type == typeof(IEnumerable<>).MakeGenericType(_model)) {
      return _asModels(_rows);
    }
    _failed = true;
    return node;
  }

  /// <summary>An <c>Enumerable</c> operator over the models, applied to their rows where its result stays the same.</summary>
  protected override Expression VisitMethodCall(MethodCallExpression node) {
    if (node.Method is not { IsStatic: true, IsGenericMethod: true } method
        || method.DeclaringType != typeof(Enumerable)
        || method.GetGenericArguments()[0] != _model) {
      return base.VisitMethodCall(node);
    }

    Expression visitedSource;
    Expression? rows;
    if (node.Arguments[0] == _models) {
      visitedSource = _models;
      rows = _rows;
    } else {
      visitedSource = Visit(node.Arguments[0]);
      rows = _rowsBehind.GetValueOrDefault(visitedSource);
    }
    var rest = node.Arguments.Skip(1).ToArray();
    if (rows is not null && _onRows(method, rows, rest) is { } call) {
      if (call.Type == node.Type) {
        return call;
      }
      if (method.Name is nameof(Enumerable.Where) or nameof(Enumerable.Skip) or nameof(Enumerable.Take)) {
        return _asModels(call);
      }
    }

    // As written, over the models: the group itself is the rows projected to models.
    var models = visitedSource == _models ? _asModels(_rows) : visitedSource;
    return node.Update(null, [models, .. rest.Select(a => Visit(a))]);
  }

  /// <summary>
  /// The operator applied to <paramref name="rows"/>, every lambda over a model rewritten over a row; null when an
  /// argument has no row form.
  /// </summary>
  private MethodCallExpression? _onRows(MethodInfo method, Expression rows, Expression[] rest) {
    var arguments = new Expression[rest.Length + 1];
    arguments[0] = rows;
    for (var i = 0; i < rest.Length; i++) {
      if (rest[i] is LambdaExpression lambda) {
        if (lambda.Parameters.Count != 1 || lambda.Parameters[0].Type != _model) {
          return null;
        }
        arguments[i + 1] = _overRow(lambda);
      } else {
        arguments[i + 1] = Visit(rest[i]);
      }
    }

    var types = method.GetGenericArguments();
    types[0] = _row;
    var onRows = method.GetGenericMethodDefinition().MakeGenericMethod(types);
    var parameters = onRows.GetParameters();
    return arguments.Select((a, i) => parameters[i].ParameterType.IsAssignableFrom(a.Type)).All(fits => fits)
      ? Expression.Call(null, onRows, arguments)
      : null;
  }

  /// <summary><c>x =&gt; f(x)</c> over a model as <c>r =&gt; f(r.Data)</c> over its row.</summary>
  private LambdaExpression _overRow(LambdaExpression lambda) {
    var row = Expression.Parameter(_row, lambda.Parameters[0].Name);
    var body = new ParameterReplacer(lambda.Parameters[0], Expression.Property(row, ROW_DATA)).Visit(lambda.Body);
    return Expression.Lambda(Visit(body), row);
  }

  /// <summary><c>rows.Select(r =&gt; r.Data)</c>, remembered as standing for <paramref name="rows"/>.</summary>
  private MethodCallExpression _asModels(Expression rows) {
    var row = Expression.Parameter(_row, "r");
    var models = Expression.Call(null, _select.MakeGenericMethod(_row, _model), rows, Expression.Lambda(Expression.Property(row, ROW_DATA), row));
    _rowsBehind[models] = rows;
    return models;
  }

  private sealed class ParameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor {
    protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
  }
}
