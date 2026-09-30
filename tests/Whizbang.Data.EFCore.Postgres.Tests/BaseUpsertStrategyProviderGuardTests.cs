using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A provider other than Npgsql is a case that still legitimately falls back: the atomic statement is
/// raw PostgreSQL, so the PostgreSQL strategy on any other provider must write through Entity Framework.
/// </summary>
/// <remarks>
/// The atomic path is available with no startup hook (#967), and the model here is one the persistence union
/// resolves, so nothing but this guard keeps the atomic path away from an in-memory fixture. Had the guard gone, the atomic path would ask the in-memory context for its
/// database connection, which a non-relational provider refuses, and the write would throw instead of landing.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs</code-under-test>
[Category("Shard1")]
public class BaseUpsertStrategyProviderGuardTests {

  private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<UpsertCoverageWidgetModel>>(entity => {
        entity.ToTable("wh_per_guard_model");
        entity.HasKey(e => e.Id);
        entity.Property<DateTime?>("sys_created_at");
        entity.Property<DateTime?>("sys_updated_at");
        entity.OwnsOne(e => e.Data, d => d.WithOwner());
        entity.OwnsOne(e => e.Metadata, m => m.WithOwner());
        entity.Property(e => e.Scope)
          .HasConversion(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<PerspectiveScope>(v, JsonSerializerOptions.Default)!);
      });
    }
  }

  [Test]
  public async Task Upsert_OnANonNpgsqlProvider_FallsBackToTheEntityFrameworkPathAndPersistsAsync() {
    var options = new DbContextOptionsBuilder<ProbeDbContext>()
      .UseInMemoryDatabase($"probe-{Guid.CreateVersion7()}")
      .Options;
    await using var context = new ProbeDbContext(options);
    var id = Guid.CreateVersion7();

    await new PostgresUpsertStrategy().UpsertPerspectiveRowAsync(
      context,
      "wh_per_guard_model",
      id,
      new UpsertCoverageWidgetModel { Id = id, Name = "InMemory" },
      new PerspectiveMetadata { EventType = "Guarded", EventId = Guid.CreateVersion7().ToString(), Timestamp = DateTime.UtcNow },
      new PerspectiveScope());

    var row = await context.Set<PerspectiveRow<UpsertCoverageWidgetModel>>().AsNoTracking().SingleAsync(r => r.Id == id);
    await Assert.That(row.Data.Name).IsEqualTo("InMemory")
      .Because("on a provider other than Npgsql the atomic statement declines and Entity Framework writes the row");
  }
}
