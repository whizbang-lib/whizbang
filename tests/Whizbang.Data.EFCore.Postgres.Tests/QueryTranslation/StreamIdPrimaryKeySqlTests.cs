// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Pins that a filter on a model's stream id is also answered by the row's primary key, in both the
/// row form and the projected form an <c>IQueryable</c> composer writes after <c>Select(r =&gt; r.Data)</c>.
/// </summary>
/// <remarks>
/// The original predicate is always kept beside the added <c>id</c> comparison, so these tests check the
/// added comparison is present (or absent) and never that the document predicate went away.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#stream-id-filters</docs>
[Category("Shard1")]
public class StreamIdPrimaryKeySqlTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=shape;Username=u;Password=p";

  /// <summary>A model with a Guid stream id, a promoted field and a document field.</summary>
  [SuppressIndexAdvisory("this model exists to compile queries, never to run them")]
  public class OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }

    [PhysicalField]
    [Indexed]
    public decimal Price { get; init; }

    public string Title { get; init; } = string.Empty;

    public Guid CustomerId { get; init; }

    public List<string> Tags { get; init; } = [];
  }

  /// <summary>A model whose stream id is never registered.</summary>
  [SuppressIndexAdvisory("this model exists to compile queries, never to run them")]
  public class UnregisteredModel {
    [StreamId]
    public Guid UnregisteredId { get; init; }

    public string Title { get; init; } = string.Empty;
  }

  /// <summary>A model whose stream id is nullable, so a document can hold none.</summary>
  [SuppressIndexAdvisory("this model exists to compile queries, never to run them")]
  public class DraftModel {
    [StreamId]
    public Guid? DraftId { get; init; }

    public string Title { get; init; } = string.Empty;
  }

  private sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<OrderModel>>(entity => {
        _mapRow(entity, "wh_per_order");
        entity.Property<decimal>("price").HasColumnName("price");
      });
      modelBuilder.Entity<PerspectiveRow<UnregisteredModel>>(entity => _mapRow(entity, "wh_per_unregistered"));
      modelBuilder.Entity<PerspectiveRow<DraftModel>>(entity => _mapRow(entity, "wh_per_draft"));
    }

    private static void _mapRow<TModel>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PerspectiveRow<TModel>> entity, string table)
        where TModel : class {
      entity.ToTable(table);
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
      entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
      entity.ComplexProperty(e => e.Scope, s => {
        s.ToJson("scope");
        s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
      });
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
      entity.Property(e => e.Version).HasColumnName("version").IsRequired();
    }
  }

  private static readonly Guid _probeId = new("6f3c1f4e-6a8e-4c63-9d1e-2b7f0f6a1c55");
  private static readonly Guid _otherId = new("0d9a4b1c-3e2f-4a5b-8c7d-6e5f4a3b2c1d");

  private static readonly DbContextOptions<OrderDbContext> _options =
    new DbContextOptionsBuilder<OrderDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private static OrderDbContext _newContext() {
    PhysicalFieldRegistry.Register<OrderModel>("Price", "price");
    StreamIdRegistry.Register<OrderModel>("OrderId");
    StreamIdRegistry.Register<DraftModel>("DraftId");
    return new OrderDbContext(_options);
  }

  private static string _draftSql(Func<IQueryable<DraftModel>, IQueryable<DraftModel>> shape) {
    using var db = _newContext();
    return shape(db.Set<PerspectiveRow<DraftModel>>().Select(r => r.Data)).ToQueryString();
  }

  private static string _rowSql(Func<IQueryable<PerspectiveRow<OrderModel>>, IQueryable<PerspectiveRow<OrderModel>>> shape) {
    using var db = _newContext();
    return shape(db.Set<PerspectiveRow<OrderModel>>()).ToQueryString();
  }

  private static string _projectedSql(Func<IQueryable<OrderModel>, IQueryable<OrderModel>> shape) {
    using var db = _newContext();
    return shape(db.Set<PerspectiveRow<OrderModel>>().Select(r => r.Data)).ToQueryString();
  }

  // ========================================
  // The primary key is added
  // ========================================

  /// <summary>A row-form equality on the stream id is answered by the primary key.</summary>
  [Test]
  public async Task RowForm_StreamIdEquality_AddsPrimaryKeyAsync() {
    var id = _probeId;
    var sql = _rowSql(rows => rows.Where(r => r.Data.OrderId == id));

    await Assert.That(sql).Contains("w.id = @");
  }

  /// <summary>The value may be on either side of the equality.</summary>
  [Test]
  public async Task RowForm_StreamIdEquality_ValueOnTheLeft_AddsPrimaryKeyAsync() {
    var id = _probeId;
    var sql = _rowSql(rows => rows.Where(r => id == r.Data.OrderId));

    await Assert.That(sql).Contains("w.id = @");
  }

  /// <summary>
  /// The shape a GraphQL list field with filtering produces: the repository projects and orders, and the
  /// filter is applied to the projected model afterward.
  /// </summary>
  [Test]
  public async Task ProjectedForm_AfterOrderBy_AddsPrimaryKeyAsync() {
    var id = _probeId;
    var sql = _projectedSql(models => models.OrderBy(x => x.OrderId).Where(x => x.OrderId == id));

    await Assert.That(sql).Contains("w.id = @");
  }

  /// <summary>A stream-id conjunct among others still adds the primary key.</summary>
  [Test]
  public async Task ProjectedForm_StreamIdInConjunction_AddsPrimaryKeyAsync() {
    var id = _probeId;
    var sql = _projectedSql(models => models.Where(x => x.Title == "a" && x.OrderId == id));

    await Assert.That(sql).Contains("w.id = @");
  }

  /// <summary>A set of stream ids is answered by the primary key too.</summary>
  [Test]
  public async Task ProjectedForm_StreamIdContains_AddsPrimaryKeyAsync() {
    var ids = new List<Guid> { _probeId, _otherId };
    var sql = _projectedSql(models => models.Where(x => ids.Contains(x.OrderId)));

    await Assert.That(sql).Contains("w.id = ANY (");
  }

  /// <summary>A set held in an array, whose <c>Contains</c> is a static method rather than the list's own, is answered too.</summary>
  [Test]
  public async Task ProjectedForm_StreamIdInAnArray_AddsPrimaryKeyAsync() {
    var ids = new[] { _probeId, _otherId };
    var sql = _projectedSql(models => models.Where(x => ids.Contains(x.OrderId)));

    await Assert.That(sql).Contains("w.id = ANY (");
  }

  /// <summary>A nullable value lifts the comparison to <c>Guid?</c>, and the row's id is compared the same way.</summary>
  [Test]
  public async Task ProjectedForm_StreamIdEqualsANullableValue_AddsPrimaryKeyAsync() {
    Guid? id = _probeId;
    var sql = _projectedSql(models => models.Where(x => x.OrderId == id));

    await Assert.That(sql).Contains("w.id = @");
  }

  /// <summary>A filter holding a lambda of its own moves beneath the projection with that lambda intact.</summary>
  [Test]
  public async Task ProjectedForm_AFilterWithANestedLambda_MovesBeneathTheProjectionAsync() {
    var id = _probeId;
    var sql = _projectedSql(models => models.Where(x => x.OrderId == id && x.Tags.Any(t => t == "rush")));

    await Assert.That(sql).Contains("w.id = @");
  }

  /// <summary>The original predicate is kept, so results never depend on the two ids agreeing.</summary>
  [Test]
  public async Task ProjectedForm_StreamIdEquality_KeepsTheDocumentPredicateAsync() {
    var id = _probeId;
    var sql = _projectedSql(models => models.Where(x => x.OrderId == id));

    await Assert.That(sql).Contains("OrderId");
  }

  // ========================================
  // Projected filters reach promoted columns
  // ========================================

  /// <summary>A projected filter on a promoted field compares its column, as the row form does.</summary>
  [Test]
  public async Task ProjectedForm_PhysicalField_ReadsTheColumnAsync() {
    var sql = _projectedSql(models => models.Where(x => x.Price >= 50m));

    await Assert.That(sql).Contains("w.price >= ");
    await Assert.That(sql).DoesNotContain("'Price'");
  }

  /// <summary>A projected sort on a promoted field orders by its column, so an index can supply the order.</summary>
  [Test]
  public async Task ProjectedOrderBy_PhysicalField_OrdersByTheColumnAsync() {
    var sql = _projectedSql(models => models.OrderBy(x => x.Price));

    await Assert.That(sql).Contains("ORDER BY w.price");
  }

  /// <summary>A secondary sort follows its primary sort beneath the projection.</summary>
  [Test]
  public async Task ProjectedThenBy_PhysicalField_OrdersByTheColumnAsync() {
    var sql = _projectedSql(models => models.OrderBy(x => x.Title).ThenByDescending(x => x.Price));

    await Assert.That(sql).Contains("w.price DESC");
  }

  /// <summary>Paging after a projected sort keeps the sort on the column.</summary>
  [Test]
  public async Task ProjectedOrderByThenSkip_OrdersByTheColumnAsync() {
    var sql = _projectedSql(models => models.OrderBy(x => x.Price).Skip(5).Take(10));

    await Assert.That(sql).Contains("ORDER BY w.price");
  }

  /// <summary>A projection over the model reads a promoted field from its column.</summary>
  [Test]
  public async Task ProjectedSelect_PhysicalField_ReadsTheColumnAsync() {
    await using var db = _newContext();
    var sql = db.Set<PerspectiveRow<OrderModel>>().Select(r => r.Data).Select(x => new { x.OrderId, x.Price }).ToQueryString();

    await Assert.That(sql).Contains("w.price");
    await Assert.That(sql).DoesNotContain("'Price'");
  }

  // ========================================
  // A read of the stream id reads the row's id (#2423)
  // ========================================

  /// <summary>A row-form projection of the stream id reads the row's id, so the document is not read for it.</summary>
  [Test]
  public async Task RowFormSelect_StreamId_ReadsTheRowIdAsync() {
    await using var db = _newContext();
    var sql = db.Set<PerspectiveRow<OrderModel>>()
      .Where(r => r.Data.Title == "a")
      .Select(r => new { r.Data.OrderId, r.Data.Price })
      .ToQueryString();

    await Assert.That(sql).Contains("SELECT w.id AS \"OrderId\", w.price AS \"Price\"");
    await Assert.That(sql).DoesNotContain("'OrderId'");
  }

  /// <summary>The projected form an <c>IQueryable</c> composer writes, <c>Select(r =&gt; r.Data).Select(x =&gt; x.Id)</c>, reads the row's id.</summary>
  [Test]
  public async Task ProjectedSelect_StreamId_ReadsTheRowIdAsync() {
    await using var db = _newContext();
    var sql = db.Set<PerspectiveRow<OrderModel>>().Select(r => r.Data).Select(x => x.OrderId).ToQueryString();

    await Assert.That(sql).Contains("SELECT w.id");
    await Assert.That(sql).DoesNotContain("'OrderId'");
  }

  /// <summary>A projection of the model whose members include the stream id reads it from the row, beside the promoted column.</summary>
  [Test]
  public async Task ProjectedSelect_AnonymousTypeWithStreamId_ReadsTheRowIdAsync() {
    await using var db = _newContext();
    var sql = db.Set<PerspectiveRow<OrderModel>>().Select(r => r.Data)
      .Where(x => x.Title == "a")
      .Select(x => new { x.OrderId, x.Price, Urgent = x.Title.StartsWith("rush") })
      .ToQueryString();

    await Assert.That(sql).Contains("SELECT w.id AS \"OrderId\", w.price AS \"Price\", ");
    await Assert.That(sql).DoesNotContain("'OrderId'");
  }

  /// <summary>A sort on the stream id orders by the primary key.</summary>
  [Test]
  public async Task ProjectedOrderBy_StreamId_OrdersByTheRowIdAsync() {
    var sql = _projectedSql(models => models.OrderBy(x => x.Title).ThenByDescending(x => x.OrderId));

    await Assert.That(sql).Contains("w.id DESC");
    await Assert.That(sql).DoesNotContain("'OrderId'");
  }

  /// <summary>A grouping key on the stream id groups by the primary key, and the key read back is the row's id.</summary>
  [Test]
  public async Task ProjectedGroupBy_StreamId_GroupsByTheRowIdAsync() {
    await using var db = _newContext();
    var sql = db.Set<PerspectiveRow<OrderModel>>().Select(r => r.Data)
      .GroupBy(x => x.OrderId)
      .Select(g => new { g.Key, Count = g.Count() })
      .ToQueryString();

    await Assert.That(sql).Contains("GROUP BY w.id");
    await Assert.That(sql).DoesNotContain("'OrderId'");
  }

  /// <summary>An aggregate over the stream id reads the primary key.</summary>
  [Test]
  public async Task ProjectedMax_StreamId_ReadsTheRowIdAsync() {
    await using var db = _newContext();
    var query = db.Set<PerspectiveRow<OrderModel>>().Select(r => r.Data);
    var rewritten = new StreamIdPrimaryKeyRewriter().Visit(
      System.Linq.Expressions.Expression.Call(null,
        typeof(Queryable).GetMethods().Single(m => m.Name == nameof(Queryable.Max) && m.GetGenericArguments().Length == 2)
          .MakeGenericMethod(typeof(OrderModel), typeof(Guid)),
        query.Expression,
        System.Linq.Expressions.Expression.Quote((System.Linq.Expressions.Expression<Func<OrderModel, Guid>>)(x => x.OrderId))));

    await Assert.That(rewritten.ToString()).Contains(".Id)");
    await Assert.That(rewritten.ToString()).DoesNotContain("OrderId");
  }

  /// <summary>A nullable stream id read from a row reads its id, lifted to <c>Guid?</c>: a row always has one.</summary>
  [Test]
  public async Task NullableStreamId_Select_ReadsTheRowIdAsync() {
    await using var db = _newContext();
    var sql = db.Set<PerspectiveRow<DraftModel>>().Select(r => r.Data).Select(x => x.DraftId).ToQueryString();

    await Assert.That(sql).Contains("SELECT w.id");
    await Assert.That(sql).DoesNotContain("'DraftId'");
  }

  /// <summary>A filter on the stream id keeps reading the document beside the key, as #2303 settled: only reads are moved.</summary>
  [Test]
  public async Task FilterOnStreamId_StillKeepsTheDocumentTestAsync() {
    var id = _probeId;
    var sql = _projectedSql(models => models.Where(x => x.OrderId == id).OrderBy(x => x.OrderId));

    await Assert.That(sql).Contains("'OrderId'");
    await Assert.That(sql).Contains("ORDER BY w.id");
  }

  /// <summary>A read of the stream id over a group's members reads each member row's id.</summary>
  [Test]
  public async Task GroupedMembers_MaxOfStreamId_ReadsTheRowIdAsync() {
    await using var db = _newContext();
    var query = db.Set<PerspectiveRow<OrderModel>>().Select(r => r.Data)
      .GroupBy(x => x.Title)
      .Select(g => new { g.Key, Last = g.Max(x => x.OrderId) });

    var rewritten = new StreamIdPrimaryKeyRewriter().Visit(query.Expression).ToString();

    await Assert.That(rewritten).Contains("Max(x => x.Id)");
    await Assert.That(rewritten).DoesNotContain("OrderId");
  }

  /// <summary>
  /// A filter over a group's members tests the document, as a top-level filter does: a design choice (#2423) that only
  /// reads move to the row's id, so a filter never finds a row by its id that its document would not match.
  /// </summary>
  [Test]
  public async Task GroupedMembers_CountWhereStreamId_KeepsTheDocumentTestAsync() {
    var id = _probeId;
    await using var db = _newContext();
    var query = db.Set<PerspectiveRow<OrderModel>>().Select(r => r.Data)
      .GroupBy(x => x.Title)
      .Select(g => new { g.Key, Matching = g.Count(x => x.OrderId == id) });

    var rewritten = new StreamIdPrimaryKeyRewriter().Visit(query.Expression).ToString();

    await Assert.That(rewritten).Contains("x.Data.OrderId ==");
  }

  /// <summary>A subquery filter inside a projection tests the document too; the subquery's own reads still read the row's id.</summary>
  [Test]
  public async Task SubqueryInsideAProjection_KeepsItsFilterAndReadsItsIdAsync() {
    var id = _probeId;
    await using var db = _newContext();
    var others = db.Set<PerspectiveRow<OrderModel>>();
    var query = db.Set<PerspectiveRow<OrderModel>>()
      .Select(r => new {
        Same = others.Where(o => o.Data.OrderId == id).Select(o => o.Data.OrderId).FirstOrDefault(),
      });

    var rewritten = new StreamIdPrimaryKeyRewriter().Visit(query.Expression).ToString();

    await Assert.That(rewritten).Contains("o.Data.OrderId ==");
    await Assert.That(rewritten).Contains("Select(o => o.Id)");
  }

  /// <summary>A model whose stream id is not registered keeps reading it from the document.</summary>
  [Test]
  public async Task UnregisteredModel_Select_KeepsTheDocumentAsync() {
    await using var db = _newContext();
    var sql = db.Set<PerspectiveRow<UnregisteredModel>>().Select(r => r.Data).Select(x => x.UnregisteredId).ToQueryString();

    await Assert.That(sql).Contains("'UnregisteredId'");
  }

  /// <summary>A projection of another Guid member of the model still reads the document.</summary>
  [Test]
  public async Task ProjectedSelect_AnotherGuidMember_KeepsTheDocumentAsync() {
    await using var db = _newContext();
    var sql = db.Set<PerspectiveRow<OrderModel>>().Select(r => r.Data).Select(x => x.CustomerId).ToQueryString();

    await Assert.That(sql).Contains("'CustomerId'");
  }

  // ========================================
  // Left alone
  // ========================================

  /// <summary>An equality under OR cannot be narrowed to one row, so nothing is added.</summary>
  [Test]
  public async Task StreamIdUnderOr_IsLeftAloneAsync() {
    var id = _probeId;
    var sql = _projectedSql(models => models.Where(x => x.OrderId == id || x.Title == "a"));

    await Assert.That(sql).DoesNotContain("w.id = @");
  }

  /// <summary>An inequality says nothing the primary key can serve.</summary>
  [Test]
  public async Task StreamIdInequality_IsLeftAloneAsync() {
    var id = _probeId;
    var sql = _rowSql(rows => rows.Where(r => r.Data.OrderId != id));

    await Assert.That(sql).DoesNotContain("w.id = @");
    await Assert.That(sql).DoesNotContain("w.id <> @");
  }

  /// <summary>The stream id compared with another field of the same row names no single row, so nothing is added.</summary>
  [Test]
  public async Task StreamIdComparedWithAnotherField_IsLeftAloneAsync() {
    var sql = _projectedSql(models => models.Where(x => x.OrderId == x.CustomerId));

    await Assert.That(sql).DoesNotContain("w.id =");
  }

  /// <summary>An operator that does not commute with the projection is left where it is.</summary>
  [Test]
  public async Task ProjectedTakeWhile_IsLeftWhereItIsAsync() {
    var id = _probeId;
    await using var db = _newContext();
    var query = db.Set<PerspectiveRow<OrderModel>>().Select(r => r.Data).TakeWhile(x => x.OrderId == id);

    var rewritten = new StreamIdPrimaryKeyRewriter().Visit(query.Expression);

    await Assert.That(rewritten).IsSameReferenceAs(query.Expression);
  }

  /// <summary>
  /// A secondary sort that takes a comparer cannot move beneath the projection, so the primary sort beneath it
  /// stays where it was too, and the query is the one that was written.
  /// </summary>
  [Test]
  public async Task ProjectedThenByWithAComparer_KeepsTheQueryAsWrittenAsync() {
    await using var db = _newContext();
    var query = db.Set<PerspectiveRow<OrderModel>>().Select(r => r.Data)
      .OrderBy(x => x.Title).ThenBy(x => x.Title, StringComparer.Ordinal);

    var rewritten = new StreamIdPrimaryKeyRewriter().Visit(query.Expression);

    await Assert.That(rewritten).IsSameReferenceAs(query.Expression);
  }

  /// <summary>A filter after paging does not commute with it, so it stays above the projection.</summary>
  [Test]
  public async Task ProjectedFilterAfterSkip_IsNotHoistedAsync() {
    var id = _probeId;
    var sql = _projectedSql(models => models.OrderBy(x => x.Title).Skip(5).Where(x => x.OrderId == id));

    await Assert.That(sql).DoesNotContain("w.id = @");
  }

  // ========================================
  // A null value never reaches the primary key
  // ========================================

  /// <summary>A nullable stream id compared with a value that is not null is answered by the primary key.</summary>
  [Test]
  public async Task NullableStreamId_EqualsAValue_AddsPrimaryKeyAsync() {
    Guid? id = _probeId;
    var sql = _draftSql(models => models.Where(x => x.DraftId == id));

    await Assert.That(sql).Contains("w.id = @");
  }

  /// <summary>
  /// The primary key is never null, so a comparison with null keeps finding the documents with no stream id.
  /// This is the shape a GraphQL <c>eq: null</c> filter produces.
  /// </summary>
  [Test]
  public async Task NullableStreamId_EqualsNull_IsLeftAloneAsync() {
    var sql = _draftSql(models => models.Where(x => x.DraftId == null));

    await Assert.That(sql).DoesNotContain("w.id");
  }

  /// <summary>A captured value that happens to be null is settled from the parameter, and the key is dropped.</summary>
  [Test]
  public async Task NullableStreamId_EqualsANullVariable_IsLeftAloneAsync() {
    Guid? id = null;
    var sql = _draftSql(models => models.Where(x => x.DraftId == id));

    await Assert.That(sql).DoesNotContain("w.id");
  }

  /// <summary>A set of nullable ids can hold null, so it stays a document search.</summary>
  [Test]
  public async Task NullableStreamId_InASetThatCanHoldNull_IsLeftAloneAsync() {
    var ids = new List<Guid?> { _probeId, null };
    var sql = _draftSql(models => models.Where(x => ids.Contains(x.DraftId)));

    await Assert.That(sql).DoesNotContain("w.id");
  }

  /// <summary>A model whose stream id is not registered compiles exactly as before.</summary>
  [Test]
  public async Task UnregisteredModel_IsLeftAloneAsync() {
    var id = _probeId;
    await using var db = _newContext();
    var sql = db.Set<PerspectiveRow<UnregisteredModel>>().Where(r => r.Data.UnregisteredId == id).ToQueryString();

    await Assert.That(sql).DoesNotContain("w.id = @");
  }
}
