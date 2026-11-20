// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// The grouping and aggregate moves of <see cref="StreamIdPrimaryKeyRewriter"/> and <see cref="GroupedRowsRewriter"/>
/// (#2424), on in-memory rows: each rewritten query returns exactly what it returned before, and reads the row where
/// the row form exists. No database: the in-memory provider runs both trees.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields</docs>
[Category("Unit")]
[Category("Shard1")]
public class GroupedRowsRewriterTests {
  /// <summary>A model a perspective row carries.</summary>
  public sealed class TicketModel {
    public string Status { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string Title { get; init; } = string.Empty;
  }

  private static readonly Type _row = typeof(PerspectiveRow<TicketModel>);
  private static readonly DateTime _at = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

  private static readonly IQueryable<PerspectiveRow<TicketModel>> _rows = new[] {
    _ticket("open", 5m, "a1"), _ticket("open", 12m, "b2"), _ticket("closed", 40m, "a3"), _ticket("open", 30m, "c4"),
  }.AsQueryable();

  private static readonly IQueryable<TicketModel> _models = _rows.Select(r => r.Data);

  private static readonly string[] _openStatuses = ["open"];

  private static PerspectiveRow<TicketModel> _ticket(string status, decimal price, string title) => new() {
    Id = new Guid($"01937000-0000-7000-8000-{(int)price:x12}"),
    Data = new TicketModel { Status = status, Price = price, Title = title },
    Metadata = new PerspectiveMetadata(),
    Scope = new PerspectiveScope(),
    CreatedAt = _at,
    UpdatedAt = _at,
    Version = 1,
  };

  /// <summary>A static generic method that is not an <c>Enumerable</c> operator.</summary>
  public static T Pass<T>(T value) => value;

  /// <summary>A static, non-generic method returning a query, as a source no rewrite recognizes. Only ever in a tree.</summary>
  public static IQueryable<IGrouping<string, TicketModel>> Same(IQueryable<IGrouping<string, TicketModel>> groups) => groups;

  /// <summary><c>Select(g =&gt; g.Key)</c> over a source built as a tree, so a call in it is not run while building.</summary>
  private static MethodCallExpression _selectKeys(Expression groups) {
    Expression<Func<IGrouping<string, TicketModel>, string>> key = g => g.Key;
    return Expression.Call(
      typeof(Queryable), nameof(Queryable.Select), [typeof(IGrouping<string, TicketModel>), typeof(string)], groups, Expression.Quote(key));
  }

  private static Expression _rewrite(Expression expression) => new StreamIdPrimaryKeyRewriter().Visit(expression);

  private static List<T> _run<T>(IQueryable<T> query) => [.. _rows.Provider.CreateQuery<T>(_rewrite(query.Expression))];

  /// <summary>Every parameter type of every lambda in the tree.</summary>
  private static List<Type> _lambdaParameterTypes(Expression expression) {
    var finder = new LambdaFinder();
    finder.Visit(expression);
    return finder.Types;
  }

  private sealed class LambdaFinder : ExpressionVisitor {
    public List<Type> Types { get; } = [];

    protected override Expression VisitLambda<T>(Expression<T> node) {
      Types.AddRange(node.Parameters.Select(p => p.Type));
      return base.VisitLambda(node);
    }
  }

  private static bool _readsGroupsOfRows(Expression expression) =>
    _lambdaParameterTypes(expression).Contains(typeof(IGrouping<string, PerspectiveRow<TicketModel>>));

