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
  }

  /// <summary>A model whose stream id is never registered.</summary>
  [SuppressIndexAdvisory("this model exists to compile queries, never to run them")]
  public class UnregisteredModel {
    [StreamId]
    public Guid UnregisteredId { get; init; }

    public string Title { get; init; } = string.Empty;
  }

  private sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<OrderModel>>(entity => {
        _mapRow(entity, "wh_per_order");
        entity.Property<decimal>("price").HasColumnName("price");
      });
      modelBuilder.Entity<PerspectiveRow<UnregisteredModel>>(entity => _mapRow(entity, "wh_per_unregistered"));
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
    return new OrderDbContext(_options);
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

  /// <summary>A filter after paging does not commute with it, so it stays above the projection.</summary>
  [Test]
  public async Task ProjectedFilterAfterSkip_IsNotHoistedAsync() {
    var id = _probeId;
    var sql = _projectedSql(models => models.OrderBy(x => x.Title).Skip(5).Where(x => x.OrderId == id));

    await Assert.That(sql).DoesNotContain("w.id = @");
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
