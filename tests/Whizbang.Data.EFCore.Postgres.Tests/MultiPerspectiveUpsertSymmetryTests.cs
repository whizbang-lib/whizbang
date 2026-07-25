using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Targets a production symptom observed on a consumer at the EFCore layer: when two
/// perspectives both upsert to the same DbContext for the same stream, BOTH rows must
/// persist. The production observation was Order's row appeared but OrderLines'
/// row did not, despite both perspectives' Apply running.
///
/// <para>
/// These tests use InMemoryDatabase. They cover the multi-perspective DbContext-sharing
/// invariant for scalar models. Real-Postgres tests with list-property models live in
/// <see cref="MultiPerspectivePostgresUpsertSymmetryTests"/>.
/// </para>
/// </summary>
public class MultiPerspectiveUpsertSymmetryTests {

  /// <summary>Mirrors OrderModel shape: scalar properties only — the working case.</summary>
  public class ModelOne {
    [StreamId]
    public Guid Id { get; set; }
    public string? FieldA { get; set; }
    public string? FieldB { get; set; }
    public int Version { get; set; }
  }

  /// <summary>Different model with different shape — represents a second perspective on the
  /// same stream. Both should upsert independently into their own perspective table.</summary>
  public class ModelTwo {
    [StreamId]
    public Guid Id { get; set; }
    public string? FieldX { get; set; }
    public int Counter { get; set; }
    public int Version { get; set; }
  }

  private sealed class TwoModelsDbContext(DbContextOptions<TwoModelsDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      _configurePerspectiveRow<ModelOne>(modelBuilder);
      _configurePerspectiveRow<ModelTwo>(modelBuilder);
    }