  [Test]
  public async Task AProjectionOverGroups_ReadsTheRows_AndReturnsWhatItDidAsync() {
    var query = _models.GroupBy(x => x.Status).Select(g => new {
      g.Key,
      Total = g.Sum(x => Pass(x.Price)),
      Filtered = g.Where(x => x.Price > 6m).Skip(0).Take(5).Count(),
      Cheapest = g.OrderBy(x => x.Price).Select(x => x.Title).FirstOrDefault(),
      StartsWithA = g.Count(x => x.Title.StartsWith('a')),
      Nested = g.Count(x => x.Title.Any(c => c == '2')),
      // Written as a projection and a non-generic Sum on purpose: the Sum is an operator the rewrite leaves alone.
#pragma warning disable RCS1077
      Prices = g.Select(x => x.Price).Sum(),
#pragma warning restore RCS1077
      Indexed = g.Where((x, i) => i == 0).Count(),
      Joined = g.Join(_openStatuses, x => x.Status, s => s, (x, s) => x.Price).Sum(),
      HasFirst = g.Contains(_models.First()),
    }).OrderBy(g => g.Key);

    await Assert.That(_run(query)).IsEquivalentTo(query.ToList(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(_readsGroupsOfRows(_rewrite(query.Expression))).IsTrue()
      .Because("every use of the group has a row form, so the projection moves to the groups of rows");
  }

  [Test]
  public async Task AProjectionThatUsesTheGroupAsAValue_StaysOverTheModelsAsync() {
    var query = _models.GroupBy(x => x.Status).Select(g => new { g.Key, Size = g.ToList().Count, Group = g })
      .Select(g => new { g.Key, g.Size, Same = g.Group.Count() == g.Size });

    await Assert.That(_run(query)).IsEquivalentTo(query.ToList(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(_readsGroupsOfRows(_rewrite(query.Expression))).IsFalse()
      .Because("a group of models has no row form, so the projection is left over the models");
  }

  [Test]
  public async Task AnElementSelector_AResultSelector_AndBoth_ReadTheRowsAsync() {
    var elements = _models.GroupBy(x => x.Status, x => x.Price).Select(g => new { g.Key, Total = g.Sum() });
    var results = _models.GroupBy(x => x.Status, (key, members) => new { Key = key, Total = members.Sum(x => x.Price), All = members });
    var both = _models.GroupBy(x => x.Status, x => x.Price, (key, prices) => new { Key = key, Total = prices.Sum() });

    await Assert.That(_run(elements)).IsEquivalentTo(elements.ToList());
    await Assert.That(_run(results).Select(r => (r.Key, r.Total, r.All.Count()))).IsEquivalentTo(results.AsEnumerable().Select(r => (r.Key, r.Total, r.All.Count())));
    await Assert.That(_run(both)).IsEquivalentTo(both.ToList());
    foreach (var query in new[] { elements.Expression, results.Expression, both.Expression }) {
      await Assert.That(_lambdaParameterTypes(_rewrite(query))).Contains(_row)
        .Because("the key and the selectors read the row");
    }
  }

  [Test]
  public async Task AnAggregateOverTheProjection_IsTheSameAggregateOverTheRowsAsync() {
    Expression<Func<IQueryable<TicketModel>, object>>[] aggregates = [
      q => q.Sum(x => x.Price), q => q.Min(x => x.Price), q => q.Max(x => x.Price), q => q.Average(x => x.Price),
      q => q.Count(x => x.Price > 10m), q => q.LongCount(x => x.Price > 10m), q => q.Any(x => x.Price > 35m), q => q.All(x => x.Price > 1m),
    ];

    foreach (var aggregate in aggregates) {
      var call = (MethodCallExpression)new ParameterSwap(aggregate.Parameters[0], _models.Expression).Visit(((UnaryExpression)aggregate.Body).Operand)!;
      var rewritten = (MethodCallExpression)_rewrite(call);

      await Assert.That(rewritten.Arguments[0]).IsSameReferenceAs(_rows.Expression)
        .Because($"{call.Method.Name} reads the rows directly");
      await Assert.That(_rows.Provider.Execute(rewritten)).IsEqualTo(_rows.Provider.Execute(call));
    }
  }

  [Test]
  public async Task WhatNoRewriteRecognizes_IsLeftAsWrittenAsync() {
    Expression[] untouched = [
      _models.GroupBy(x => x.Status, StringComparer.Ordinal).Expression,
      _models.OrderBy(x => x.Title, StringComparer.Ordinal).Expression,
      _models.SkipWhile(x => x.Price < 10m).Expression,
      _models.Distinct().Expression,
      _selectKeys(Expression.Call(typeof(GroupedRowsRewriterTests).GetMethod(nameof(Same))!, _models.GroupBy(x => x.Status).Expression)),
      _models.GroupBy(x => x.Status).Select((g, i) => g.Key).Expression,
      _rows.GroupBy(r => r.Data.Status, r => r.Version).Select(g => g.Key).Expression,
    ];

    foreach (var expression in untouched) {
      var rewritten = _rewrite(expression);
      await Assert.That(_lambdaParameterTypes(rewritten).Contains(typeof(IGrouping<string, PerspectiveRow<TicketModel>>))).IsFalse();
    }
    await Assert.That(_rewrite(untouched[0])).IsSameReferenceAs(untouched[0])
      .Because("a grouping with a comparer is not moved");
  }

  [Test]
  public async Task ASelectWhoseProjectionIsNotALambda_IsLeftAsWrittenAsync() {
    var groups = _rows.GroupBy(r => r.Data.Status, r => r.Data);
    Expression<Func<IGrouping<string, TicketModel>, string>> key = g => g.Key;
    var select = Expression.Call(
      typeof(Queryable), nameof(Queryable.Select), [typeof(IGrouping<string, TicketModel>), typeof(string)],
      groups.Expression, Expression.Constant(key, typeof(Expression<Func<IGrouping<string, TicketModel>, string>>)));

    await Assert.That(_rewrite(select)).IsSameReferenceAs(select);
  }

  private sealed class ParameterSwap(ParameterExpression from, Expression to) : ExpressionVisitor {
    protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
  }
}
