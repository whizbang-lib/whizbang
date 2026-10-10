// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// How the collective paths find a perspective's table before writing raw SQL against it. A mapped row
/// gives its table and schema; a row the model does not map, or maps to a view, is refused with the
/// reason, because raw SQL against a guessed table would update nothing or the wrong rows.
/// </summary>
/// <remarks>
/// Building an EF Core model needs a provider, not a server: these models are built with Npgsql and
/// never open a connection.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Collective/PerspectiveRowTable.cs</code-under-test>
[Category("Shard1")]
public class PerspectiveRowTableTests {
  [Test]
  public async Task Resolve_WhenTheRowIsMappedToATable_ReturnsItsTableAndSchemaAsync() {
    await using var ctx = Ctx.Create(asView: false);

    var (table, schema) = PerspectiveRowTable.Resolve(
      ctx.Model, typeof(PerspectiveRow<MappedModel>), nameof(MappedModel), "unused");

    await Assert.That(table).IsEqualTo("mapped_rows");
    await Assert.That(schema).IsEqualTo("projections");
  }

  [Test]
  public async Task Resolve_WhenTheModelDoesNotMapTheRow_ThrowsWithTheCallersHintAsync() {
    await using var ctx = Ctx.Create(asView: false);

    var thrown = Assert.Throws<InvalidOperationException>(() => PerspectiveRowTable.Resolve(
      ctx.Model, typeof(PerspectiveRow<UnmappedModel>), nameof(UnmappedModel), "the sibling DbSet is not registered."));

    await Assert.That(thrown.Message).Contains("PerspectiveRow<UnmappedModel>");
    await Assert.That(thrown.Message).Contains("the sibling DbSet is not registered.");
  }

  [Test]
  public async Task Resolve_WhenTheRowIsMappedToAView_ThrowsThatItHasNoTableAsync() {
    await using var ctx = Ctx.Create(asView: true);

    var thrown = Assert.Throws<InvalidOperationException>(() => PerspectiveRowTable.Resolve(
      ctx.Model, typeof(PerspectiveRow<MappedModel>), nameof(MappedModel), "unused"));

    await Assert.That(thrown.Message).IsEqualTo("PerspectiveRow<MappedModel> has no table name.");
  }

  [Test]
  public async Task TableFor_ResolvesTheSiblingTableThroughTheModelAsync() {
    await using var ctx = Ctx.Create(asView: false);

    var table = new EFCoreCollectiveQuery(ctx).TableFor(typeof(MappedModel));

    await Assert.That(table).IsEqualTo("mapped_rows");
  }

  public sealed class MappedModel {
    public string Name { get; set; } = string.Empty;
  }

  public sealed class UnmappedModel {
    public string Name { get; set; } = string.Empty;
  }

  private sealed class Ctx(DbContextOptions<Ctx> options, bool asView) : DbContext(options) {
    public static Ctx Create(bool asView) => new(
      new DbContextOptionsBuilder<Ctx>()
        .UseNpgsql("Host=localhost;Database=probe;Username=u;Password=p")
        // The two mappings differ, so each needs its own cached model.
        .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, ViewKeyFactory>()
        .Options,
      asView);

    public bool AsView { get; } = asView;

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<PerspectiveRow<MappedModel>>(entity => {
        entity.HasKey(e => e.Id);
        if (AsView) {
          entity.ToView("mapped_rows_view", "projections");
        } else {
          entity.ToTable("mapped_rows", "projections");
        }
        entity.OwnsOne(e => e.Data);
        entity.OwnsOne(e => e.Metadata);
        entity.Property(e => e.Scope).HasConversion(
          v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
          v => JsonSerializer.Deserialize<PerspectiveScope>(v, JsonSerializerOptions.Default)!);
      });
  }

  private sealed class ViewKeyFactory : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory {
    public object Create(DbContext context, bool designTime) =>
      (context.GetType(), ((Ctx)context).AsView, designTime);
  }
}
