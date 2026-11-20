// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Pins that a grouping or an aggregate written over the projected model (after <c>Select(r =&gt; r.Data)</c>) reads a
/// promoted field from its column, as a filter or a sort there does (#2424). Under Split storage the document holds the
/// field's default, so reading the document groups every row together and sums defaults, and the query still succeeds.
/// </summary>
/// <remarks>
/// The document reads a property by its JSON key, written in the model's casing (<c>'Status'</c>); the promoted
/// column is lowercase (<c>status</c>). So a JSON key in the SQL is the document read these tests rule out.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
[Category("Shard1")]
public class PhysicalFieldGroupingSqlTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=shape;Username=u;Password=p";

  /// <summary>A model with two promoted fields and a document field.</summary>
  [SuppressIndexAdvisory("this model exists to compile queries, never to run them")]
  public class TicketModel {
    [PhysicalField]
    public string Status { get; init; } = string.Empty;

    [PhysicalField]
    public string Region { get; init; } = string.Empty;

    [PhysicalField]
    public decimal Price { get; init; }

    public string Title { get; init; } = string.Empty;
  }

  private sealed class TicketDbContext(DbContextOptions<TicketDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<TicketModel>>(entity => {
        entity.ToTable("wh_per_ticket");
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
        entity.Property<string>("status").HasColumnName("status");
        entity.Property<string>("region").HasColumnName("region");
        entity.Property<decimal>("price").HasColumnName("price");
      });
    }
  }

  private static readonly DbContextOptions<TicketDbContext> _options =
    new DbContextOptionsBuilder<TicketDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private static string _sql<TResult>(Func<IQueryable<TicketModel>, IQueryable<TResult>> shape) {
    PhysicalFieldRegistry.Register<TicketModel>("Status", "status");
    PhysicalFieldRegistry.Register<TicketModel>("Region", "region");
    PhysicalFieldRegistry.Register<TicketModel>("Price", "price");
    using var db = new TicketDbContext(_options);
    return shape(db.Set<PerspectiveRow<TicketModel>>().Select(r => r.Data)).ToQueryString();
  }

  /// <summary>The issue's shape: a count per value of a promoted field.</summary>
  [Test]
  public async Task GroupBy_APromotedField_GroupsByItsColumnAsync() {
    var sql = _sql(models => models.GroupBy(x => x.Status).Select(g => new { g.Key, Count = g.Count() }));

    await Assert.That(sql).Contains("w.status");
    await Assert.That(sql).DoesNotContain("'Status'");
  }

  /// <summary>A key built from several promoted fields groups by each one's column.</summary>
  [Test]
  public async Task GroupBy_ACompositeKey_GroupsByEachColumnAsync() {
    var sql = _sql(models => models.GroupBy(x => new { x.Status, x.Region }).Select(g => new { g.Key.Status, g.Key.Region, Count = g.Count() }));

    await Assert.That(sql).Contains("w.status").And.Contains("w.region");
    await Assert.That(sql).DoesNotContain("'Status'");
    await Assert.That(sql).DoesNotContain("'Region'");
  }

  /// <summary>An aggregate over the group's members reads the promoted field from its column.</summary>
  [Test]
  public async Task GroupBy_AnAggregateOverTheMembers_ReadsTheColumnAsync() {
    var sql = _sql(models => models.GroupBy(x => x.Status).Select(g => new {
      g.Key,
      Total = g.Sum(x => x.Price),
      Highest = g.Max(x => x.Price),
      Expensive = g.Count(x => x.Price > 10m),
    }));

    await Assert.That(sql).Contains("sum(w");
    await Assert.That(sql).Contains("max(w");
    await Assert.That(sql).DoesNotContain("'Price'");
  }

  /// <summary>A filter over the group's members before an aggregate reads the column too.</summary>
  [Test]
  public async Task GroupBy_AFilterOverTheMembersBeforeAnAggregate_ReadsTheColumnAsync() {
    var sql = _sql(models => models.GroupBy(x => x.Status).Select(g => new {
      g.Key,
      Total = g.Where(x => x.Price > 10m).Take(5).Sum(x => x.Price),
    }));

    await Assert.That(sql).DoesNotContain("'Price'");
    await Assert.That(sql).DoesNotContain("'Status'");
  }

  /// <summary>An element selector reads the promoted field from its column.</summary>
  [Test]
  public async Task GroupBy_WithAnElementSelector_ReadsTheColumnAsync() {
    var sql = _sql(models => models.GroupBy(x => x.Status, x => x.Price).Select(g => new { g.Key, Total = g.Sum() }));

    await Assert.That(sql).Contains("sum(w");
    await Assert.That(sql).DoesNotContain("'Price'");
    await Assert.That(sql).DoesNotContain("'Status'");
  }

  /// <summary>A result selector's aggregate over the members reads the promoted field from its column.</summary>
  [Test]
  public async Task GroupBy_WithAResultSelector_ReadsTheColumnAsync() {
    var sql = _sql(models => models.GroupBy(x => x.Status, (key, members) => new { Key = key, Total = members.Sum(x => x.Price) }));

    await Assert.That(sql).Contains("sum(w");
    await Assert.That(sql).DoesNotContain("'Price'");
    await Assert.That(sql).DoesNotContain("'Status'");
  }

  /// <summary>An element and a result selector together read the promoted field from its column.</summary>
  [Test]
  public async Task GroupBy_WithAnElementAndAResultSelector_ReadsTheColumnAsync() {
    var sql = _sql(models => models.GroupBy(x => x.Status, x => x.Price, (key, prices) => new { Key = key, Total = prices.Sum() }));

    await Assert.That(sql).Contains("sum(w");
    await Assert.That(sql).DoesNotContain("'Price'");
    await Assert.That(sql).DoesNotContain("'Status'");
  }

  /// <summary>
  /// A member read the column form cannot express (the group itself, or an operator whose result is the model) keeps the
  /// document read for that member, and the key still groups by the column.
  /// </summary>
  [Test]
  public async Task GroupBy_AMemberUseTheColumnFormCannotExpress_StillGroupsByTheColumnAsync() {
    var sql = _sql(models => models.GroupBy(x => x.Status).Select(g => new { g.Key, Cheapest = g.OrderBy(x => x.Price).Select(x => x.Title).FirstOrDefault() }));

    await Assert.That(sql).Contains("w.status");
    await Assert.That(sql).DoesNotContain("'Status'");
  }
}
