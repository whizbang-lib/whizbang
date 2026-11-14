// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2304: which queries the projected-document rewrite touches, and what the registered copy does. Only a query whose last
/// projection reads a registered Split model's document, followed by nothing but paging or an element operator without
/// a predicate, is rewritten; every other shape is left exactly as it was. Expression trees only: nothing here runs a
/// query.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/QueryTranslation/SplitDocumentProjectionRewriter.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/SplitDocumentRegistry.cs</code-under-test>
[Category("Shard2")]
public class SplitDocumentRegistryTests {
  /// <summary>A Split model registered here; no other test registers it.</summary>
  public sealed class ProjectionProbeModel {
    public string? Status { get; set; }
    public string? Note { get; set; }
  }

  /// <summary>A model with no registration, as an Extracted model has none.</summary>
  public sealed class UnregisteredProbeModel {
    public string? Status { get; set; }
  }

  /// <summary>Not a perspective row, though it has a document-like property.</summary>
  public sealed class NotARow {
    public ProjectionProbeModel Data { get; set; } = new();
  }

  private static readonly Expression<Func<PerspectiveRow<ProjectionProbeModel>, ProjectionProbeModel>> _projection =
    r => SplitDocumentRegistry.WithColumns(r.Data, new object?[] { EF.Property<string?>(r, "status") });

  static SplitDocumentRegistryTests() {
    SplitDocumentRegistry.Register<ProjectionProbeModel>(_projection, (data, columns) => {
      data.Status = (string?)columns[0];
      return data;
    });
  }

  private static IQueryable<PerspectiveRow<ProjectionProbeModel>> _rows() =>
    Array.Empty<PerspectiveRow<ProjectionProbeModel>>().AsQueryable();

  private static Expression _element(string name, Expression source) =>
    Expression.Call(typeof(Queryable), name, [typeof(ProjectionProbeModel)], source);

  private static LambdaExpression? _selectorOf(Expression rewritten) {
    var node = rewritten;
    while (node is MethodCallExpression call && call.Method.Name != nameof(Queryable.Select)) {
      node = call.Arguments[0];
    }
    return ((node as MethodCallExpression)?.Arguments[1] as UnaryExpression)?.Operand as LambdaExpression;
  }

  [Test]
  public async Task Rewrite_TheDocumentProjection_FollowedByPagingAndAnElementOperator_IsTheRegisteredProjectionAsync() {
    foreach (var name in new[] { "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault" }) {
      var query = _element(name, _rows().Select(r => r.Data).Skip(1).Take(2).Expression);

      var rewritten = SplitDocumentProjectionRewriter.Rewrite(query);

      await Assert.That(_selectorOf(rewritten)).IsSameReferenceAs(_projection)
        .Because($"{name} after paging is applied in the database, before the copy runs");
    }
  }

  [Test]
  public async Task Rewrite_TheDocumentProjectionAsTheLastOperator_IsRewrittenAsync() {
    var rewritten = SplitDocumentProjectionRewriter.Rewrite(_rows().Select(r => r.Data).Expression);

    await Assert.That(_selectorOf(rewritten)).IsSameReferenceAs(_projection);
  }

  [Test]
  public async Task Rewrite_AnyOtherShape_IsLeftAsItWasAsync() {
    var rows = _rows();
    Expression[] untouched = [
      // An element operator with a predicate, and a filter, run over the copied model: not in the database.
      Expression.Call(typeof(Queryable), "First", [typeof(ProjectionProbeModel)],
        rows.Select(r => r.Data).Expression, Expression.Quote((Expression<Func<ProjectionProbeModel, bool>>)(m => m.Note == "x"))),
      rows.Select(r => r.Data).Where(m => m.Note == "x").Expression,
      // Paging over something that is not the document projection.
      rows.Where(r => r.Id != Guid.Empty).Skip(1).Expression,
      // A projection of something other than the document, or with an index.
      rows.Select(r => r.Data.Note).Expression,
      rows.Select((r, i) => r.Data).Expression,
      // The document of another row than the lambda's own, and a document-shaped property of another type.
      rows.Select(r => _rows().First().Data).Expression,
      new[] { new NotARow() }.AsQueryable().Select(n => n.Data).Expression,
      // A model with no registration, and a query that is not a call at all.
      Array.Empty<PerspectiveRow<UnregisteredProbeModel>>().AsQueryable().Select(r => r.Data).Expression,
      rows.Expression,
    ];

    foreach (var query in untouched) {
      await Assert.That(SplitDocumentProjectionRewriter.Rewrite(query)).IsSameReferenceAs(query);
    }
  }

