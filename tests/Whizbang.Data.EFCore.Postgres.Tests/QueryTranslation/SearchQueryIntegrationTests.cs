using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Data.Postgres;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// A search written as a plain <c>Contains</c> on a field declared <c>[Indexed(IndexKinds.Search)]</c> finds
/// what a person typing a search box expects, and is answered by the trigram index over the folded value.
/// </summary>
/// <remarks>
/// Real database, real migrations (for <c>wh_fold</c>), the index built by the generator's own script, and
/// the query built by Entity Framework through the framework's rewrite. The plan is taken from the exact SQL
/// Entity Framework produced, so a query whose expression drifted from the index's would show a scan here.
/// </remarks>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class SearchQueryIntegrationTests : EFCoreTestBase {
  private const string TABLE = "wh_per_search_it";

  [SuppressIndexAdvisory("integration fixture; the index under test is created by the test itself")]
  public class SearchItModel {
    [StreamId]
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
  }

  private sealed class SearchDbContext(DbContextOptions<SearchDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<PerspectiveRow<SearchItModel>>(entity => {
        entity.ToTable(TABLE);
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
      });
  }

  private SearchDbContext _context() => new(new DbContextOptionsBuilder<SearchDbContext>()
    .UseNpgsql(ConnectionString, o => o.UseWhizbangFunctions())
    .UseWhizbangPhysicalFields()
    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
    .Options);

  private async Task _executeAsync(string sql) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
  }

  [Before(Test)]
  public async Task SeedAsync() {
    JsonIndexRegistry.Register<SearchItModel>(nameof(SearchItModel.Title), IndexKinds.Search);
    await _executeAsync($"""
      DROP TABLE IF EXISTS {TABLE};
      CREATE TABLE {TABLE} (
        id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb NOT NULL, scope jsonb NOT NULL,
        created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, version integer NOT NULL,
        expires_at timestamptz);
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version)
      SELECT gen_random_uuid(), jsonb_build_object('Id', gen_random_uuid(), 'Title', t), jsonb_build_object(), jsonb_build_object(), now(), now(), 1
      FROM unnest(ARRAY[
        'Chief O' || chr(8217) || 'Brien ' || chr(8211) || ' Operations',
        'Registered Nurse',
        'NURSE Practitioner',
        'Senior Engineer',
        '100% Remote Analyst'
      ]) AS t;
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version)
      SELECT gen_random_uuid(), jsonb_build_object('Id', gen_random_uuid(), 'Title', 'Filler role ' || g), jsonb_build_object(), jsonb_build_object(), now(), now(), 1
      FROM generate_series(1, 3000) AS g;
      ANALYZE {TABLE};
      """);
    var script = JsonIndexSql.Script(
      [new JsonIndexInfo("Title", "Title", JsonIndexCast.None, Ordered: false, Substring: false, CaseInsensitive: false, Search: true)],
      $"public.{TABLE}", "search_it");
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await OptionalExtensionBlocks.ApplyAsync(conn, null, script, 30);
  }

  private async Task<List<string>> _titlesAsync(string term) {
    await using var db = _context();
    return await db.Set<PerspectiveRow<SearchItModel>>()
      .Where(r => r.Data.Title.Contains(term))
      .Select(r => r.Data.Title)
      .OrderBy(t => t)
      .ToListAsync();
  }

  [Test]
  public async Task Search_IgnoresCaseAsync() {
    await Assert.That(await _titlesAsync("nurse")).IsEquivalentTo(["NURSE Practitioner", "Registered Nurse"]);
  }

  [Test]
  public async Task Search_AStraightQuoteTerm_FindsACurlyQuoteTitle_AndDashesAlikeAsync() {
    var found = await _titlesAsync("o'brien - oper");

    await Assert.That(found.Count).IsEqualTo(1)
      .Because("the person typed a straight quote and a hyphen; the title has a curly quote and an en dash");
  }

  [Test]
  public async Task Search_AWildcardInTheTerm_MatchesLiterallyAsync() {
    await Assert.That(await _titlesAsync("100% rem")).IsEquivalentTo(["100% Remote Analyst"]);
    await Assert.That(await _titlesAsync("0_ r")).IsEmpty()
      .Because("an underscore typed into a search box is an underscore, not 'any character'");
  }

  [Test]
  public async Task Search_IsAnsweredByTheFoldIndexAsync() {
    await using var db = _context();
    var sql = db.Set<PerspectiveRow<SearchItModel>>()
      .Where(r => r.Data.Title.Contains("nurse"))
      .Select(r => r.Id)
      .ToQueryString();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("EXPLAIN " + sql, conn);
    var plan = new List<string>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        plan.Add(reader.GetString(0));
      }
    }

    await Assert.That(string.Join("\n", plan)).Contains("idx_search_it_title_fold_trgm")
      .Because("the query's value side must be the index's expression, or the index is never used");
  }
}
