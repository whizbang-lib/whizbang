// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Reads a <c>Split</c> model's promoted columns beside its document when a query's result is the projected
/// document, so the fields arrive filled (#2304).
/// </summary>
/// <remarks>
/// <para>
/// A query whose result is <c>Select(rows, r =&gt; r.Data)</c> over a model registered in
/// <see cref="SplitDocumentProjectionRegistry"/> has its selector rewritten to
/// <c>r =&gt; SplitDocumentProjectionRegistry.Hydrate(r.Data, new object?[] { EF.Property&lt;T1&gt;(r, "c1"), ... })</c>.
/// EF Core reads the columns with the document and calls <c>Hydrate</c> on the client, in the final projection.
/// </para>
/// <para>
/// Only a projection that is the query's result is rewritten: one reached from the root through nothing but
/// paging (<c>Skip</c>, <c>Take</c>) and the parameterless element operators (<c>First</c>, <c>Single</c>,
/// <c>Last</c>, <c>ElementAt</c> and their <c>OrDefault</c> forms), which a client-evaluated projection allows.
/// A count, a filter over the projected document, or any other operator after it leaves the query as it was.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#reading-promoted-fields-back</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitModeProductionTests.cs</tests>
[SuppressMessage("AOT", "IL2060:MakeGenericMethod can break functionality when AOT compiling", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("Trimming", "IL2072:Target parameter argument does not satisfy annotation requirements", Justification = "Looks up the query's own row type in the EF Core model, which already maps and keeps it")]
[SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Builds EF.Property and the registry call over the query's own model types; EF Core query translation is reflection-based throughout")]
public static class SplitDocumentProjectionRewriter {
  private const string ROW_DATA = "Data";

  private static readonly MethodInfo _hydrate =
    typeof(SplitDocumentProjectionRegistry).GetMethod(nameof(SplitDocumentProjectionRegistry.Hydrate))!;

  private static readonly MethodInfo _efProperty = typeof(EF).GetMethod(nameof(EF.Property))!;

  /// <summary>The operators a client-evaluated final projection may still be followed by.</summary>
  private static readonly HashSet<string> _passThrough = new(StringComparer.Ordinal) {
    nameof(Queryable.Skip), nameof(Queryable.Take),
    nameof(Queryable.First), nameof(Queryable.FirstOrDefault),
    nameof(Queryable.Single), nameof(Queryable.SingleOrDefault),
    nameof(Queryable.Last), nameof(Queryable.LastOrDefault),
    nameof(Queryable.ElementAt), nameof(Queryable.ElementAtOrDefault),
  };

  /// <summary>The query with its final document projection hydrated, or unchanged.</summary>
  /// <param name="query">The query expression.</param>
  /// <param name="model">The context's EF Core model, which knows each column's type.</param>
  /// <returns>The rewritten query, or <paramref name="query"/> itself.</returns>
  public static Expression Rewrite(Expression query, IModel? model) {
    if (model is null) {
      return query;
    }

    if (query is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable)) {
      if (_passThrough.Contains(call.Method.Name) && _hasOnlyValueArguments(call)) {
        var source = Rewrite(call.Arguments[0], model);
        return source == call.Arguments[0] ? call : call.Update(null, [source, .. call.Arguments.Skip(1)]);
      }

      if (call.Method.Name == nameof(Queryable.Select) && _hydrated(call, model) is { } hydrated) {
        return hydrated;
      }
    }

    return query;
  }

  /// <summary>
  /// True for a parameterless element operator or a paging operator: every argument after the source is a value,
  /// not a lambda, so the operator does not reach into the projected document.
  /// </summary>
  private static bool _hasOnlyValueArguments(MethodCallExpression call) =>
    call.Arguments.Skip(1).All(a => a is not UnaryExpression { NodeType: ExpressionType.Quote } and not LambdaExpression);

  /// <summary>The identity document projection of a registered Split model, rewritten to read its columns; null otherwise.</summary>
  private static MethodCallExpression? _hydrated(MethodCallExpression select, IModel model) {
    if (select.Arguments.Count != 2
        || _unquote(select.Arguments[1]) is not LambdaExpression { Parameters.Count: 1, Body: MemberExpression { Member.Name: ROW_DATA } data } selector
        || data.Expression != selector.Parameters[0]
        || !PhysicalFieldExpressionVisitor.IsPerspectiveRowType(selector.Parameters[0].Type)) {
      return null;
    }

    if (!SplitDocumentProjectionRegistry.TryGet(data.Type, out var registration)
        || model.FindEntityType(selector.Parameters[0].Type) is not { } entityType) {
      return null;
    }

    var row = selector.Parameters[0];
    var columns = new List<Expression>(registration.Columns.Count);
    foreach (var column in registration.Columns) {
      if (entityType.FindProperty(column) is not { } property) {
        return null; // a column the context does not map: leave the projection as it was
      }

      var read = Expression.Call(null, _efProperty.MakeGenericMethod(property.ClrType), row, Expression.Constant(column));
      columns.Add(Expression.Convert(read, typeof(object)));
    }

    var hydrate = Expression.Call(null, _hydrate.MakeGenericMethod(data.Type), data, Expression.NewArrayInit(typeof(object), columns));
    return select.Update(null, [select.Arguments[0], Expression.Quote(Expression.Lambda(hydrate, row))]);
  }

  private static Expression _unquote(Expression expression) {
    while (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote) {
      expression = quote.Operand;
    }

    return expression;
  }
}
