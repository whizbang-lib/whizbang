#pragma warning disable CA1707

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Coverage for <see cref="EFCoreCollectiveQuery.Of{TOther}"/> — the sibling-perspective accessor a
/// handler's <c>Where</c> calls to reference another perspective's rows
/// (<c>q.Of&lt;TOther&gt;().Any(...)</c>). <see cref="EFCoreCollectiveQuery.TableFor"/> and the
/// correlated-EXISTS translation are covered end-to-end against a real Postgres container by
/// <c>CollectiveDispatcherEFCoreIntegrationTests</c>; <c>Of</c> itself only resolves EF Core's
/// <see cref="DbContext.Set{TEntity}"/>, which never opens a connection — an EF Core InMemory
/// context (owned-type mapping, matching the <c>TestDbContext</c> pattern in
/// <c>EFCorePostgresLensQueryTests.cs</c>) is enough. No database is used in this file.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Collective/EFCoreCollectiveQuery.cs</code-under-test>
[Category("Shard1")]
public class EFCoreCollectiveQueryCoverageTests {

  // If Of<TOther>() resolved the wrong DbSet (or a detached/new context instead of the live one
  // passed to the query), a handler's cross-perspective cohort check would silently compare
  // against empty or stale data — a collective apply could then mutate rows a correlated EXISTS
  // was supposed to exclude, or skip rows it should have included.
  [Test]
  public async Task Of_ReturnsTheLiveDbContextsDbSetForThatModelAsync() {
    await using var ctx = _newCtx();
    var now = DateTime.UtcNow;
    var seeded = new PerspectiveRow<_siblingModel> {
      Id = Guid.NewGuid(),
      Data = new _siblingModel { Name = "sibling" },
      Metadata = new PerspectiveMetadata(),
      Scope = new PerspectiveScope(),
      CreatedAt = now,
      UpdatedAt = now,
      Version = 1,
    };
    ctx.Add(seeded);
    await ctx.SaveChangesAsync();
    var query = new EFCoreCollectiveQuery(ctx);

    var result = query.Of<_siblingModel>();

    await Assert.That(result.Count()).IsEqualTo(1)
      .Because("Of<TOther>() must return the SAME DbContext's live DbSet, not a disconnected or "
             + "empty one, or a handler's sibling-perspective cohort check would see no rows");
  }

  private sealed class _siblingModel {
    public string Name { get; set; } = string.Empty;
  }

  private static _ctx _newCtx() {
    var options = new DbContextOptionsBuilder<_ctx>()
      .UseInMemoryDatabase($"collective-query-coverage-{Guid.NewGuid():N}")
      .Options;
    return new _ctx(options);
  }

  private sealed class _ctx(DbContextOptions<_ctx> opts) : DbContext(opts) {
    public DbSet<PerspectiveRow<_siblingModel>> Siblings => Set<PerspectiveRow<_siblingModel>>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<_siblingModel>>(entity => {
        entity.HasKey(e => e.Id);
        // Owned types for the InMemory provider — matches the TestDbContext pattern in
        // EFCorePostgresLensQueryTests.cs. Production maps these as JSONB via .ToJson();
        // that translation is a real-Postgres concern, not what Of<TOther>() itself does.
        entity.OwnsOne(e => e.Data, data => data.WithOwner());
        entity.OwnsOne(e => e.Metadata, metadata => metadata.WithOwner());
        entity.Property(e => e.Scope)
          .HasConversion(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<PerspectiveScope>(v, JsonSerializerOptions.Default)!);
      });
    }
  }
}