    private static void _configurePerspectiveRow<TModel>(ModelBuilder modelBuilder) where TModel : class {
      modelBuilder.Entity<PerspectiveRow<TModel>>(entity => {
        entity.HasKey(e => e.Id);
        entity.OwnsOne(e => e.Data, data => data.WithOwner());
        entity.OwnsOne(e => e.Metadata, m => {
          m.WithOwner();
          m.Property(p => p.EventType).IsRequired();
          m.Property(p => p.EventId).IsRequired();
          m.Property(p => p.Timestamp).IsRequired();
        });
        entity.Property(e => e.Scope).HasConversion(
          v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
          v => System.Text.Json.JsonSerializer.Deserialize<PerspectiveScope>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new PerspectiveScope());
      });
    }
  }

  private static DbContextOptions<TwoModelsDbContext> _createInMemoryOptions() =>
    new DbContextOptionsBuilder<TwoModelsDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;

  // ---------- TESTS ----------

  [Test]
  public async Task TwoPerspectives_SharedDbContext_SameStreamId_BothRowsPersistAsync() {
    // Direct consumer scenario: two perspectives on the SAME stream sharing a DbContext.
    // Both upsert into their own perspective table. Both rows must exist after.
    await using var ctx = new TwoModelsDbContext(_createInMemoryOptions());
    var storeOne = new EFCorePostgresPerspectiveStore<ModelOne>(ctx, "perspective_one");
    var storeTwo = new EFCorePostgresPerspectiveStore<ModelTwo>(ctx, "perspective_two");
    var streamId = Guid.NewGuid();

    await storeOne.UpsertAsync(streamId, new ModelOne { Id = streamId, FieldA = "a", FieldB = "b", Version = 1 });
    await storeTwo.UpsertAsync(streamId, new ModelTwo { Id = streamId, FieldX = "x", Counter = 5, Version = 1 });

    var savedOne = await ctx.Set<PerspectiveRow<ModelOne>>().FirstOrDefaultAsync(r => r.Id == streamId);
    var savedTwo = await ctx.Set<PerspectiveRow<ModelTwo>>().FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(savedOne).IsNotNull()
      .Because("ModelOne perspective row must persist on shared DbContext.");
    await Assert.That(savedTwo).IsNotNull()
      .Because("ModelTwo perspective row must ALSO persist — symmetric with ModelOne. Asymmetry here is the consumer's production bug at the EFCore layer.");
    await Assert.That(savedOne!.Data.FieldA).IsEqualTo("a");
    await Assert.That(savedTwo!.Data.FieldX).IsEqualTo("x");
  }

  [Test]
  public async Task TwoPerspectives_OneAfterAnother_TrackerClearedBetween_BothPersistAsync() {
    // After the first upsert, PostgresUpsertStrategy clears the change tracker. The second
    // upsert on the same DbContext for a different model must not be affected by lingering
    // tracking state from the first upsert.
    await using var ctx = new TwoModelsDbContext(_createInMemoryOptions());
    var storeOne = new EFCorePostgresPerspectiveStore<ModelOne>(ctx, "perspective_one");
    var storeTwo = new EFCorePostgresPerspectiveStore<ModelTwo>(ctx, "perspective_two");
    var streamId = Guid.NewGuid();

    await storeOne.UpsertAsync(streamId, new ModelOne { Id = streamId, FieldA = "first", FieldB = "second", Version = 1 });

    // After first upsert: tracker should be in a clean state, second upsert should not interfere
    await storeTwo.UpsertAsync(streamId, new ModelTwo { Id = streamId, FieldX = "third", Counter = 99, Version = 1 });

    var savedOne = await ctx.Set<PerspectiveRow<ModelOne>>().FirstOrDefaultAsync(r => r.Id == streamId);
    var savedTwo = await ctx.Set<PerspectiveRow<ModelTwo>>().FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(savedOne).IsNotNull()
      .Because("ModelOne row must still exist after ModelTwo upsert (no cross-perspective interference).");
    await Assert.That(savedTwo).IsNotNull();
  }

  [Test]
  public async Task TwoPerspectives_ReverseOrder_StreamFirstUsedByModelTwo_BothPersistAsync() {
    // Order-independence: insert into ModelTwo first, then ModelOne. Both should persist.
    // Catches a regression where the FIRST upsert for a stream sets up state that breaks
    // a different-model upsert later for the same stream.
    await using var ctx = new TwoModelsDbContext(_createInMemoryOptions());
    var storeOne = new EFCorePostgresPerspectiveStore<ModelOne>(ctx, "perspective_one");
    var storeTwo = new EFCorePostgresPerspectiveStore<ModelTwo>(ctx, "perspective_two");
    var streamId = Guid.NewGuid();

    // ModelTwo first
    await storeTwo.UpsertAsync(streamId, new ModelTwo { Id = streamId, FieldX = "x-first", Counter = 1, Version = 1 });
    // Then ModelOne
    await storeOne.UpsertAsync(streamId, new ModelOne { Id = streamId, FieldA = "a-second", FieldB = "b-second", Version = 1 });

    var savedOne = await ctx.Set<PerspectiveRow<ModelOne>>().FirstOrDefaultAsync(r => r.Id == streamId);
    var savedTwo = await ctx.Set<PerspectiveRow<ModelTwo>>().FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(savedOne).IsNotNull();
    await Assert.That(savedTwo).IsNotNull();
    await Assert.That(savedOne!.Data.FieldA).IsEqualTo("a-second");
    await Assert.That(savedTwo!.Data.FieldX).IsEqualTo("x-first");
  }

  [Test]
  public async Task TwoPerspectives_SecondUpsertUpdatesFirst_BothModelsRowsCoExistAsync() {
    // Update path: upsert ModelOne, then upsert ModelOne again (update). ModelTwo's row
    // for the same stream remains untouched.
    await using var ctx = new TwoModelsDbContext(_createInMemoryOptions());
    var storeOne = new EFCorePostgresPerspectiveStore<ModelOne>(ctx, "perspective_one");
    var storeTwo = new EFCorePostgresPerspectiveStore<ModelTwo>(ctx, "perspective_two");
    var streamId = Guid.NewGuid();

    await storeOne.UpsertAsync(streamId, new ModelOne { Id = streamId, FieldA = "v1", FieldB = "old", Version = 1 });
    await storeTwo.UpsertAsync(streamId, new ModelTwo { Id = streamId, FieldX = "two", Counter = 10, Version = 1 });
    await storeOne.UpsertAsync(streamId, new ModelOne { Id = streamId, FieldA = "v2", FieldB = "new", Version = 2 });

    var savedOne = await ctx.Set<PerspectiveRow<ModelOne>>().FirstOrDefaultAsync(r => r.Id == streamId);
    var savedTwo = await ctx.Set<PerspectiveRow<ModelTwo>>().FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(savedOne).IsNotNull();
    await Assert.That(savedOne!.Data.FieldA).IsEqualTo("v2")
      .Because("ModelOne update must overwrite the previous row.");
    await Assert.That(savedTwo).IsNotNull()
      .Because("ModelTwo's row for the same stream must not be deleted by ModelOne's update.");
    await Assert.That(savedTwo!.Data.FieldX).IsEqualTo("two");
  }
}
