using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// A <c>Contains</c> on a field declared <c>[Indexed(IndexKinds.Search)]</c> becomes the folded search its
/// trigram index is built over, with no change to how the query is written.
/// </summary>
/// <remarks>
/// The index is over <c>wh_fold(data -&gt;&gt; 'Field')</c>, so the query must produce exactly that on the
/// value side or the planner will not use it, and must fold the term the same way or a curly-quote value
/// would not match a straight-quote term. The SQL is asserted here; that it returns the right rows and uses
/// the index against a real database is proven in SearchQueryIntegrationTests.
/// </remarks>
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class SearchQueryShapeTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=shape;Username=u;Password=p";

  [SuppressIndexAdvisory("this model exists to compile queries, never to run them")]
  public class SearchModel {
    [StreamId]
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Code { get; init; }
    public string? Notes { get; init; }

    [PhysicalField]
    public string? Sku { get; init; }
  }

  private sealed class ShapeDbContext(DbContextOptions<ShapeDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<PerspectiveRow<SearchModel>>(entity => {
        entity.ToTable("wh_per_search");
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
        entity.Property<DateTime?>("expires_at").HasColumnName("expires_at");
        entity.Property<string?>("sku").HasColumnName("sku");
      });
  }

  private static readonly DbContextOptions<ShapeDbContext> _options =
    new DbContextOptionsBuilder<ShapeDbContext>()
      .UseNpgsql(UNUSED_CONNECTION, o => o.UseWhizbangFunctions())
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private static string _sqlFor(Func<IQueryable<PerspectiveRow<SearchModel>>, IQueryable<object>> shape) {
    JsonIndexRegistry.Register<SearchModel>(nameof(SearchModel.Title), IndexKinds.Search);
    JsonIndexRegistry.Register<SearchModel>(nameof(SearchModel.Code), IndexKinds.Search);
    PhysicalFieldRegistry.Register<SearchModel>(nameof(SearchModel.Sku), "sku");
    JsonIndexRegistry.Register<SearchModel>(nameof(SearchModel.Sku), IndexKinds.Search);
    using var db = new ShapeDbContext(_options);
    return shape(db.Set<PerspectiveRow<SearchModel>>()).ToQueryString();
  }

  [Test]
  public async Task Contains_OnASearchField_FoldsTheValueAndTheTermAsync() {
    var sql = _sqlFor(rows => rows.Where(r => r.Data.Title.Contains("O'Brien")));

    await Assert.That(sql).Contains("wh_fold(w.data ->> 'Title') LIKE wh_fold_pattern(")
      .Because("the value side must be the very expression the index is built over");
    await Assert.That(sql).DoesNotContain("strpos");
  }

  [Test]
  public async Task Contains_WithACapturedTerm_FoldsTheParameterAsync() {
    var term = "nurse";
    var sql = _sqlFor(rows => rows.Where(r => r.Data.Title.Contains(term)));

    await Assert.That(sql).Contains("LIKE wh_fold_pattern(@");
  }

  [Test]
  public async Task Contains_OnAFieldNotDeclaredForSearch_IsLeftAloneAsync() {
    var sql = _sqlFor(rows => rows.Where(r => r.Data.Notes!.Contains("rn")));

    await Assert.That(sql).DoesNotContain("wh_fold")
      .Because("folding changes what Contains means, so it applies only where the field asked for it");
  }

  [Test]
  public async Task Contains_InALensShapedQuery_OverTheSelectedModel_IsFoldedAsync() {
    var sql = _sqlFor(rows => rows.Select(r => r.Data).Where(m => m.Title.Contains("rn")));

    await Assert.That(sql).Contains("wh_fold(w.data ->> 'Title') LIKE wh_fold_pattern(");
  }

  [Test]
  public async Task SeveralSearchFields_OrTogether_EachFoldedAsync() {
    var sql = _sqlFor(rows => rows.Where(r => r.Data.Title.Contains("rn") || r.Data.Code!.Contains("rn")));

    await Assert.That(sql).Contains("wh_fold(w.data ->> 'Title') LIKE wh_fold_pattern(");
    await Assert.That(sql).Contains("wh_fold(w.data ->> 'Code') LIKE wh_fold_pattern(");
  }

  [Test]
  public async Task TheExplicitFunction_TranslatesOnAnyFieldAsync() {
    var sql = _sqlFor(rows => rows.Where(r => EF.Functions.FoldedContains(r.Data.Title, "rn")));

    await Assert.That(sql).Contains("wh_fold(w.data ->> 'Title') LIKE wh_fold_pattern(");
  }

  [Test]
  public async Task TheFunction_CalledInMemory_ThrowsAsync() {
    await Assert.That(() => EF.Functions.FoldedContains("a", "a")).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Contains_OnAPromotedSearchField_FoldsTheColumnAsync() {
    var sql = _sqlFor(rows => rows.Where(r => r.Data.Sku!.Contains("ab")));

    await Assert.That(sql).Contains("wh_fold(w.sku) LIKE wh_fold_pattern(")
      .Because("the rewrite folds the member, and the promoted-field redirect then points it at the column the index is on");
  }
}
