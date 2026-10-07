// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Model = Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation.PhysicalJsonbContainmentSqlTests.JsonbColumnsModel;
using Row = Whizbang.Core.Lenses.PerspectiveRow<Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation.PhysicalJsonbContainmentSqlTests.JsonbColumnsModel>;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// What <see cref="PhysicalJsonbContainmentRewriter"/> claims and what it leaves alone, read off the rewritten
/// expression tree: every shape outside the supported set is returned without a containment test in it, so the
/// supported set the documentation lists is exactly the one compiled.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-filters</docs>
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
[SuppressMessage("Readability", "RCS1118:Mark local variable as const",
  Justification = "Captured on purpose: a const local is inlined as a literal and the parameterized path would go untested.")]
[SuppressMessage("Performance", "CA1829:Use Length/Count property instead of Count() when available",
  Justification = "Count() over the column is a shape under test.")]
[SuppressMessage("Performance", "S2971:Use 'Count' property here instead",
  Justification = "Count() over the column is a shape under test.")]
public class PhysicalJsonbContainmentRewriterTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=jsonbrewrite;Username=u;Password=p";

  private static readonly DbContextOptions<PhysicalJsonbContainmentSqlTests.JsonbColumnsDbContext> _options =
    new DbContextOptionsBuilder<PhysicalJsonbContainmentSqlTests.JsonbColumnsDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  /// <summary>How many containment tests the rewrite put into the filter.</summary>
  private static int _containments(Expression<Func<Row, bool>> filter, bool withModel = true) {
    Model.Register();
    using var context = new PhysicalJsonbContainmentSqlTests.JsonbColumnsDbContext(_options);
    var query = context.Set<Row>().Where(filter).Expression;
    var redirected = new PhysicalFieldExpressionVisitor().Visit(query);
    var rewritten = new PhysicalJsonbContainmentRewriter(withModel ? context.Model : null).Visit(redirected);
    var counter = new ContainmentCounter();
    counter.Visit(rewritten);
    return counter.Count;
  }

  // ------------------------------------------------------------------
  // Claimed
  // ------------------------------------------------------------------

  [Test]
  public async Task SupportedShapes_AreEachOneContainmentTestAsync() {
    var region = "north";
    var tag = "red";
    var key = "k";

    await Assert.That(_containments(r => r.Data.GridFilter["region"].Contains(region))).IsEqualTo(1);
    await Assert.That(_containments(r => r.Data.GridFilter[key].Any(v => v == region))).IsEqualTo(1);
    await Assert.That(_containments(r => r.Data.GridFilter[key].Any(v => region == v))).IsEqualTo(1);
    await Assert.That(_containments(r => r.Data.Counts["open"] == 3)).IsEqualTo(1);
    await Assert.That(_containments(r => r.Data.Tags.Contains(tag))).IsEqualTo(1);
    await Assert.That(_containments(r => Enumerable.Contains(r.Data.Tags, tag))).IsEqualTo(1);
    await Assert.That(_containments(r => r.Data.Labels.Any(l => l.Key == key && l.Label == tag))).IsEqualTo(1);
    await Assert.That(_containments(r => r.Data.Location!.Address.City == tag)).IsEqualTo(1);
    await Assert.That(_containments(r => r.Data.Bare!.A == tag)).IsEqualTo(1)
      .Because("metadata without an attribute provider names a member by its own name.");
    await Assert.That(_containments(r => r.Data.Bare!.B == tag)).IsEqualTo(0)
      .Because("a member that metadata does not list has no stored name.");
    await Assert.That(_containments(r => r.Data.Location!.Note == tag)).IsEqualTo(1)
      .Because("a member ignored only when null is stored under its name whenever it has a value.");
    await Assert.That(_containments(r => !r.Data.Tags.Contains(tag))).IsEqualTo(1);
    await Assert.That(_containments(r => r.Data.Tags.Contains(null!))).IsEqualTo(1)
      .Because("a null element is a stored null element, which is what the in-memory Contains matches.");
  }

  /// <summary>
  /// The expression a GraphQL list filter produces: HotChocolate's <c>some</c> on a list of objects is
  /// <c>Enumerable.Any(list, element =&gt; element.Member == constant)</c>, built with the expression API.
  /// </summary>
  [Test]
  public async Task TheShapeAGraphQLListFilterBuilds_IsClaimedAsync() {
    var row = Expression.Parameter(typeof(Row), "r");
    var labels = Expression.Property(Expression.Property(row, nameof(Row.Data)), nameof(Model.Labels));
    var element = Expression.Parameter(typeof(PhysicalJsonbContainmentSqlTests.JsonbLabel), "l");
    var some = Expression.Lambda(
      Expression.Equal(Expression.Property(element, nameof(PhysicalJsonbContainmentSqlTests.JsonbLabel.Key)), Expression.Constant("team")),
      element);
    var any = Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), [element.Type], labels, some);

    await Assert.That(_containments(Expression.Lambda<Func<Row, bool>>(any, row))).IsEqualTo(1);
  }

  [Test]
  public async Task AsyncAndCountOperators_ArePredicatesTooAsync() {
    Model.Register();
    await using var context = new PhysicalJsonbContainmentSqlTests.JsonbColumnsDbContext(_options);
    var tag = "red";
    Expression<Func<IQueryable<Row>, int>> count = rows => rows.Count(r => r.Data.Tags.Contains(tag));
    var redirected = new PhysicalFieldExpressionVisitor().Visit(count.Body);
    var counter = new ContainmentCounter();
    counter.Visit(new PhysicalJsonbContainmentRewriter(context.Model).Visit(redirected));

    await Assert.That(counter.Count).IsEqualTo(1);
  }

  /// <summary>An asynchronous operator's name is read without its suffix, as the document rewrite reads it.</summary>
  [Test]
  public async Task AnAsyncFilteringOperator_IsAPredicateTooAsync() {
    Model.Register();
    await using var context = new PhysicalJsonbContainmentSqlTests.JsonbColumnsDbContext(_options);
    var tag = "red";
    Expression<Func<Row, bool>> predicate = r => r.Data.Tags.Contains(tag);
    var source = ((IQueryable<Row>)context.Set<Row>()).Expression;
    var call = Expression.Call(
      typeof(PhysicalJsonbContainmentRewriterTests).GetMethod(nameof(FirstAsync))!, source, Expression.Quote(predicate));
    var counter = new ContainmentCounter();
    counter.Visit(new PhysicalJsonbContainmentRewriter(context.Model).Visit(new PhysicalFieldExpressionVisitor().Visit(call)));

    await Assert.That(counter.Count).IsEqualTo(1);
  }

  /// <summary>Stands in for an asynchronous filtering operator in a hand-built tree; never called.</summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1047:Non-asynchronous method name should not end with 'Async'",
    Justification = "Named after the async query operator it stands in for: the rewriter is tested against that name.")]
  public static Row? FirstAsync(IQueryable<Row> source, Expression<Func<Row, bool>> predicate) =>
    throw new NotSupportedException();

  // ------------------------------------------------------------------
  // Left alone
  // ------------------------------------------------------------------

  [Test]
  [SuppressMessage("Usage", "CA2263", Justification = "The non-generic spelling is a shape under test.")]
  public async Task UnsupportedShapes_AreLeftAsWrittenAsync() {
    var tag = "red";
    var key = "k";
    var when = DateTime.UtcNow;
    Func<string, bool> isRed = v => v == "red";
    var place = new PhysicalJsonbContainmentSqlTests.JsonbLocation();

    var shapes = new List<(string Name, Expression<Func<Row, bool>> Filter, bool WithModel, int Expected)> {
#pragma warning disable RCS1077 // The unoptimized shape is the input under test: the rewriter must stand down on it.
      ("r => r.Data.Tags.Count() > 1", r => r.Data.Tags.Count() > 1, true, 0),
#pragma warning restore RCS1077
      ("r => r.Data.GridFilter.ContainsKey(key)", r => r.Data.GridFilter.ContainsKey(key), true, 0),
      ("r => r.Data.Location!.Zone > 3", r => r.Data.Location!.Zone > 3, true, 0),
      ("r => r.Data.Location!.Address.City.Contains(tag)", r => r.Data.Location!.Address.City.Contains(tag), true, 0),
      ("r => r.Data.Tags.Contains(tag, StringComparer.Ordinal)", r => r.Data.Tags.Contains(tag, StringComparer.Ordinal), true, 0),
      ("r => r.Data.Tags.Any()", r => r.Data.Tags.Any(), true, 0),
      ("r => r.Data.Tags.Any(isRed)", r => r.Data.Tags.Any(isRed), true, 0),
      ("r => r.Data.Labels.AsQueryable().Any(l => l.Key == key)", r => r.Data.Labels.AsQueryable().Any(l => l.Key == key), true, 0),
      ("r => r.Data.Location!.Zone == r.Version", r => r.Data.Location!.Zone == r.Version, true, 0),
      ("r => r.Data.Tags.Contains(r.Data.Title)", r => r.Data.Tags.Contains(r.Data.Title), true, 0),
      ("r => r.Data.GridFilter[r.Data.Title].Contains(tag)", r => r.Data.GridFilter[r.Data.Title].Contains(tag), true, 0),
      ("r => r.Data.Labels.Any(l => l.Key == l.Label)", r => r.Data.Labels.Any(l => l.Key == l.Label), true, 0),
      ("r => r.Data.Location!.Address.City == null", r => r.Data.Location!.Address.City == null, true, 0),
      ("r => r.Data.Labels.Any(l => l.Key == null)", r => r.Data.Labels.Any(l => l.Key == null), true, 0),
      ("r => r.Data.Location == null", r => r.Data.Location == null, true, 0),
      ("r => r.Data.Location == place", r => r.Data.Location == place, true, 0),
      ("r => r.Data.Stamps.Contains(when)", r => r.Data.Stamps.Contains(when), true, 0),
      ("r => r.Data.Labels.Any(l => l.Key == key || l.Label == tag)", r => r.Data.Labels.Any(l => l.Key == key || l.Label == tag), true, 0),
      ("r => r.Data.Tags.Any(v => v == key && v == tag)", r => r.Data.Tags.Any(v => v == key && v == tag), true, 0),
      ("r => r.Data.Places.Any(p => p.Address.City == key && p.Address.City == tag)", r => r.Data.Places.Any(p => p.Address.City == key && p.Address.City == tag), true, 0),
      ("r => r.Data.Rows.Any(d => d[key] == tag)", r => r.Data.Rows.Any(d => d[key] == tag), true, 0),
      ("r => r.Data.Rows.Any(d => d[\"a\"] == tag && d[\"b\"] == key)", r => r.Data.Rows.Any(d => d["a"] == tag && d["b"] == key), true, 1),
      ("r => r.Data.Title == tag", r => r.Data.Title == tag, true, 0),
      ("r => r.Data.Location!.Hidden == tag", r => r.Data.Location!.Hidden == tag, true, 0),
      ("r => r.Data.GridFilter.Count == 1", r => r.Data.GridFilter.Count == 1, true, 0),
      ("r => r.Data.ByNumber[1] == tag", r => r.Data.ByNumber[1] == tag, true, 0),
      ("r => r.Data.Other!.Name == tag", r => r.Data.Other!.Name == tag, true, 0),
      ("r => _local(r.Data).Tags.Contains(tag)", r => _local(r.Data).Tags.Contains(tag), true, 0),
      ("r => r.Data.Tags.Contains(tag)", r => r.Data.Tags.Contains(tag), false, 0),
      // EF.Property for a name the row does not map, and over something that is not an entity at all:
      // neither is a jsonb column, so neither is claimed.
      ("r => EF.Property<List<string>>(r, \"NoSuchColumn\").Contains(tag)",
        r => EF.Property<List<string>>(r, "NoSuchColumn").Contains(tag), true, 0),
      ("r => EF.Property<List<string>>(r.Data, \"Tags\").Contains(tag)",
        r => EF.Property<List<string>>(r.Data, "Tags").Contains(tag), true, 0),
    };

    var wrong = shapes.Where(s => _containments(s.Filter, s.WithModel) != s.Expected).Select(s => s.Name).ToList();

    await Assert.That(wrong).IsEmpty();
  }

  [Test]
  public async Task OutsideAFilter_NothingIsRewrittenAsync() {
    Model.Register();
    await using var context = new PhysicalJsonbContainmentSqlTests.JsonbColumnsDbContext(_options);
    var tag = "red";
    var query = context.Set<Row>().Select(r => r.Data.Tags.Contains(tag)).Expression;
    var counter = new ContainmentCounter();
    counter.Visit(new PhysicalJsonbContainmentRewriter(context.Model).Visit(new PhysicalFieldExpressionVisitor().Visit(query)));

    await Assert.That(counter.Count).IsEqualTo(0);
  }

  [Test]
  public async Task AModelWithoutTheMarkers_StandsDownAsync() {
    var withoutMarkers = new DbContextOptionsBuilder<BareDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;
    await using var context = new BareDbContext(withoutMarkers);
    var tag = "red";
    Expression<Func<IQueryable<Row>, IQueryable<Row>>> query = rows => rows.Where(r => r.Data.Tags.Contains(tag));
    var counter = new ContainmentCounter();
    counter.Visit(new PhysicalJsonbContainmentRewriter(context.Model).Visit(query.Body));

    await Assert.That(counter.Count).IsEqualTo(0);
  }

  private static Model _local(Model model) => model;

  private sealed class BareDbContext(DbContextOptions<BareDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<Row>(e => {
        e.HasKey(r => r.Id);
        e.Ignore(r => r.Data);
        e.Ignore(r => r.Metadata);
        e.Ignore(r => r.Scope);
      });
  }

  private sealed class ContainmentCounter : ExpressionVisitor {
    public int Count { get; private set; }

    protected override Expression VisitMethodCall(MethodCallExpression node) {
      if (node.Method.Name == nameof(NpgsqlJsonDbFunctionsExtensions.JsonContains)) {
        Count++;
      }

      return base.VisitMethodCall(node);
    }
  }
}