  [Test]
  public async Task Rewrite_AnOperatorFromOutsideQueryable_IsLeftAsItWasAsync() {
    // Built as EF Core builds it: over an in-memory source the extension returns the source and records nothing.
    var query = Expression.Call(typeof(EntityFrameworkQueryableExtensions), nameof(EntityFrameworkQueryableExtensions.AsNoTracking),
      [typeof(ProjectionProbeModel)], _rows().Select(r => r.Data).Expression);

    await Assert.That(SplitDocumentProjectionRewriter.Rewrite(query)).IsSameReferenceAs(query);
  }

  [Test]
  public async Task WithColumns_CopiesTheColumnsIntoARegisteredModel_AndLeavesAnyOtherAsItIsAsync() {
    var document = new ProjectionProbeModel { Note = "document" };
    var other = new UnregisteredProbeModel { Status = "kept" };

    var filled = SplitDocumentRegistry.WithColumns(document, ["from-column"]);

    await Assert.That(filled.Status).IsEqualTo("from-column");
    await Assert.That(filled.Note).IsEqualTo("document");
    await Assert.That(SplitDocumentRegistry.WithColumns(other, ["ignored"]).Status).IsEqualTo("kept");
    await Assert.That(SplitDocumentRegistry.WithColumns<ProjectionProbeModel>(null, ["ignored"])).IsNull()
      .Because("a row whose document is null has nothing to copy into");
  }

  [Test]
  public async Task Register_RequiresAProjectionAndACopyAsync() {
    await Assert.That(() => SplitDocumentRegistry.Register<UnregisteredProbeModel>(null!, (d, _) => d)).Throws<ArgumentNullException>();
    await Assert.That(() => SplitDocumentRegistry.Register<UnregisteredProbeModel>(r => r.Data, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Document_HoldsTheStoredDocument_UntilTheCopyReplacesItAsync() {
    var stored = new ProjectionProbeModel();
    var replacement = new ProjectionProbeModel();
    var document = new SplitDocumentRegistry.Document<ProjectionProbeModel>(stored);
    await Assert.That(document.Data).IsSameReferenceAs(stored);

    document.Data = replacement;

    await Assert.That(document.Data).IsSameReferenceAs(replacement);
  }
  private static readonly string[] _elementOperators =
    ["First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault"];

  [Test]
  public async Task Rewrite_PagingAndEveryElementOperatorWithoutAPredicate_PassThroughToTheProjectionAsync() {
    var projection = _rows().Select(r => r.Data).Expression;
    Expression[] passing = [
      Expression.Call(typeof(Queryable), nameof(Queryable.Skip), [typeof(ProjectionProbeModel)], projection, Expression.Constant(1)),
      Expression.Call(typeof(Queryable), nameof(Queryable.Take), [typeof(ProjectionProbeModel)], projection, Expression.Constant(2)),
      .. _elementOperators.Select(name => _element(name, projection)),
    ];

    foreach (var query in passing) {
      await Assert.That(_selectorOf(SplitDocumentProjectionRewriter.Rewrite(query))).IsSameReferenceAs(_projection)
        .Because($"{((MethodCallExpression)query).Method.Name} is applied in the database, before the copy runs");
    }
  }

  [Test]
  public async Task Rewrite_EveryElementOperatorWithAPredicate_OrAnyOtherOperator_IsLeftAsItWasAsync() {
    var projection = _rows().Select(r => r.Data).Expression;
    var predicate = Expression.Quote((Expression<Func<ProjectionProbeModel, bool>>)(m => m.Note == "x"));
    Expression[] stopping = [
      .. _elementOperators.Select(name =>
        Expression.Call(typeof(Queryable), name, [typeof(ProjectionProbeModel)], projection, predicate)),
      Expression.Call(typeof(Queryable), nameof(Queryable.Distinct), [typeof(ProjectionProbeModel)], projection),
      Expression.Call(typeof(Queryable), nameof(Queryable.Count), [typeof(ProjectionProbeModel)], projection),
    ];

    foreach (var query in stopping) {
      await Assert.That(SplitDocumentProjectionRewriter.Rewrite(query)).IsSameReferenceAs(query)
        .Because($"{((MethodCallExpression)query).Method.Name} here runs over the copied model, so the query keeps its shape");
    }
  }
}
